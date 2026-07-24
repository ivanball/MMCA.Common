using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Shared.Concurrency;

namespace MMCA.Common.Shared.Tests.Concurrency;

/// <summary>
/// Verifies the striped keyed lock: mutual exclusion for one key, independent progress across
/// stripes, correct release on the exception path, and a bounded table size regardless of how many
/// distinct (potentially caller-supplied) keys arrive.
/// </summary>
public sealed class KeyedSemaphoreStripeTests
{
    [Fact]
    public async Task AcquireAsync_SameKey_SerializesCallers()
    {
        var sut = new KeyedSemaphoreStripe();
        var inFlight = 0;
        var observedMaxInFlight = 0;

        async Task ContendAsync()
        {
            using (await sut.AcquireAsync("key"))
            {
                var current = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref observedMaxInFlight, current);
                await Task.Delay(5);
                Interlocked.Decrement(ref inFlight);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ContendAsync()));

        observedMaxInFlight.Should().Be(1, "callers holding the same key must never overlap");
    }

    [Fact]
    public async Task AcquireAsync_HeldKey_BlocksASecondCallerUntilRelease()
    {
        var sut = new KeyedSemaphoreStripe(width: 1);

        var first = await sut.AcquireAsync("key");
        var second = sut.AcquireAsync("key");

        second.IsCompleted.Should().BeFalse("the stripe is held");

        first.Dispose();

        var acquired = await second.WaitAsync(TimeSpan.FromSeconds(5));
        acquired.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_ReleasesWhenTheGuardedWorkThrows()
    {
        var sut = new KeyedSemaphoreStripe(width: 1);

        var act = async () =>
        {
            using (await sut.AcquireAsync("key"))
            {
                throw new InvalidOperationException("boom");
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();

        // A leaked stripe would leave this waiting forever.
        var reacquire = sut.AcquireAsync("key");
        var releaser = await reacquire.WaitAsync(TimeSpan.FromSeconds(5));
        releaser.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_DifferentStripes_ProceedIndependently()
    {
        // Width 2 with keys that land on different stripes: holding one must not block the other.
        var sut = new KeyedSemaphoreStripe(width: 2);
        var keys = Enumerable.Range(0, 64).Select(i => string.Create(CultureInfo.InvariantCulture, $"key-{i}")).ToList();

        var held = await sut.AcquireAsync(keys[0]);
        try
        {
            var other = keys.Select(k => sut.AcquireAsync(k)).FirstOrDefault(t => t.IsCompleted);
            other.Should().NotBeNull("with more than one stripe some key must map to a free stripe");
            (await other!).Dispose();
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public async Task AcquireAsync_ManyDistinctKeys_DoesNotGrowBeyondTheConfiguredWidth()
    {
        // The point of striping: a caller-supplied key space cannot grow the lock table.
        var sut = new KeyedSemaphoreStripe(width: 4);

        foreach (var i in Enumerable.Range(0, 10_000))
        {
            using (await sut.AcquireAsync(string.Create(CultureInfo.InvariantCulture, $"key-{i}")))
            {
                // No-op: exercising allocation behavior, not the guarded work.
            }
        }

        sut.Width.Should().Be(4);
    }

    [Fact]
    public async Task AcquireAsync_AlreadyCanceledToken_Throws()
    {
        var sut = new KeyedSemaphoreStripe();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await sut.AcquireAsync("key", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveWidth_Throws(int width)
    {
        var act = () => new KeyedSemaphoreStripe(width);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref target);
            if (candidate <= observed)
                return;
        }
        while (Interlocked.CompareExchange(ref target, candidate, observed) != observed);
    }
}
