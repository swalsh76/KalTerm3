using System.Globalization;
using KalTerm.Core.Money;

namespace KalTerm.Core.Tests.Money;

/// <summary>Differential tests: every expectation comes from the Python reference (fixedpoint.json).</summary>
public class FixedPointVectorTests
{
    private const string Refuse = "refuse";

    public static TheoryData<string, string, string> ParseCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var c in VectorFile.Load("fixedpoint.json").GetProperty("cases").EnumerateArray())
        {
            // "csharp" is set where KalTerm is deliberately stricter than the reference.
            var expected = c.TryGetProperty("csharp", out var strict) ? strict.GetString()! : c.GetProperty("expect").GetString()!;
            data.Add(c.GetProperty("scale").GetString()!, c.GetProperty("input").GetString()!, expected);
        }

        return data;
    }

    public static TheoryData<string, string, string> RoundTripCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var r in VectorFile.Load("fixedpoint.json").GetProperty("roundTrips").EnumerateArray())
        {
            data.Add(r.GetProperty("scale").GetString()!, r.GetProperty("scaled").GetString()!, r.GetProperty("decimal").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void ParseMatchesTheReference(string scale, string input, string expected)
    {
        if (expected == Refuse)
        {
            Assert.False(TryParse(scale, input, out _));
            Assert.Throws<PrecisionException>(() => Parse(scale, input));
            return;
        }

        var scaled = long.Parse(expected, CultureInfo.InvariantCulture);
        Assert.True(TryParse(scale, input, out var viaTry));
        Assert.Equal(scaled, viaTry);
        Assert.Equal(scaled, Parse(scale, input));
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void ConversionBackToDecimalMatchesTheReference(string scale, string scaled, string expectedDecimal)
    {
        var value = long.Parse(scaled, CultureInfo.InvariantCulture);
        var expected = decimal.Parse(expectedDecimal, NumberStyles.Float, CultureInfo.InvariantCulture);

        Assert.Equal(expected, ToDecimal(scale, value));
        Assert.Equal(value, Parse(scale, ToText(scale, value)));   // ToString is exact and parseable
    }

    [Fact]
    public void TheVectorFileRecordsWhatItWasGeneratedFrom()
    {
        var file = VectorFile.Load("fixedpoint.json");
        Assert.Matches("^[0-9a-f]{40}$", file.GetProperty("referenceCommit").GetString());
        Assert.True(file.GetProperty("cases").GetArrayLength() > 100);
    }

    private static bool TryParse(string scale, string input, out long scaled)
    {
        switch (scale)
        {
            case "e6":
                var ok6 = Dollars.TryParse(input, out var d);
                scaled = d.Micros;
                return ok6;
            default:
                var ok2 = Contracts.TryParse(input, out var c);
                scaled = c.Hundredths;
                return ok2;
        }
    }

    private static long Parse(string scale, string input) =>
        scale == "e6" ? Dollars.Parse(input).Micros : Contracts.Parse(input).Hundredths;

    private static decimal ToDecimal(string scale, long scaled) =>
        scale == "e6" ? Dollars.FromMicros(scaled).ToDecimal() : Contracts.FromHundredths(scaled).ToDecimal();

    private static string ToText(string scale, long scaled) =>
        scale == "e6" ? Dollars.FromMicros(scaled).ToString() : Contracts.FromHundredths(scaled).ToString();
}
