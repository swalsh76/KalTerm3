using System.Threading.Channels;
using KalTerm.Core.RateLimiting;

namespace KalTerm.Core.Tests.RateLimiting;

/// <summary>The reference's own rate-limit tests (kalshi-core/tests/test_ratelimit.py), plus the .NET-specific ones.</summary>
public class TokenBucketTests
{
    private static TokenBucket Bucket(AutoAdvanceTimeProvider time, decimal rate = 100, decimal capacity = 100) =>
        new(rate, capacity, time);

    [Fact]
    public async Task BurstUpToCapacityIsImmediate()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = Bucket(time);
        for (var i = 0; i < 10; i++)
        {
            await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);
        }

        Assert.Empty(time.Delays);
        Assert.Equal(TimeSpan.Zero, time.Elapsed);
    }

    [Fact]
    public async Task WaitsForRefillWhenEmpty()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = Bucket(time);
        for (var i = 0; i < 10; i++)
        {
            await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);
        }

        await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromTicks(1_000_000), time.Elapsed);   // 10 tokens at 100/s = 0.1 s, exactly
    }

    [Fact]
    public async Task SustainedThroughputMatchesTheRate()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = Bucket(time);
        for (var i = 0; i < 100; i++)
        {
            await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);   // 1,000 tokens
        }

        // 100 tokens come free from the initial bucket; the other 900 refill at 100/s.
        Assert.Equal(TimeSpan.FromSeconds(9), time.Elapsed);
    }

    [Fact]
    public async Task RefillIsCappedAtCapacity()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = Bucket(time);
        await bucket.AcquireAsync(100, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(60));   // a long idle; the bucket must not exceed its capacity

        await bucket.AcquireAsync(100, TestContext.Current.CancellationToken);
        Assert.Empty(time.Delays);

        await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromTicks(601_000_000), time.Elapsed);   // 60 s idle plus 0.1 s
    }

    [Theory]
    [InlineData(101)]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task CostMustBeWithinZeroAndCapacity(int cost)
    {
        using var bucket = Bucket(new AutoAdvanceTimeProvider());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await bucket.AcquireAsync(cost, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    public void RateAndCapacityMustBePositive(int rate, int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(rate, capacity));
    }

    [Fact]
    public async Task WaitersAreServedInArrivalOrder()
    {
        var time = new AutoAdvanceTimeProvider(autoFire: false);   // the test fires each wait, one at a time
        using var bucket = Bucket(time, rate: 10, capacity: 10);
        await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);

        var completed = Channel.CreateUnbounded<int>();
        var workers = Enumerable.Range(0, 5).Select(async i =>
        {
            await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);
            completed.Writer.TryWrite(i);
        }).ToArray();   // started in order: each reaches the queue before the next begins

        var order = new List<int>();
        for (var step = 0; step < 5; step++)
        {
            // Exactly one waiter holds the bucket and is waiting for tokens; the rest are queued behind it.
            Assert.True(SpinWait.SpinUntil(() => time.PendingTimers == 1, TimeSpan.FromSeconds(10)));
            time.FireNext();
            order.Add(await completed.Reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(workers);

        Assert.Equal([0, 1, 2, 3, 4], order);
        Assert.Equal(TimeSpan.FromSeconds(5), time.Elapsed);
    }

    [Fact]
    public async Task AWaitIsRoundedUpToAWholeMillisecond()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = new TokenBucket(180, 180, time);   // 200 tokens/s x a 0.9 safety margin
        await bucket.AcquireAsync(180, TestContext.Current.CancellationToken);

        await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);

        // 10 / 180 s = 55.555... ms: Task.Delay truncates to milliseconds, so the wait is rounded up to 56 ms.
        Assert.Equal(TimeSpan.FromMilliseconds(56), Assert.Single(time.Delays));
        Assert.Equal(TimeSpan.FromMilliseconds(56), time.Elapsed);
    }

    [Fact]
    public async Task ASubMillisecondDeficitStillWaitsAMillisecond()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = new TokenBucket(1_000_000, 100, time);   // 10 tokens refill in 10 microseconds
        await bucket.AcquireAsync(100, TestContext.Current.CancellationToken);

        await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMilliseconds(1), Assert.Single(time.Delays));   // never zero, which would spin
    }

    [Fact]
    public async Task TheSurplusFromRoundingStaysInTheBucket()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = new TokenBucket(180, 180, time);
        await bucket.AcquireAsync(180, TestContext.Current.CancellationToken);

        // 18 acquires of 10 tokens = 180 tokens = exactly one second at 180/s. Rounding each wait up would add
        // up to 18 ms if the surplus were lost; kept, the total stays within one rounding step of the exact second.
        for (var i = 0; i < 18; i++)
        {
            await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);
        }

        Assert.InRange(time.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ACancelledWaitSpendsNothingAndTheNextCallerStillGetsItsTokens()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = Bucket(time, rate: 10, capacity: 10);
        await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await bucket.AcquireAsync(10, cancelled.Token));

        var before = time.Elapsed;
        await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal(before + TimeSpan.FromSeconds(1), time.Elapsed);   // a full second for 10 tokens at 10/s
    }

    [Fact]
    public async Task AClockThatGoesBackwardsAddsNothing()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = Bucket(time);
        await bucket.AcquireAsync(100, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(-30));

        await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);

        // No tokens are added by the jump back; the bucket carries on from the new time and waits 0.1 s.
        Assert.Equal(TimeSpan.FromSeconds(-30) + TimeSpan.FromTicks(1_000_000), time.Elapsed);
    }

    [Theory]
    [InlineData("0.0000001", "10")]
    [InlineData("10", "0.0000001")]
    public void MoreThanSixDecimalPlacesIsRefused(string rate, string capacity)
    {
        Assert.Throws<ArgumentException>(() => new TokenBucket(decimal.Parse(rate, CultureInfo.InvariantCulture), decimal.Parse(capacity, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task ACostFinerThanSixDecimalPlacesIsRefused()
    {
        using var bucket = Bucket(new AutoAdvanceTimeProvider());

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await bucket.AcquireAsync(0.0000001m, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExposesItsConfiguration()
    {
        using var bucket = new TokenBucket(180, 200);

        Assert.Equal(180m, bucket.TokensPerSecond);
        Assert.Equal(200m, bucket.Capacity);
        Assert.Equal(10m, TokenBucket.DefaultRequestCost);
    }

    [Fact]
    public async Task TheDefaultCostIsTen()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = Bucket(time, rate: 10, capacity: 10);

        await bucket.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);
        await bucket.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(1), time.Elapsed);
    }

    [Fact]
    public async Task WorksWithTheRealClock()
    {
        using var bucket = new TokenBucket(1000, 1000);   // never has to wait

        await bucket.AcquireAsync(10, TestContext.Current.CancellationToken);
        await bucket.AcquireAsync(990, TestContext.Current.CancellationToken);
    }
}
