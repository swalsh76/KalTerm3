namespace KalTerm.Core.Money;

/// <summary>
/// A market level such as a strike (a temperature, a price of an underlying) as a <see cref="long"/> count of millionths.
/// Same scale and exactness as <see cref="Dollars"/> but a different quantity, so the two cannot be mixed up by accident.
/// Values finer than 10^-6 are refused, never rounded. No floating point anywhere.
/// </summary>
public readonly record struct Level : IComparable<Level>
{
    /// <summary>Decimal places in the scale.</summary>
    public const int ScaleDigits = 6;

    /// <summary>Millionths in one unit.</summary>
    public const long Scale = 1_000_000;

    private Level(long micros)
    {
        Micros = micros;
    }

    public static Level Zero => default;

    /// <summary>The level in millionths of a unit.</summary>
    public long Micros { get; }

    public static Level FromMicros(long micros) => new(FixedPointParser.Validate(micros));

    public static Level FromDecimal(decimal value) => new(FixedPointParser.FromDecimal(value, ScaleDigits));

    /// <summary>Parses decimal text such as "118.7499". Throws <see cref="PrecisionException"/> if it cannot be exact.</summary>
    public static Level Parse(string? text) => new(FixedPointParser.Parse(text, ScaleDigits));

    public static bool TryParse(string? text, out Level result)
    {
        if (text is not null && FixedPointParser.TryParse(text, ScaleDigits, out var micros) == ParseStatus.Ok)
        {
            result = new Level(micros);
            return true;
        }

        result = default;
        return false;
    }

    public static bool operator <(Level left, Level right) => left.CompareTo(right) < 0;

    public static bool operator >(Level left, Level right) => left.CompareTo(right) > 0;

    public static bool operator <=(Level left, Level right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Level left, Level right) => left.CompareTo(right) >= 0;

    public decimal ToDecimal() => new decimal(Micros) / Scale;

    public int CompareTo(Level other) => Micros.CompareTo(other.Micros);

    /// <summary>Fixed-point text with six decimals in the invariant culture, for example "118.749900".</summary>
    public override string ToString() => FixedPointParser.Format(Micros, ScaleDigits);
}
