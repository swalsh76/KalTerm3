using KalTerm.Core.Money;

namespace KalTerm.Core.Fees;

/// <summary>
/// A series' fee multiplier (the M in the taker fee formula) as a non-negative <see cref="long"/>
/// count of millionths. Exact: finer text is refused, never rounded.
/// </summary>
public readonly record struct FeeMultiplier
{
    /// <summary>Decimal places in the scale.</summary>
    public const int ScaleDigits = 6;

    /// <summary>Millionths in a multiplier of 1.</summary>
    public const long Scale = 1_000_000;

    private FeeMultiplier(long micros)
    {
        Micros = micros;
    }

    public static FeeMultiplier Zero => default;

    public static FeeMultiplier One => new(Scale);

    /// <summary>The multiplier in millionths.</summary>
    public long Micros { get; }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for a negative multiplier.</summary>
    public static FeeMultiplier FromMicros(long micros)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(micros);
        return new FeeMultiplier(micros);
    }

    /// <summary>
    /// Parses decimal text such as "0.07". Throws <see cref="PrecisionException"/> if it cannot be exact and
    /// <see cref="ArgumentOutOfRangeException"/> if it is negative.
    /// </summary>
    public static FeeMultiplier Parse(string? text) => FromMicros(FixedPointParser.Parse(text, ScaleDigits));

    public static bool TryParse(string? text, out FeeMultiplier result)
    {
        if (text is not null
            && FixedPointParser.TryParse(text, ScaleDigits, out var micros) == ParseStatus.Ok
            && micros >= 0)
        {
            result = new FeeMultiplier(micros);
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>Fixed-point text with six decimals in the invariant culture, for example "0.070000".</summary>
    public override string ToString() => FixedPointParser.Format(Micros, ScaleDigits);
}
