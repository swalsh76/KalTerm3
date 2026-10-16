using System.Text.Json;
using KalTerm.Core.Fees;
using KalTerm.Core.Money;

namespace KalTerm.Core.Tests.Fees;

/// <summary>Differential tests: every expectation comes from the Python reference (fees.json).</summary>
public class FeeVectorTests
{
    public static TheoryData<long, long, long, string> TakerFeeCases()
    {
        var data = new TheoryData<long, long, long, string>();
        foreach (var c in VectorFile.Load("fees.json").GetProperty("takerFee").EnumerateArray())
        {
            data.Add(
                c.GetProperty("multiplier").GetInt64(),
                c.GetProperty("count").GetInt64(),
                c.GetProperty("price").GetInt64(),
                c.GetProperty("expect").GetString()!);
        }

        return data;
    }

    public static TheoryData<long, long, string> RoundUpCases()
    {
        var data = new TheoryData<long, long, string>();
        foreach (var c in VectorFile.Load("fees.json").GetProperty("roundUp").EnumerateArray())
        {
            data.Add(c.GetProperty("value").GetInt64(), c.GetProperty("unit").GetInt64(), c.GetProperty("expect").GetString()!);
        }

        return data;
    }

    public static TheoryData<string?, long?, string> MultiplierCases()
    {
        var data = new TheoryData<string?, long?, string>();
        foreach (var c in VectorFile.Load("fees.json").GetProperty("takerMultiplier").EnumerateArray())
        {
            var feeType = c.GetProperty("feeType");
            var multiplier = c.GetProperty("multiplier");
            data.Add(
                feeType.ValueKind == JsonValueKind.Null ? null : feeType.GetString(),
                multiplier.ValueKind == JsonValueKind.Null ? null : multiplier.GetInt64(),
                c.GetProperty("expect").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TakerFeeCases))]
    public void TakerFeeMatchesTheReference(long multiplier, long count, long price, string expected)
    {
        Assert.Equal(expected, Outcome(() => KalshiFees.TakerFee(
            FeeMultiplier.FromMicros(multiplier), Contracts.FromHundredths(count), Dollars.FromMicros(price))));
    }

    [Theory]
    [MemberData(nameof(RoundUpCases))]
    public void RoundUpMatchesTheReference(long value, long unit, string expected)
    {
        Assert.Equal(expected, Outcome(() => KalshiFees.RoundUp(Dollars.FromMicros(value), Dollars.FromMicros(unit))));
    }

    [Theory]
    [MemberData(nameof(MultiplierCases))]
    public void TakerMultiplierMatchesTheReference(string? feeType, long? multiplier, string expected)
    {
        var result = KalshiFees.TakerMultiplier(feeType, multiplier);
        Assert.Equal(expected, result is null ? "null" : result.Value.Micros.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheQuadraticFamilyMatchesTheReference()
    {
        var reference = VectorFile.Load("fees.json").GetProperty("quadraticFamily").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal);
        Assert.Equal(reference, KalshiFees.QuadraticFamily.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheVectorFileHasTheExpectedBreadth()
    {
        var file = VectorFile.Load("fees.json");
        var fees = file.GetProperty("takerFee").EnumerateArray().Select(c => c.GetProperty("expect").GetString()).ToList();
        Assert.True(fees.Count > 1_000);
        Assert.Contains("refuse", fees);
        Assert.Contains("overflow", fees);
        Assert.Matches("^[0-9a-f]{40}$", file.GetProperty("referenceCommit").GetString());
    }

    // The reference refuses out-of-domain input with ValueError and returns unbounded integers; here a
    // refusal is an ArgumentOutOfRangeException and a result that does not fit a long is an OverflowException.
    private static string Outcome(Func<Dollars> compute)
    {
        try
        {
            return compute().Micros.ToString(CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "refuse";
        }
        catch (OverflowException)
        {
            return "overflow";
        }
    }
}
