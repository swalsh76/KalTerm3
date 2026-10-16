namespace KalTerm.Core.Money;

/// <summary>
/// A dollar amount (or price) as a <see cref="long"/> count of millionths of a dollar.
/// Exact: values finer than 10^-6 are refused, never rounded. No floating point anywhere.
/// </summary>
public readonly record struct Dollars : IComparable<Dollars>
{
    /// <summary>Decimal places in the scale.</summary>
    public const int ScaleDigits = 6;

    /// <summary>Millionths in one dollar.</summary>
    public const long Scale = 1_000_000;

    private Dollars(long micros)
    {
        Micros = micros;
    }

    public static Dollars Zero => default;

    public static Dollars One => new(Scale);

    /// <summary>The amount in millionths of a dollar.</summary>
    public long Micros { get; }

    public static Dollars FromMicros(long micros) => new(FixedPointParser.Validate(micros));

    /// <summary>Whole dollars. Throws <see cref="OverflowException"/> if the result does not fit.</summary>
    public static Dollars FromWhole(long dollars) => new(FixedPointParser.Validate(checked(dollars * Scale)));

    public static Dollars FromDecimal(decimal value) => new(FixedPointParser.FromDecimal(value, ScaleDigits));

    /// <summary>Parses decimal text such as "0.4100". Throws <see cref="PrecisionException"/> if it cannot be exact.</summary>
    public static Dollars Parse(string? text) => new(FixedPointParser.Parse(text, ScaleDigits));

    public static bool TryParse(string? text, out Dollars result)
    {
        if (text is not null && FixedPointParser.TryParse(text, ScaleDigits, out var micros) == ParseStatus.Ok)
        {
            result = new Dollars(micros);
            return true;
        }

        result = default;
        return false;
    }

    public static Dollars Add(Dollars left, Dollars right) =>
        new(FixedPointParser.Validate(checked(left.Micros + right.Micros)));

    public static Dollars Subtract(Dollars left, Dollars right) =>
        new(FixedPointParser.Validate(checked(left.Micros - right.Micros)));

    public static Dollars Negate(Dollars value) => new(-value.Micros);

    public static Dollars operator +(Dollars left, Dollars right) => Add(left, right);

    public static Dollars operator -(Dollars left, Dollars right) => Subtract(left, right);

    public static Dollars operator -(Dollars value) => Negate(value);

    public static bool operator <(Dollars left, Dollars right) => left.CompareTo(right) < 0;

    public static bool operator >(Dollars left, Dollars right) => left.CompareTo(right) > 0;

    public static bool operator <=(Dollars left, Dollars right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Dollars left, Dollars right) => left.CompareTo(right) >= 0;

    public decimal ToDecimal() => new decimal(Micros) / Scale;

    public int CompareTo(Dollars other) => Micros.CompareTo(other.Micros);

    /// <summary>Fixed-point text with six decimals in the invariant culture, for example "0.410000".</summary>
    public override string ToString() => FixedPointParser.Format(Micros, ScaleDigits);
}
