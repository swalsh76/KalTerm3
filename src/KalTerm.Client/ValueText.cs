using System.Globalization;
using System.Text.RegularExpressions;

namespace KalTerm.Client;

/// <summary>
/// How exact values from the API are shortened for a person: the one place the command line and the terminal get their rules from, so the two never show the same number two ways.
/// The API sends exact decimal strings; these only shorten them for reading (<c>0.560000</c> becomes <c>0.56</c>) and never change what they mean.
/// </summary>
public static partial class ValueText
{
    /// <summary>Shown where there is no value.</summary>
    public const string None = "-";

    /// <summary>The shape of an exact decimal string from the API: a sign, the whole part and the fraction.</summary>
    /// <returns>The pattern, with groups <c>sign</c>, <c>whole</c> and <c>fraction</c>.</returns>
    [GeneratedRegex(@"^(?<sign>-?)(?<whole>[0-9]+)\.(?<fraction>[0-9]+)$", RegexOptions.CultureInvariant)]
    public static partial Regex DecimalText();

    /// <summary>Dollars without trailing zeros but with at least two decimals: <c>0.560000</c> gives <c>0.56</c>, <c>1.000000</c> gives <c>1.00</c>.</summary>
    /// <param name="value">The API's decimal text.</param>
    /// <returns>The text for a person; anything that is not a decimal comes back as it was.</returns>
    public static string Dollars(string? value)
    {
        if (value is null)
        {
            return None;
        }

        var match = DecimalText().Match(value);
        if (!match.Success)
        {
            return value;
        }

        var fraction = match.Groups["fraction"].Value.TrimEnd('0').PadRight(2, '0');
        return $"{match.Groups["sign"].Value}{match.Groups["whole"].Value}.{fraction}";
    }

    /// <summary>Contracts with thousands separators; a whole number loses its <c>.00</c>: <c>1234.00</c> gives <c>1,234</c>, <c>20.50</c> stays <c>20.50</c>.</summary>
    /// <param name="value">The API's decimal text.</param>
    /// <returns>The text for a person; anything that is not a decimal comes back as it was.</returns>
    public static string Count(string? value)
    {
        if (value is null)
        {
            return None;
        }

        var match = DecimalText().Match(value);
        if (!match.Success)
        {
            return value;
        }

        var whole = match.Groups["whole"].Value.TrimStart('0');
        whole = whole.Length == 0 ? "0" : System.Numerics.BigInteger.Parse(whole, CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture);
        var fraction = match.Groups["fraction"].Value;
        return fraction.TrimEnd('0').Length == 0 ? $"{match.Groups["sign"].Value}{whole}" : $"{match.Groups["sign"].Value}{whole}.{fraction}";
    }

    /// <summary>How long ago, coarsely: <c>45s ago</c>, <c>12m ago</c>, <c>5h ago</c>, <c>3d ago</c>.</summary>
    /// <param name="seconds">The age in seconds.</param>
    /// <returns>The text.</returns>
    public static string Age(long? seconds) => seconds switch
    {
        null => None,
        < 120 => $"{seconds}s ago",
        < 7_200 => $"{seconds / 60}m ago",
        < 172_800 => $"{seconds / 3_600}h ago",
        _ => $"{seconds / 86_400}d ago",
    };

    /// <summary>A byte count as <c>264.0 MB</c>.</summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The text.</returns>
    public static string Size(long? bytes)
    {
        if (bytes is not { } value)
        {
            return None;
        }

        foreach (var (unit, step) in new (string, long)[] { ("GB", 1L << 30), ("MB", 1L << 20), ("kB", 1L << 10) })
        {
            if (value >= step)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{(double)value / step:0.0} {unit}");
            }
        }

        return $"{value} B";
    }
}
