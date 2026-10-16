using KalTerm.Core.Clock;

namespace KalTerm.Core.Tests.Clock;

/// <summary>The header handling the reference's REST client does inline: server time is Date plus Age.</summary>
public class ClockSampleTests
{
    private static readonly DateTimeOffset Sent = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly DateTimeOffset Received = Sent.AddMilliseconds(50);
    private const string Date = "Tue, 14 Nov 2023 22:13:20 GMT";   // 1,700,000,000 s

    [Fact]
    public void TheServerTimeIsTheDateHeader()
    {
        var sample = ClockSample.FromHeaders(Sent, Received, Date, null);

        Assert.Equal(Sent, sample.ServerTime);
        Assert.Equal(Sent, sample.Sent);
        Assert.Equal(Received, sample.Received);
    }

    [Fact]
    public void TheAgeHeaderIsAddedToTheServerTime()
    {
        // A cached reply generated 3 s ago: Date is old, Age says so. Without Age this would look 3 s skewed.
        var staleDate = "Tue, 14 Nov 2023 22:13:17 GMT";   // 3 s before Sent

        var withAge = ClockSample.FromHeaders(Sent, Received, staleDate, "3");
        var withoutAge = ClockSample.FromHeaders(Sent, Received, staleDate, null);
        var threshold = TimeSpan.FromSeconds(1);

        Assert.Equal(Sent, withAge.ServerTime);
        Assert.Equal(ClockSkewStatus.Ok, ClockSkewEstimator.Estimate([withAge], threshold).Status);
        Assert.Equal(ClockSkewStatus.Skewed, ClockSkewEstimator.Estimate([withoutAge], threshold).Status);   // the 3 s would look like skew
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-3")]
    [InlineData("3.5")]
    [InlineData(" 3")]
    [InlineData("3s")]
    public void AnAgeThatIsNotPlainDigitsCountsAsZero(string? age)
    {
        Assert.Equal(Sent, ClockSample.FromHeaders(Sent, Received, Date, age).ServerTime);
    }

    [Fact]
    public void AMissingDateHeaderIsAClearError()
    {
        var error = Assert.Throws<ClockCheckException>(() => ClockSample.FromHeaders(Sent, Received, null, null));

        Assert.Contains("Date", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnparseableDateHeaderIsAClearError()
    {
        Assert.Throws<ClockCheckException>(() => ClockSample.FromHeaders(Sent, Received, "yesterday", null));
    }

    [Fact]
    public void AnAbsurdAgeIsAClearErrorNotAnOverflow()
    {
        Assert.Throws<ClockCheckException>(() => ClockSample.FromHeaders(Sent, Received, Date, "9999999999999999"));
    }

    [Fact]
    public void AnAgeTooLargeForALongCountsAsNotANumber()
    {
        Assert.Equal(Sent, ClockSample.FromHeaders(Sent, Received, Date, "99999999999999999999999").ServerTime);
    }
}
