using KalTerm.Core.Fees;
using KalTerm.Core.Money;

namespace KalTerm.Core.Tests.Fees;

/// <summary>The reference's own fee tests (kalshiterm-server/tests/test_fees.py), ported case for case.</summary>
public class KalshiFeesTests
{
    private static Dollars Fee(long multiplierMicros, long countHundredths, long priceMicros) =>
        KalshiFees.TakerFee(
            FeeMultiplier.FromMicros(multiplierMicros),
            Contracts.FromHundredths(countHundredths),
            Dollars.FromMicros(priceMicros));

    [Fact]
    public void KalshisOwnWorkedExample()
    {
        // Fee Rounding docs: a buy at $0.055 has a model fee of $0.00363825 and a trade fee of $0.003639.
        Assert.Equal(3_639, Fee(1_000_000, 100, 55_000).Micros);
    }

    [Theory]
    [InlineData(1_000_000, 100, 500_000)]      // the peak: 1.75 cents per contract
    [InlineData(1_000_000, 10_000, 500_000)]   // 100 contracts
    [InlineData(500_000, 2_550, 123_456)]      // half multiplier, 25.5 contracts, odd price
    [InlineData(1_000_000, 1, 999_999)]        // one hundredth of a contract at the edge of the range
    [InlineData(1_234_567, 777, 10_000)]
    public void MatchesAnIndependentDecimalComputation(long multiplierMicros, long countHundredths, long priceMicros)
    {
        var m = multiplierMicros / 1_000_000m;
        var c = countHundredths / 100m;
        var p = priceMicros / 1_000_000m;
        var exact = m * 0.07m * c * p * (1 - p);
        var expected = (long)decimal.Ceiling(exact * 1_000_000m);

        Assert.Equal(expected, Fee(multiplierMicros, countHundredths, priceMicros).Micros);
    }

    [Fact]
    public void ThePeakFeeAndTheExtremes()
    {
        Assert.Equal(17_500, Fee(1_000_000, 100, 500_000).Micros);   // $0.0175 per contract at 50 cents
        Assert.Equal(0, Fee(1_000_000, 100, 0).Micros);
        Assert.Equal(0, Fee(1_000_000, 100, 1_000_000).Micros);
        Assert.Equal(0, Fee(0, 100, 500_000).Micros);                // a series with multiplier 0 charges nothing
        Assert.Equal(Fee(1_000_000, 100, 400_000), Fee(1_000_000, 100, 600_000));   // P (1 - P) is symmetric
    }

    [Fact]
    public void TheFeeNeverRoundsDownEvenByAMillionth()
    {
        Assert.Equal(693, Fee(1_000_000, 100, 10_000).Micros);   // 0.07 x 0.01 x 0.99 = $0.000693 exactly
        Assert.Equal(7, Fee(1_000_000, 1, 10_000).Micros);       // $0.00000693 = 6.93 millionths, rounds up to 7
    }

    [Theory]
    [InlineData(1_000_000, 100, -1)]
    [InlineData(1_000_000, 100, 1_000_001)]
    [InlineData(1_000_000, -1, 5)]
    public void InputsOutsideTheDomainAreRefused(long multiplierMicros, long countHundredths, long priceMicros)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fee(multiplierMicros, countHundredths, priceMicros));
    }

    [Fact]
    public void ANegativeMultiplierCannotBeConstructed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FeeMultiplier.FromMicros(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FeeMultiplier.Parse("-0.07"));
        Assert.False(FeeMultiplier.TryParse("-0.07", out _));
    }

    [Fact]
    public void AFeeThatDoesNotFitIsAnOverflowNotAWrongNumber()
    {
        // The reference uses unbounded integers; a Dollars value is a long, so absurd inputs are refused.
        Assert.Throws<OverflowException>(() => Fee(long.MaxValue, long.MaxValue, 500_000));
        Assert.Throws<OverflowException>(() => Fee(1_000_000_000_000, 1_000_000_000_000, 500_000));
    }

    [Fact]
    public void ALargeButRealisticOrderIsExact()
    {
        // 1,000,000 contracts (10^8 hundredths) at 50 cents with multiplier 1: $17,500 exactly.
        Assert.Equal(17_500_000_000, Fee(1_000_000, 100_000_000, 500_000).Micros);
    }

    [Fact]
    public void RoundingUpToAUnit()
    {
        Assert.Equal(20_000, KalshiFees.RoundUp(Dollars.FromMicros(17_500), Dollars.FromMicros(10_000)).Micros);   // $0.0175 -> $0.02
        Assert.Equal(20_000, KalshiFees.RoundUp(Dollars.FromMicros(20_000), Dollars.FromMicros(10_000)).Micros);
        Assert.Equal(0, KalshiFees.RoundUp(Dollars.Zero, Dollars.FromMicros(10_000)).Micros);
        Assert.Equal(3_700, KalshiFees.RoundUp(Dollars.FromMicros(3_639), Dollars.FromMicros(100)).Micros);
        Assert.Equal(3_639, KalshiFees.RoundUp(Dollars.FromMicros(3_639), Dollars.FromMicros(1)).Micros);
        Assert.Throws<ArgumentOutOfRangeException>(() => KalshiFees.RoundUp(Dollars.FromMicros(5), Dollars.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => KalshiFees.RoundUp(Dollars.FromMicros(5), Dollars.FromMicros(-1)));
    }

    [Fact]
    public void RoundingUpNegativeValuesGoesTowardZero()
    {
        // The ceiling of a negative amount is toward zero: -$0.0175 to a $0.01 unit is -$0.01.
        Assert.Equal(-10_000, KalshiFees.RoundUp(Dollars.FromMicros(-17_500), Dollars.FromMicros(10_000)).Micros);
        Assert.Equal(-20_000, KalshiFees.RoundUp(Dollars.FromMicros(-20_000), Dollars.FromMicros(10_000)).Micros);
    }

    [Fact]
    public void RoundingUpDetectsOverflow()
    {
        Assert.Throws<OverflowException>(() => KalshiFees.RoundUp(Dollars.FromMicros(long.MaxValue), Dollars.FromMicros(1_000_000)));
    }

    [Fact]
    public void OnlyTheQuadraticFamilyIsModelled()
    {
        Assert.Equal(1_000_000, KalshiFees.TakerMultiplier("quadratic", 1_000_000)!.Value.Micros);
        Assert.Equal(500_000, KalshiFees.TakerMultiplier("quadratic_with_maker_fees", 500_000)!.Value.Micros);
        Assert.Equal(0, KalshiFees.TakerMultiplier("quadratic_with_combo_maker_fees", 0)!.Value.Micros);

        Assert.Null(KalshiFees.TakerMultiplier("flat", 1_000_000));
        Assert.Null(KalshiFees.TakerMultiplier("margin_market_maker_program_fees", 1));
        Assert.Null(KalshiFees.TakerMultiplier(null, 1));
        Assert.Null(KalshiFees.TakerMultiplier("quadratic", null));
        Assert.Null(KalshiFees.TakerMultiplier("quadratic", -1));
        Assert.Null(KalshiFees.TakerMultiplier("mystery", 1_000_000));
        Assert.Null(KalshiFees.TakerMultiplier("Quadratic", 1_000_000));   // fee types are matched exactly
    }

    [Fact]
    public void TheQuadraticFamilyIsExactlyThree()
    {
        Assert.Equal(
            ["quadratic", "quadratic_with_combo_maker_fees", "quadratic_with_maker_fees"],
            KalshiFees.QuadraticFamily.Order(StringComparer.Ordinal).ToArray());
    }
}
