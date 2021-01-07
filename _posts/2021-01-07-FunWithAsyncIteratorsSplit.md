---
classes: wide
category: Coding
tags:
 - C#
 - Async iterators
share: false
related: false
title:  "Fun with C# async iterators: The Split operator"
---

I recently had the odd but totally reasonable requirement to split a single C# async iterator between its consumers. If you're not familiar with async iterators in C#, I'd recommend you read [this article from the November 2019 issue of MSDN magazine](https://docs.microsoft.com/en-us/archive/msdn-magazine/2019/november/csharp-iterating-with-async-enumerables-in-csharp-8). I'll wait.

So now, what do I mean by 'splitting an async iterator between its consumers'? Let's assume we have an instance of `IAsyncEnumerable<int>` that'll yield the numbers between 1 and 10:

```cs
var source = AsyncEnumerable.Range(1, 10);
```

A 'consumer' of `source` now is just some piece of code that has an iteration going on, i.e. it, at the very minimum, called `GetAsyncEnumerator` on `source` to get an `IAsyncEnumerator<int>` and hasn't disposed of it yet. 'Splitting' `source` means applying an operator `Split` (that is yet to be written)

```cs
var split = source.Split();
```

such that the resulting `IAsyncEnumerable<int>` satisfies a couple of properties:

1. Independent of the number of concurrent consumers of `split`, `source` will only ever be iterated by mostly one consumer.
2. Consumers of `split` may try to consume any number of elements from it. More specifically, it is valid to finish iterating `split` at any time.
3. Any element that `source` produces can be consumed only by excatly one consumer of `split`.
4. When the last consumer of `split` is finshed iterating, the single consumer of `source` is disposed. Any next consumer of `split` will create a new single consumer on `source`.

In other terms, `Split` is a way of sharing an iterator between multiple concurrent consumers such that consumers take turns in consuming. For example, given `source` and `split` as defined above, concurrently running the consumers like this

```cs
var task1 = Task.Run(async () => await split.ToArrayAsync());
var task2 = Task.Run(async () => await split.ToArrayAsync());
var task3 = Task.Run(async () => await split.ToArrayAsync());

var array1 = await task1;
var array2 = await task2;
var array3 = await task3;
```

may end up with `array1 = {1, 4, 7, 10}`, `array2 = {2, 5, 8}` and `array3 = {3, 6, 9}`.

The code for the operator I present here is lock free, i.e. it should perform well in concurrent scenarios
since it doesn't ever block any threads. It is hopefully sufficiently commented too.

```cs
{% include_relative AsyncEnumerableSplit.cs %}
```

Some tests are in order:

```cs
{% include_relative AsyncEnumerableSplitTests.cs %}
```

Happy coding!