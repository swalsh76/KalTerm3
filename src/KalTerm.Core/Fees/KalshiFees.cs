using System.Collections.Frozen;
using KalTerm.Core.Money;

namespace KalTerm.Core.Fees;

/// <summary>
/// Kalshi trading fees as exact integer arithmetic. Source: Kalshi's API documentation ("Fee Rounding",
/// series <c>fee_type</c> / <c>fee_multiplier</c>). The model fee of a taker trade is
/// <c>M x 0.07 x C x P x (1 - P)</c> (M the series multiplier, C contracts, P the price in dollars)
/// and the trade fee is that rounded UP to $0.000001. Maker fees are a further fraction of the same
/// shape and do not apply to orders that take liquidity.
/// </summary>
/// <remarks>
/// Only the quadratic family is modelled. Flat and margin-program fees are not:
/// <see cref="TakerMultiplier"/> returns <c>null</c> for them so callers skip rather than guess.
/// </remarks>
public static class KalshiFees
{
    // The numerator is M x 7 x C x P x (10^6 - P) with M and P in millionths and C in hundredths. Dividing by
    // 10^6 (M) x 100 (the 0.07) x 100 (C) x 10^6 (P x (1 - P) carries 10^12, and the result is wanted in millionths) = 10^16.
    private static readonly UInt128 Denominator = (UInt128)10_000_000_000_000_000;

    /// <summary>The fee types whose taker fee follows the quadratic formula.</summary>
    public static IReadOnlySet<string> QuadraticFamily { get; } = new[]
    {
        "quadratic",
        "quadratic_with_maker_fees",
        "quadratic_with_combo_maker_fees",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The taker multiplier for a series, or <c>null</c> when the fee cannot be modelled: a fee type outside
    /// the quadratic family, a missing multiplier, or a negative one.
    /// </summary>
    public static FeeMultiplier? TakerMultiplier(string? feeType, long? multiplierMicros) =>
        feeType is not null && QuadraticFamily.Contains(feeType) && multiplierMicros is >= 0
            ? FeeMultiplier.FromMicros(multiplierMicros.Value)
            : null;

    /// <summary>
    /// The trade fee of a taker fill: <c>M x 0.07 x C x P x (1 - P)</c> rounded up to a millionth, exactly.
    /// Throws <see cref="ArgumentOutOfRangeException"/> for a negative count or a price outside [$0, $1], and
    /// <see cref="OverflowException"/> when the exact fee does not fit a <see cref="Dollars"/>.
    /// </summary>
    public static Dollars TakerFee(FeeMultiplier multiplier, Contracts count, Dollars price)
    {
        if (price.Micros < 0 || price.Micros > Dollars.Scale)
        {
            throw new ArgumentOutOfRangeException(nameof(price), price, "price must be within [$0, $1]");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(count.Hundredths, nameof(count));

        UInt128 numerator = checked(
            (UInt128)multiplier.Micros * (UInt128)7 * (UInt128)count.Hundredths
            * (UInt128)price.Micros * (UInt128)(Dollars.Scale - price.Micros));

        var (quotient, remainder) = UInt128.DivRem(numerator, Denominator);
        if (remainder != UInt128.Zero)
        {
            quotient += UInt128.One;   // round up: the fee never rounds down, even by a millionth
        }

        if (quotient > (UInt128)long.MaxValue)
        {
            throw new OverflowException("the exact fee does not fit a Dollars value");
        }

        return Dollars.FromMicros((long)quotient);
    }

    /// <summary>
    /// Rounds <paramref name="value"/> up to a multiple of <paramref name="unit"/> (a unit of one micro-dollar
    /// leaves it unchanged). Throws <see cref="ArgumentOutOfRangeException"/> for a unit below one micro-dollar and
    /// <see cref="OverflowException"/> when the result does not fit.
    /// </summary>
    public static Dollars RoundUp(Dollars value, Dollars unit)
    {
        if (unit.Micros < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(unit), unit, "rounding unit must be at least one micro-dollar");
        }

        var quotient = Math.DivRem(value.Micros, unit.Micros, out var remainder);   // truncates toward zero
        if (remainder > 0)
        {
            quotient++;   // a positive remainder rounds up; for negatives truncation toward zero already does
        }

        return Dollars.FromMicros(checked(quotient * unit.Micros));
    }
}
