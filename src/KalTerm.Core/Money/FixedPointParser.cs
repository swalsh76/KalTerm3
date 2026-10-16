using System.Globalization;

namespace KalTerm.Core.Money;

internal enum ParseStatus
{
    Ok,
    NotANumber,
    TooPrecise,
    OutOfRange,
}

/// <summary>
/// Exact conversion between decimal text and scaled <see cref="long"/> values. No floating point
/// is involved at any point. Accepts <c>[+-]digits[.digits][e[+-]digits]</c> with ASCII digits only.
/// Refuses (never rounds) anything finer than the scale or larger than ±(2^63 - 1).
/// </summary>
internal static class FixedPointParser
{
    private const int MaxDigits = 19;            // a long has at most 19 decimal digits
    private const long ExponentClamp = 100_000;  // beyond this the outcome is the same: out of range or too precise

    private static readonly ulong[] PowersOfTen = BuildPowersOfTen();

    internal static ParseStatus TryParse(ReadOnlySpan<char> text, int scaleDigits, out long value)
    {
        value = 0;
        var i = 0;
        var negative = false;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            negative = text[i] == '-';
            i++;
        }

        var intStart = i;
        while (i < text.Length && IsDigit(text[i]))
        {
            i++;
        }

        var intLen = i - intStart;
        var fracStart = i;
        var fracLen = 0;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            fracStart = i;
            while (i < text.Length && IsDigit(text[i]))
            {
                i++;
            }

            fracLen = i - fracStart;
        }

        if (intLen + fracLen == 0)
        {
            return ParseStatus.NotANumber;
        }

        long exponent = 0;
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            var expNegative = false;
            if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            {
                expNegative = text[i] == '-';
                i++;
            }

            var expStart = i;
            while (i < text.Length && IsDigit(text[i]))
            {
                exponent = Math.Min(ExponentClamp, (exponent * 10) + (text[i] - '0'));
                i++;
            }

            if (i == expStart)
            {
                return ParseStatus.NotANumber;
            }

            if (expNegative)
            {
                exponent = -exponent;
            }
        }

        if (i != text.Length)
        {
            return ParseStatus.NotANumber;
        }

        // The digits of the number, integer part then fraction part, as one sequence.
        var totalDigits = intLen + fracLen;
        var first = -1;
        var last = -1;
        for (var k = 0; k < totalDigits; k++)
        {
            if (DigitAt(text, intStart, intLen, fracStart, k) != '0')
            {
                if (first < 0)
                {
                    first = k;
                }

                last = k;
            }
        }

        if (first < 0)
        {
            return ParseStatus.Ok;   // zero, whatever the exponent
        }

        // value = mantissa x 10^exp10, with the mantissa stripped of leading and trailing zeros.
        var significant = last - first + 1;
        var exp10 = exponent - fracLen + (totalDigits - 1 - last);
        var shift = exp10 + scaleDigits;
        if (shift < 0)
        {
            return ParseStatus.TooPrecise;       // a stripped mantissa is not divisible by 10
        }

        if (significant + shift > MaxDigits)
        {
            return ParseStatus.OutOfRange;
        }

        ulong mantissa = 0;
        for (var k = first; k <= last; k++)
        {
            mantissa = (mantissa * 10) + (ulong)(DigitAt(text, intStart, intLen, fracStart, k) - '0');
        }

        var scaled = mantissa * PowersOfTen[shift];   // at most 19 digits, so it cannot overflow a ulong
        if (scaled > long.MaxValue)
        {
            return ParseStatus.OutOfRange;
        }

        value = negative ? -(long)scaled : (long)scaled;
        return ParseStatus.Ok;
    }

    internal static long Parse(string? text, int scaleDigits)
    {
        if (text is null)
        {
            throw new PrecisionException("not a number: null");
        }

        var status = TryParse(text, scaleDigits, out var value);
        return status switch
        {
            ParseStatus.Ok => value,
            ParseStatus.NotANumber => throw new PrecisionException($"not a number: '{text}'"),
            ParseStatus.TooPrecise => throw new PrecisionException($"'{text}' has more precision than 1/10^{scaleDigits}"),
            _ => throw new PrecisionException($"'{text}' does not fit in a long at scale 1/10^{scaleDigits}"),
        };
    }

    internal static long FromDecimal(decimal value, int scaleDigits)
    {
        decimal scaled;
        try
        {
            scaled = value * (decimal)PowersOfTen[scaleDigits];
        }
        catch (OverflowException ex)
        {
            throw new PrecisionException($"{value} does not fit in a long at scale 1/10^{scaleDigits}", ex);
        }

        if (scaled != decimal.Truncate(scaled))
        {
            throw new PrecisionException($"{value} has more precision than 1/10^{scaleDigits}");
        }

        if (scaled > long.MaxValue || scaled < -long.MaxValue)
        {
            throw new PrecisionException($"{value} does not fit in a long at scale 1/10^{scaleDigits}");
        }

        return (long)scaled;
    }

    /// <summary>Fixed-point text with exactly <paramref name="scaleDigits"/> decimals, invariant culture.</summary>
    internal static string Format(long scaled, int scaleDigits)
    {
        var unit = (long)PowersOfTen[scaleDigits];
        var magnitude = scaled < 0 ? -scaled : scaled;   // safe: long.MinValue is never a valid value
        var whole = magnitude / unit;
        var fraction = magnitude % unit;
        var sign = scaled < 0 ? "-" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{whole}.{fraction.ToString("D" + scaleDigits, CultureInfo.InvariantCulture)}");
    }

    /// <summary>Rejects <see cref="long.MinValue"/>, which has no positive counterpart.</summary>
    internal static long Validate(long scaled)
    {
        if (scaled == long.MinValue)
        {
            throw new OverflowException("long.MinValue is not a valid fixed-point value");
        }

        return scaled;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    /// <summary>The k-th digit of the number, counting the integer part then the fraction part as one sequence.</summary>
    private static char DigitAt(ReadOnlySpan<char> text, int intStart, int intLen, int fracStart, int k) =>
        k < intLen ? text[intStart + k] : text[fracStart + (k - intLen)];

    private static ulong[] BuildPowersOfTen()
    {
        var table = new ulong[MaxDigits + 1];
        table[0] = 1;
        for (var k = 1; k < table.Length; k++)
        {
            table[k] = table[k - 1] * 10;
        }

        return table;
    }
}
