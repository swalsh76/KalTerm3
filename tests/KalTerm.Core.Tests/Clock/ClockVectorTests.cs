using System.Text.Json;
using KalTerm.Core.Clock;

namespace KalTerm.Core.Tests.Clock;

/// <summary>
/// Differential tests against the Python reference (clock.json). The reference computes in floats, which at today's
/// epoch carry about 0.2 microseconds of noise; the port is exact in 100 ns ticks, so values may differ by a little.
/// </summary>
public class ClockVectorTests
{
    private const long ToleranceTicks = 20;   // 2 microseconds

    public static TheoryData<string> EstimateNames()
    {
        var data = new TheoryData<string>();
        foreach (var e in VectorFile.Load("clock.json").GetProperty("estimates").EnumerateArray())
        {
            data.Add(e.GetProperty("name").GetString()!);
        }

        return data;
    }

    public static TheoryData<int> DescribeIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < VectorFile.Load("clock.json").GetProperty("describe").GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    public static TheoryData<int> HttpDateIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < VectorFile.Load("clock.json").GetProperty("httpDates").GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EstimateNames))]
    public void EstimatesMatchTheReference(string name)
    {
        var entry = VectorFile.Load("clock.json").GetProperty("estimates").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == name);
        var samples = entry.GetProperty("samples").EnumerateArray()
            .Select(s => new ClockSample(Instant(s, "sent"), Instant(s, "received"), Instant(s, "server")))
            .ToList();

        var skew = ClockSkewEstimator.Estimate(samples, Span(entry, "threshold"));

        AssertClose(Span(entry, "offset"), skew.Offset, "offset");
        AssertClose(Span(entry, "uncertainty"), skew.Uncertainty, "uncertainty");
        AssertClose(Span(entry, "low"), skew.Low, "low");
        AssertClose(Span(entry, "high"), skew.High, "high");
        Assert.Equal(entry.GetProperty("consistent").GetBoolean(), skew.Consistent);
        Assert.Equal(samples.Count, skew.Samples);
        Assert.Equal(entry.GetProperty("status").GetString(), skew.Status.ToString(), ignoreCase: true);
        Assert.Equal(entry.GetProperty("describe").GetString(), skew.Describe());
    }

    [Theory]
    [MemberData(nameof(DescribeIndexes))]
    public void DescribeMatchesTheReference(int index)
    {
        var entry = VectorFile.Load("clock.json").GetProperty("describe")[index];
        var skew = new ClockSkew(
            Span(entry, "offset"), Span(entry, "uncertainty"), Span(entry, "low"), Span(entry, "high"),
            entry.GetProperty("samples").GetInt32(), entry.GetProperty("consistent").GetBoolean(), Span(entry, "threshold"));

        Assert.Equal(entry.GetProperty("status").GetString(), skew.Status.ToString(), ignoreCase: true);
        Assert.Equal(entry.GetProperty("text").GetString(), skew.Describe());
    }

    [Theory]
    [MemberData(nameof(HttpDateIndexes))]
    public void HttpDatesMatchTheReferenceExceptWhereThePortIsStricter(int index)
    {
        var entry = VectorFile.Load("clock.json").GetProperty("httpDates")[index];
        var text = entry.GetProperty("text").GetString()!;
        var expected = entry.TryGetProperty("csharp", out var strict) ? strict.GetString()! : entry.GetProperty("expect").GetString()!;

        if (expected == "error")
        {
            Assert.False(HttpDate.TryParse(text, out _), $"'{text}' should be refused");
            Assert.Throws<ClockCheckException>(() => HttpDate.Parse(text));
            return;
        }

        var seconds = decimal.Parse(expected, NumberStyles.Float, CultureInfo.InvariantCulture);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond)), HttpDate.Parse(text));
    }

    [Fact]
    public void TheNoSamplesErrorMatchesTheReference()
    {
        var errors = VectorFile.Load("clock.json").GetProperty("errors");

        Assert.Equal("no-samples", errors[0].GetProperty("name").GetString());
        Assert.Throws<ClockCheckException>(() => ClockSkewEstimator.Estimate([]));
    }

    [Fact]
    public void TheVectorFileHasTheExpectedBreadth()
    {
        var file = VectorFile.Load("clock.json");
        var statuses = file.GetProperty("estimates").EnumerateArray().Select(e => e.GetProperty("status").GetString()).Distinct().Order().ToArray();

        Assert.True(file.GetProperty("estimates").GetArrayLength() >= 200);
        Assert.Equal(["ok", "skewed", "uncertain"], statuses);
        Assert.Contains(file.GetProperty("estimates").EnumerateArray(), e => !e.GetProperty("consistent").GetBoolean());
        Assert.Matches("^[0-9a-f]{40}$", file.GetProperty("referenceCommit").GetString());
    }

    private static void AssertClose(TimeSpan expected, TimeSpan actual, string what) =>
        Assert.True(
            Math.Abs((expected - actual).Ticks) <= ToleranceTicks,
            $"{what}: reference {expected.TotalSeconds:R} s, port {actual.TotalSeconds:R} s");

    private static TimeSpan Span(JsonElement element, string property) =>
        TimeSpan.FromTicks(ToTicks(element.GetProperty(property).GetString()!));

    private static DateTimeOffset Instant(JsonElement element, string property) =>
        DateTimeOffset.UnixEpoch.AddTicks(ToTicks(element.GetProperty(property).GetString()!));

    private static long ToTicks(string seconds) =>
        (long)decimal.Round(decimal.Parse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond);
}
