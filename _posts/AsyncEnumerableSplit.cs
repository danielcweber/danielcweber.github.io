using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public static class AsyncEnumerableExtensions
{
    public static IAsyncEnumerable<T> Split<T>(this IAsyncEnumerable<T> source)
    {
        // Variables shared across all consumers of the split IAsyncEnumerable.

        // With a SemaphoreSlim, we can lock a protected code section
        // without blocking any threads.
        var asyncLock = new SemaphoreSlim(1);
        var currentEnumerator = default(RefCountEnumerator<T>?);

        return Core();
        
        async IAsyncEnumerable<T> Core([EnumeratorCancellation] CancellationToken ct = default)
        {
            var localEnumerator = default(RefCountEnumerator<T>?);

            // Get a valid RefCountEnumerator<T>, which is a wrapper for the source IAsyncEnumerator
            // which tracks the number of concurrent consumers. This is done lock-free, i.e. it doesn't
            // ever block any threads.
            while (localEnumerator == null)
            {
                localEnumerator = Volatile.Read(ref currentEnumerator);

                if (localEnumerator == null)
                {
                    // There's no valid value in currentEnumerator. Now there's a race for creation of a new
                    // RefCountEnumerator<T>. Try to win the race by setting an intermediate sentinel value.
                    if (Interlocked.CompareExchange(ref currentEnumerator, RefCountEnumerator<T>.Sentinel, null) == null)
                    {
                        //Success. This thread may now go ahead and get the source IAsyncEnumerator.
                        //Note: The ct parameter that this iterator gets will not be observed.
                        Interlocked.Exchange(ref currentEnumerator, localEnumerator = new RefCountEnumerator<T>(source));
                    }
                }
                else
                {
                    // There was a currentEnumerator. If it's still usable, Increment will return true.
                    if (!localEnumerator.Increment())
                    {
                        // But if it's not usable anymore, we reset currentEnumerator/localEnumerator to
                        // null and try again.
                        Interlocked.CompareExchange(ref currentEnumerator, null, localEnumerator);
                        localEnumerator = null;
                    }
                }
            }

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    T value;

                    //... enter the protected section. This is the key part
                    // of the Split operator. The SemaphoreSlim makes sure
                    // that only one consumer at any time may call MoveNextAsync
                    // on the source IAsyncEnumerator.
                    await asyncLock.WaitAsync(ct);

                    try
                    {
                        if (!await localEnumerator.MoveNextAsync())
                            yield break;

                        value = localEnumerator.Current;
                    }
                    finally
                    {
                        asyncLock.Release();
                    }

                    yield return value;
                }
            }
            finally
            {
                // Whenever the consumer is done iterating, we end up here.
                // By calling Decrement we signal the RefCountEnumerator to
                // decrement the concurrent consumer count and possibly
                // dispose the source IAsyncEnumerator<T>.
                if (!await localEnumerator.Decrement())
                    Interlocked.CompareExchange(ref currentEnumerator, null, localEnumerator);
            }
        }
    }

    // We need to wrap the source-IAsyncEnumerator's lifecycle
    // because the current number of consumers must be tracked
    // alongside the source-IAsyncEnumerator itself. We can't easily
    // inline this into the Split-operator code itself as it would
    // make the concurrent lock-free part much harder.
    private sealed class RefCountEnumerator<T> : IAsyncEnumerator<T>
    {
        // The Sentinel instance is used to indicate that a particular thread
        // has successfully won the race for source-enumerator creation
        // and may now proceed safely.
        public static readonly RefCountEnumerator<T> Sentinel = new();

        private int _disposed;
        private int _count = 1;
        private readonly IAsyncEnumerator<T> _baseEnumerator;
        private readonly CancellationTokenSource _cts = new();

        private RefCountEnumerator() : this(Dummy())
        {

        }

        public RefCountEnumerator(IAsyncEnumerable<T> source)
        {
            _baseEnumerator = source
                .GetAsyncEnumerator(_cts.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                _cts.Cancel();
                await _baseEnumerator.DisposeAsync();
            }
        }

        // We make sure that the wrapped IAsyncEnumerator's MoveNextAsync
        // method is not called repeatedly after it has returned false.
        // This is not a strong requirement per se but might be a good
        // idea depending of the implementation of the source iterator.
        public async ValueTask<bool> MoveNextAsync()
        {
            var ret = false;

            if (Volatile.Read(ref _disposed) == 0)
            {
                ret = await _baseEnumerator.MoveNextAsync();
                if (!ret)
                    await DisposeAsync();
            }

            return ret;
        }

        // Returns true if the wrapped IAsyncEnumerator<T> is still valid.
        public bool Increment()
        {
            while (true)
            {
                var currentCount = Volatile.Read(ref _count);

                if (currentCount == 0)
                    return false;

                if (Interlocked.CompareExchange(ref _count, currentCount + 1, currentCount) == currentCount)
                    return true;
            }
        }

        // Returns true if the wrapped IAsyncEnumerator<T> is still valid.
        public async ValueTask<bool> Decrement()
        {
            if (Interlocked.Decrement(ref _count) == 0)
            {
                await DisposeAsync();
                
                return false;
            }

            return true;
        }

        public T Current => _baseEnumerator.Current;

        private static async IAsyncEnumerable<T> Dummy()
        {
            yield break;
        }
    }
}