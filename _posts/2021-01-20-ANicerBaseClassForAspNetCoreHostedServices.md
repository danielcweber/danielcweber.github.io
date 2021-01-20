---
classes: wide
category: Coding
tags:
 - C#
 - ASP.NET Core
share: false
related: false
title:  "A nicer base class for ASP.NET Core hosted services"
---

ASP.NET allows developers to define custom tasks whose execution and lifetime is automatically handled by the host application. These tasks are represented by any registered implementation of the [IHostedService interface](https://docs.microsoft.com/de-de/dotnet/architecture/microservices/multi-container-microservice-net-applications/background-tasks-with-ihostedservice) and there may be any number of registrations for this particular interface. However, the signature of `IHostedService` might not appear so straightforward to implement at first. Let's have a look:

```cs
public interface IHostedService
{
    Task StartAsync (CancellationToken cancellationToken);
    Task StopAsync (CancellationToken cancellationToken);
}
```

We're given separate methods for starting and stopping the execution, each taking a `CancellationToken`. What's their purpose? According to the docs, the `CancellationToken` that goes into `StartAsync` [indicates that the start process has been aborted](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.ihostedservice.startasync?view=dotnet-plat-ext-5.0#Microsoft_Extensions_Hosting_IHostedService_StartAsync_System_Threading_CancellationToken_) while the one that goes into `StopAsync` [indicates that the shutdown process should no longer be graceful](https://docs.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.ihostedservice.stopasync?view=dotnet-plat-ext-5.0#Microsoft_Extensions_Hosting_IHostedService_StopAsync_System_Threading_CancellationToken_).

So any implementation of `IHostedService` has to deal with seven possible states:
 - Unstarted
 - Starting
 - Starting aborted
 - Started
 - Stopping gracefully
 - Stopping no longer gracefully
 - Stopped

Is this actually necessary? A reasonable abstraction for any asynchronous, cancellable workload is the delegate
```cs
Task ExecuteAsync(CancellationToken ct);
```
which does not distinguish between different stages of 'starting' and 'started' and also has only a single semantic for cancellation, i.e. it doesn't distinguish between graceful and forceful termination. Since this is often all we need, there's a nice [base class called BackgroundService](https://docs.microsoft.com/de-de/dotnet/api/microsoft.extensions.hosting.backgroundservice?view=dotnet-plat-ext-5.0) that exposes an [abstract method named ExecuteAsync](https://docs.microsoft.com/de-de/dotnet/api/microsoft.extensions.hosting.backgroundservice.executeasync?view=dotnet-plat-ext-5.0#Microsoft_Extensions_Hosting_BackgroundService_ExecuteAsync_System_Threading_CancellationToken_) that we can override and do all the work in, avoiding all the hassle of implementing `IHostedService` by ourselves.

It is worth noting that due to [this change in .NET 5](https://github.com/dotnet/runtime/commit/a432e94f9e2aaa12c35a6b28d672852a236b8097#diff-8e158e3e5e987479cd4a19c86ec2b668f496a95d8839e84ab0c9a483aef9dcb6R32), startup cancellation and service shutdown are now both observed on the `CancellationToken` passed to `ExecuteAsync`. However, as stated above, we now lose the ability to distinguish between graceful and forceful shutdown. The `BackgroundService` class itself doesn't do much with the `CancellationToken` passed into `StopAsync` other than asynchronously throw a `TaskCanceledException` when it gets cancelled before the actual execution has completed. This might be just fine for tasks that try their best to cancel all operations and finish as soon as possible. However, it wasn't enough for my use case, which I will get into in a minute. First, I'll introduce a base class that'll allow us to distinguish between two different `CancellationTokens`:

```cs
public abstract class HostedServiceBase : IHostedService
{
    private sealed class RunHandle
    {
        private readonly Task _runTask;
        private readonly CancellationTokenSource _gracefulCts;
        private readonly CancellationTokenSource _forcefulCts = new();

        public RunHandle(HostedServiceBase service, CancellationToken startupCancellationToken)
        {
            _gracefulCts = CancellationTokenSource.CreateLinkedTokenSource(startupCancellationToken);
            _runTask = service.ExecuteAsync(_gracefulCts.Token, _forcefulCts.Token);
        }
        
        public async Task DisposeAsync(CancellationToken forcefulCancellationToken)
        {
            _gracefulCts.Cancel();

            await using (forcefulCancellationToken.Register(() => _forcefulCts.Cancel()))
            {
                try
                {
                    await _runTask;
                }
                catch (OperationCanceledException) { }
            }
        }
    }
    
    private RunHandle? _currentRunHandle;

    public async Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _currentRunHandle, new RunHandle(this, ct), null) != null)
            throw new InvalidOperationException($"{nameof(StartAsync)} can't be called twice.");
    }
    
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Interlocked.Exchange(ref _currentRunHandle, null)?.DisposeAsync(cancellationToken) ?? Task.CompletedTask;
    }
    
    protected abstract Task ExecuteAsync(CancellationToken gracefulCancellationToken, CancellationToken forcefulCancellationToken);
}
```

It's implemented a little more defensively than the original [BackgroundService class](https://github.com/dotnet/runtime/blob/e18d25e1a81d359097371615ff1a3407597c0bb3/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/BackgroundService.cs) with respect to threading and the mutable `_currentRunHandle` field but that's just how I code. As in the original implementation, the startup `CancellationToken` is combined with a token that signals general shutdown.

`ExecuteAsync` now gets two separate tokens. But why would anybody need that? My use case was as follows: On startup, I need to subscribe to an instance of `IObservable<T>` for some type T. (If you're not familiar with what an `IObservable<T>`, it's basically a reactive stream of elements that can be subscribed to and that'll push new elements onto you whenever there is one. For introductions on Reactive Extensions checkout [this](https://github.com/dotnet/reactive) and [this](http://introtorx.com/). I love and use Rx a lot!). For each new element, I do some processing that may take time - up to several minutes. Graceful termination in this context means that no new elements should be observed, but elements currently being processed should have a way of finishing gracefully. But on the other side, I still need a way to observe forceful termination to play along nicely with SIGTERM and SIGKILL signals in Linux Docker containers. So I'll end up with something like this:

```cs
public sealed class MyHostedService : HostedServiceBase
{
    private readonly IObservable<MyDataType> _observable;
    
    public TelephonyServiceClientHostedService(IObservable<MyDataType> observable)
    {
        _observable = observable;
    }

    protected override Task ExecuteAsync(CancellationToken graceful, CancellationToken forced)
    {
        return _observable
            // Stop observing new elements on graceful shutdown. This will unsibscribe from _observable...
            .TakeUntil(Observable.Create<Unit>(observer => graceful.Register(() => observer.OnNext(Unit.Default))))
            // ...but long running operations in SelectMany will still
            .SelectMany(async (element, ct) =>
            {
                //Do some possibly long running work on element...
            })
            // Stop observing anything from this whole pipeline as soon as forceful shutdown is triggered.
            .TakeUntil(Observable.Create<Unit>(observer => forced.Register(() => observer.OnNext(Unit.Default))))
            .LastOrDefaultAsync()
            // It's important that we don't pass the 'graceful' CancellationToken to ToTask. We might choose to
            // pass 'forced' however.
            .ToTask(default(CancellationToken));
    }
}
```

Happy coding.


