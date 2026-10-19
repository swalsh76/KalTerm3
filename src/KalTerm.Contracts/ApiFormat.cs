using System.Globalization;
using System.Text;

namespace KalTerm.Contracts;

/// <summary>
/// How stored values leave the API. Prices and counts are decimal <b>strings</b> built from the stored integers with no floating point, so a client gets
/// exactly what was stored; times are ISO-8601 UTC with microseconds; cursors are opaque URL-safe text. The reference's formatting module is the oracle
/// (golden vectors in <c>api_format.json</c>).
/// </summary>
public static class ApiFormat
{
    private const long DollarScale = 1_000_000;
    private const long CountScale = 100;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Dollars stored at 10^-6: <c>560000</c> becomes <c>"0.560000"</c>.</summary>
    /// <param name="e6">The stored value, or <see langword="null"/>.</param>
    /// <returns>The text, or <see langword="null"/> when there is no value.</returns>
    public static string? Dollars(long? e6) => e6 is { } value ? Fixed(value, DollarScale, 6) : null;

    /// <summary>Contracts stored at 10^-2: <c>1850</c> becomes <c>"18.50"</c>.</summary>
    /// <param name="e2">The stored value, or <see langword="null"/>.</param>
    /// <returns>The text, or <see langword="null"/> when there is no value.</returns>
    public static string? Count(long? e2) => e2 is { } value ? Fixed(value, CountScale, 2) : null;

    /// <summary>A moment as <c>2026-10-18T22:23:48.123000+00:00</c>. An unspecified kind is taken to be UTC, as the database stores it.</summary>
    /// <param name="moment">The time.</param>
    /// <returns>The text.</returns>
    public static string Moment(DateTime moment)
    {
        var utc = moment.Kind == DateTimeKind.Local ? moment.ToUniversalTime() : moment;
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
    }

    /// <summary>A moment, or <see langword="null"/> when there is none.</summary>
    /// <param name="moment">The time, if any.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    public static string? Moment(DateTime? moment) => moment is { } value ? Moment(value) : null;

    /// <summary>Wraps text in an opaque cursor: URL-safe base64 without padding.</summary>
    /// <param name="text">What the server needs to resume from.</param>
    /// <returns>The cursor.</returns>
    public static string EncodeCursor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The text inside a cursor, or <see langword="null"/> when it is not one of ours (illegal characters, bad padding, not UTF-8, or empty).</summary>
    /// <param name="cursor">The cursor a client sent.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    public static string? DecodeCursor(string cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        foreach (var c in cursor)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '='))
            {
                return null;
            }
        }

        var padded = cursor.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        var buffer = new byte[(padded.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64String(padded, buffer, out var written))
        {
            return null;
        }

        try
        {
            var text = StrictUtf8.GetString(buffer, 0, written);
            return text.Length == 0 ? null : text;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Fixed(long value, long scale, int places)
    {
        // The magnitude of long.MinValue does not fit a long; work in unsigned so every stored value formats.
        var magnitude = value < 0 ? unchecked((ulong)(-(value + 1))) + 1UL : (ulong)value;
        var whole = magnitude / (ulong)scale;
        var fraction = magnitude % (ulong)scale;
        return string.Create(CultureInfo.InvariantCulture, $"{(value < 0 ? "-" : string.Empty)}{whole}.{fraction.ToString("D" + places, CultureInfo.InvariantCulture)}");
    }
}
