using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.RateLimiting;

namespace KalTerm.Kalshi.Tests.RateLimiting;

public class KalshiRateLimitsTests
{
    [Fact]
    public void TheReadBucketUsesTheMargin()
    {
        using var bucket = KalshiRateLimits.ReadBucket(KalshiSettings.Default);

        Assert.Equal(180m, bucket.TokensPerSecond);   // 200 x 0.9
        Assert.Equal(180m, bucket.Capacity);          // one second of budget
    }

    [Fact]
    public void TheReadBucketFollowsTheConfiguredBudget()
    {
        using var bucket = KalshiRateLimits.ReadBucket(KalshiSettings.Create(readTokensPerSecond: 600));

        Assert.Equal(540m, bucket.TokensPerSecond);
    }

    [Fact]
    public void TheWriteBucketIsIndependentAndUsesTheMargin()
    {
        using var read = KalshiRateLimits.ReadBucket(KalshiSettings.Default);
        using var write = KalshiRateLimits.WriteBucket(KalshiSettings.Default);

        Assert.Equal(90m, write.TokensPerSecond);     // 100 x 0.9
        Assert.Equal(90m, write.Capacity);
        Assert.NotSame(read, write);
    }

    [Fact]
    public async Task ABucketMadeFromSettingsPacesRequests()
    {
        var time = new AutoAdvanceTimeProvider();
        using var bucket = KalshiRateLimits.ReadBucket(KalshiSettings.Default, time);

        for (var i = 0; i < 18; i++)
        {
            await bucket.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);   // 18 x 10 = the whole 180 budget
        }

        Assert.Empty(time.Delays);

        await bucket.AcquireAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMilliseconds(56), Assert.Single(time.Delays));   // 10 tokens at 180/s, rounded up to a millisecond
    }

    [Fact]
    public void NullSettingsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => KalshiRateLimits.ReadBucket(null!));
        Assert.Throws<ArgumentNullException>(() => KalshiRateLimits.WriteBucket(null!));
    }
}
