using KalTerm.Core.Clock;

namespace KalTerm.Core.Tests.Clock;

/// <summary>The reference's own clock tests (kalshi-core/tests/test_clock.py), ported with exact arithmetic.</summary>
public class ClockSkewEstimatorTests
{
    private const long Epoch = 1_700_000_000;

    /// <summary>Samples against a server whose clock is <paramref name="offsetSeconds"/> behind ours (local - server).</summary>
    private static List<ClockSample> Simulate(decimal offsetSeconds, IEnumerable<decimal> phases, decimal rtt = 0.05m)
    {
        var samples = new List<ClockSample>();
        foreach (var phase in phases)
        {
            var sent = Epoch + phase;
            var received = sent + rtt;
            var serverAtStamp = ((sent + received) / 2) - offsetSeconds;
            samples.Add(new ClockSample(At(sent), At(received), At(decimal.Floor(serverAtStamp))));   // Date truncates
        }

        return samples;
    }

    private static DateTimeOffset At(decimal epochSeconds) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)(epochSeconds * TimeSpan.TicksPerSecond));

    private static TimeSpan Seconds(decimal value) => TimeSpan.FromTicks((long)(value * TimeSpan.TicksPerSecond));

    [Theory]
    [InlineData("0.0")]
    [InlineData("0.3")]
    [InlineData("-0.8")]
    [InlineData("3.7")]
    [InlineData("-4.2")]
    [InlineData("60.0")]
    public void TheIntervalAlwaysContainsTheTrueOffset(string offsetText)
    {
        var offset = decimal.Parse(offsetText, CultureInfo.InvariantCulture);
        var random = new Random(7);

        for (var round = 0; round < 50; round++)
        {
            var phases = Enumerable.Range(0, 5).Select(_ => (decimal)random.NextDouble() * 10m).Order().ToList();

            var skew = ClockSkewEstimator.Estimate(Simulate(offset, phases));

            Assert.True(skew.Consistent);
            Assert.InRange(Seconds(offset), skew.Low, skew.High);
        }
    }

    [Fact]
    public void MoreSamplesAcrossSecondBoundariesNarrowTheInterval()
    {
        var one = ClockSkewEstimator.Estimate(Simulate(0.3m, [0.1m]));
        var many = ClockSkewEstimator.Estimate(Simulate(0.3m, [0.0m, 0.25m, 0.5m, 0.75m, 1.0m]));

        Assert.True(many.Uncertainty < one.Uncertainty);
        Assert.InRange(one.Uncertainty.TotalSeconds, 0.515, 0.535);   // about (1 + rtt) / 2
        Assert.True(many.Uncertainty < Seconds(0.2m));
    }

    [Theory]
    [InlineData("0.2", ClockSkewStatus.Ok)]
    [InlineData("-0.9", ClockSkewStatus.Ok)]
    [InlineData("3.7", ClockSkewStatus.Skewed)]
    [InlineData("-4.2", ClockSkewStatus.Skewed)]
    [InlineData("60.0", ClockSkewStatus.Skewed)]
    public void StatusAgainstTheThreshold(string offsetText, ClockSkewStatus expected)
    {
        var offset = decimal.Parse(offsetText, CultureInfo.InvariantCulture);

        var skew = ClockSkewEstimator.Estimate(Simulate(offset, [0.0m, 0.25m, 0.5m, 0.75m, 1.0m]), TimeSpan.FromSeconds(2));

        Assert.Equal(expected, skew.Status);
    }

    [Fact]
    public void NearTheThresholdIsUncertainNotGuessed()
    {
        var skew = ClockSkewEstimator.Estimate(Simulate(2.0m, [0.1m]), TimeSpan.FromSeconds(2));

        Assert.Equal(ClockSkewStatus.Uncertain, skew.Status);
    }

    [Fact]
    public void InconsistentSamplesFallBackAndAreFlagged()
    {
        var good = Simulate(0.0m, [0.1m])[0];
        var stale = new ClockSample(good.Sent.AddSeconds(1), good.Received.AddSeconds(1), good.ServerTime.AddSeconds(-10));   // e.g. a cached reply

        var skew = ClockSkewEstimator.Estimate([good, stale]);

        Assert.False(skew.Consistent);
        Assert.Contains("low confidence", skew.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFallbackUsesTheMedianOfTheIntervalMidpoints()
    {
        // Three disagreeing samples whose interval midpoints are 0, 10 and 30 seconds: the median is 10.
        var samples = new[] { SampleWithMidpoint(0), SampleWithMidpoint(10), SampleWithMidpoint(30) };

        var skew = ClockSkewEstimator.Estimate(samples);

        Assert.False(skew.Consistent);
        Assert.Equal(TimeSpan.FromSeconds(10), skew.Offset);
        Assert.Equal(TimeSpan.FromSeconds(0.5), skew.Uncertainty);   // the widest half-width: (1 s window + 0 rtt) / 2
    }

    [Fact]
    public void WithAnEvenNumberOfDisagreeingSamplesTheMedianIsTheMiddlePairsMean()
    {
        var samples = new[] { SampleWithMidpoint(0), SampleWithMidpoint(10), SampleWithMidpoint(30), SampleWithMidpoint(100) };

        var skew = ClockSkewEstimator.Estimate(samples);

        Assert.False(skew.Consistent);
        Assert.Equal(TimeSpan.FromSeconds(20), skew.Offset);   // (10 + 30) / 2
    }

    [Fact]
    public void NoSamplesIsAnError()
    {
        Assert.Throws<ClockCheckException>(() => ClockSkewEstimator.Estimate([]));
    }

    [Fact]
    public void ASampleReceivedBeforeItWasSentIsRefused()
    {
        var sample = new ClockSample(At(Epoch + 1), At(Epoch), At(Epoch));

        Assert.Throws<ArgumentException>(() => ClockSkewEstimator.Estimate([sample]));
        Assert.Throws<ArgumentNullException>(() => ClockSkewEstimator.Estimate(null!));
    }

    [Fact]
    public void TheDefaultThresholdIsTwoSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ClockSkewEstimator.DefaultThreshold);
        Assert.Equal(ClockSkewEstimator.DefaultThreshold, ClockSkewEstimator.Estimate(Simulate(0m, [0.1m])).Threshold);
    }

    [Fact]
    public void DescribeReadsNaturally()
    {
        var ahead = new ClockSkew(Seconds(3.0m), Seconds(0.2m), Seconds(2.8m), Seconds(3.2m), 5, Consistent: true, Seconds(2.0m)).Describe();
        var behind = new ClockSkew(Seconds(-1.0m), Seconds(0.2m), Seconds(-1.2m), Seconds(-0.8m), 5, Consistent: true, Seconds(2.0m)).Describe();

        Assert.Contains("3.00s ahead of Kalshi", ahead, StringComparison.Ordinal);
        Assert.EndsWith("skewed", ahead, StringComparison.Ordinal);
        Assert.Contains("1.00s behind Kalshi", behind, StringComparison.Ordinal);
        Assert.EndsWith("ok", behind, StringComparison.Ordinal);
        Assert.Equal("local clock is 3.00s ahead of Kalshi (±0.20s, 5 samples): skewed", ahead);
    }

    [Fact]
    public void ZeroOffsetCountsAsAhead()
    {
        var skew = new ClockSkew(TimeSpan.Zero, Seconds(0.5m), Seconds(-0.5m), Seconds(0.5m), 1, Consistent: true, Seconds(2.0m));

        Assert.StartsWith("local clock is 0.00s ahead of Kalshi", skew.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeRoundsHalfToEvenOnTheExactValue()
    {
        var tie = new ClockSkew(Seconds(0.125m), Seconds(0.375m), Seconds(-0.25m), Seconds(0.5m), 2, Consistent: true, Seconds(2.0m));

        Assert.Equal("local clock is 0.12s ahead of Kalshi (±0.38s, 2 samples): ok", tie.Describe());   // 0.125 -> 0.12, 0.375 -> 0.38
    }

    [Theory]
    [InlineData("2.0", "3.0", ClockSkewStatus.Uncertain)]    // low == threshold is not beyond it
    [InlineData("2.000001", "3.0", ClockSkewStatus.Skewed)]
    [InlineData("-1.0", "2.0", ClockSkewStatus.Ok)]          // high == threshold is still within it
    [InlineData("-1.0", "2.000001", ClockSkewStatus.Uncertain)]
    [InlineData("-3.0", "-2.000001", ClockSkewStatus.Skewed)]
    [InlineData("-3.0", "-2.0", ClockSkewStatus.Uncertain)]
    public void StatusBoundariesAreExactToTheTick(string low, string high, ClockSkewStatus expected)
    {
        var lo = Seconds(decimal.Parse(low, CultureInfo.InvariantCulture));
        var hi = Seconds(decimal.Parse(high, CultureInfo.InvariantCulture));

        var skew = new ClockSkew((lo + hi) / 2, (hi - lo) / 2, lo, hi, 1, Consistent: true, TimeSpan.FromSeconds(2));

        Assert.Equal(expected, skew.Status);
    }

    /// <summary>A sample whose interval for the offset is [midpoint - 0.5 s, midpoint + 0.5 s] (zero round trip).</summary>
    private static ClockSample SampleWithMidpoint(int midpointSeconds)
    {
        // interval = [sent - server - 1 s, received - server]; with sent == received == t and server = t - mid - 0.5 s ... use whole numbers:
        // choose server = Epoch, sent = received = Epoch + midpoint + 0.5 s -> [midpoint - 0.5 s, midpoint + 0.5 s].
        var sent = At(Epoch).AddSeconds(midpointSeconds).AddMilliseconds(500);
        return new ClockSample(sent, sent, At(Epoch));
    }
}
