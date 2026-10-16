namespace KalTerm.Core.Money;

/// <summary>
/// A contract count as a <see cref="long"/> number of hundredths of a contract.
/// Exact: values finer than 10^-2 are refused, never rounded. No floating point anywhere.
/// </summary>
public readonly record struct Contracts : IComparable<Contracts>
{
    /// <summary>Decimal places in the scale.</summary>
    public const int ScaleDigits = 2;

    /// <summary>Hundredths in one contract.</summary>
    public const long Scale = 100;

    private Contracts(long hundredths)
    {
        Hundredths = hundredths;
    }

    public static Contracts Zero => default;

    public static Contracts One => new(Scale);

    /// <summary>The count in hundredths of a contract.</summary>
    public long Hundredths { get; }

    public static Contracts FromHundredths(long hundredths) => new(FixedPointParser.Validate(hundredths));

    /// <summary>Whole contracts. Throws <see cref="OverflowException"/> if the result does not fit.</summary>
    public static Contracts FromWhole(long contracts) => new(FixedPointParser.Validate(checked(contracts * Scale)));

    public static Contracts FromDecimal(decimal value) => new(FixedPointParser.FromDecimal(value, ScaleDigits));

    /// <summary>Parses decimal text such as "104.24". Throws <see cref="PrecisionException"/> if it cannot be exact.</summary>
    public static Contracts Parse(string? text) => new(FixedPointParser.Parse(text, ScaleDigits));

    public static bool TryParse(string? text, out Contracts result)
    {
        if (text is not null && FixedPointParser.TryParse(text, ScaleDigits, out var hundredths) == ParseStatus.Ok)
        {
            result = new Contracts(hundredths);
            return true;
        }

        result = default;
        return false;
    }

    public static Contracts Add(Contracts left, Contracts right) =>
        new(FixedPointParser.Validate(checked(left.Hundredths + right.Hundredths)));

    public static Contracts Subtract(Contracts left, Contracts right) =>
        new(FixedPointParser.Validate(checked(left.Hundredths - right.Hundredths)));

    public static Contracts Negate(Contracts value) => new(-value.Hundredths);

    public static Contracts operator +(Contracts left, Contracts right) => Add(left, right);

    public static Contracts operator -(Contracts left, Contracts right) => Subtract(left, right);

    public static Contracts operator -(Contracts value) => Negate(value);

    public static bool operator <(Contracts left, Contracts right) => left.CompareTo(right) < 0;

    public static bool operator >(Contracts left, Contracts right) => left.CompareTo(right) > 0;

    public static bool operator <=(Contracts left, Contracts right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Contracts left, Contracts right) => left.CompareTo(right) >= 0;

    public decimal ToDecimal() => new decimal(Hundredths) / Scale;

    public int CompareTo(Contracts other) => Hundredths.CompareTo(other.Hundredths);

    /// <summary>Fixed-point text with two decimals in the invariant culture, for example "104.24".</summary>
    public override string ToString() => FixedPointParser.Format(Hundredths, ScaleDigits);
}
