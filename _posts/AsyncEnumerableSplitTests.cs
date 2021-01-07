using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace BlogCodeTests
{
    public class AsyncEnumerableSplitTests
    {
        [Fact]
        public async Task Split_once()
        {
            var count = 100;
            var parallelism = 10;

            var source = AsyncEnumerable.Create(Range);
            var split = source.Split();

            //Range but with a bit of delay between elements
            async IAsyncEnumerator<int> Range(CancellationToken ct)
            {
                for (var i = 0; i < count; i++)
                {
                    yield return i;

                    await Task.Delay(5, ct);
                }
            }

            // Concurrently iterate `split` into buckets.
            var buckets = await Enumerable
                .Repeat(split, parallelism)
                .ToObservable()
                .SelectMany(_ => _
                    .ToObservable()
                    .ToArray())
                .ToArray()
                .ToTask();

            // There's as many buckets as parallelism says.
            buckets
                .Should()
                .HaveCount(parallelism);

            // There's at least one element in any bucket.
            buckets
                .All(x => x.Length > 0)
                .Should()
                .BeTrue();

            // All buckets sizes add up properly.
            buckets
                .Sum(x => x.Length)
                .Should()
                .Be(count);

            // All the elements combined without duplicates
            // are source's output!
            buckets
                .SelectMany(x => x)
                .Distinct()
                .Should()
                .HaveCount(count);
        }
        
        [Fact]
        public async Task Split_twice()
        {
            //Assert that after a complete iteration of source,
            //it can be done again!

            var count = 10;
            var parallelism = 10;

            var source = AsyncEnumerable.Create(Range);
            var split = source.Split();

            //Range but with a bit of delay between elements
            async IAsyncEnumerator<int> Range(CancellationToken ct)
            {
                for (var i = 0; i < count; i++)
                {
                    yield return i;

                    await Task.Delay(10, ct);
                }
            }

            var buckets1 = await Enumerable
                .Repeat(split, parallelism)
                .ToObservable()
                .SelectMany(_ => _
                    .ToObservable()
                    .ToArray())
                .ToArray()
                .ToTask();

            var buckets2 = await Enumerable
                .Repeat(split, parallelism)
                .ToObservable()
                .SelectMany(_ => _
                    .ToObservable()
                    .ToArray())
                .ToArray()
                .ToTask();

            buckets1
                .SelectMany(x => x)
                .Should()
                .BeEquivalentTo(buckets2.SelectMany(x => x));
        }
    }
}
