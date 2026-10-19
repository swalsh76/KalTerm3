using System.Globalization;
using System.Text.RegularExpressions;
using KalTerm.Contracts;
using KalTerm.Data.Reading;
using Microsoft.AspNetCore.Http;

namespace KalTerm.Api;

/// <summary>
/// Reads and checks the query string. Nothing a client sends is trusted: every parameter has a shape and a bound, a bad one is named in the error
/// (never echoed), and a parameter given twice is a bad one.
/// </summary>
internal sealed partial class RequestParameters(IQueryCollection query)
{
    public const int MaxCursorLength = 300;
    public const int MaxTradeIdLength = 64;
    public const int MaxSkip = 1_000_000;

    private readonly List<string> _invalid = [];

    public IReadOnlyList<string> Invalid => _invalid;

    public bool IsValid => _invalid.Count == 0;

    /// <summary>A ticker or event ticker.</summary>
    public static bool IsTicker(string text) => Ticker().IsMatch(text);

    /// <summary>Parses a time that carries its offset (<c>Z</c> or <c>+hh:mm</c>); a time without one is ambiguous and refused. The result is UTC.</summary>
    public static bool TryParseMoment(string text, out DateTime utc)
    {
        utc = default;
        if (!Offset().IsMatch(text) || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        utc = parsed.UtcDateTime;
        return true;
    }

    /// <summary>The page size: absent gives the default; present must be a whole number from 1 to the maximum.</summary>
    public int Limit(int defaultValue, int max)
    {
        if (Single("limit") is not { } text)
        {
            return defaultValue;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1 || value > max)
        {
            _invalid.Add("limit");
            return defaultValue;
        }

        return value;
    }

    /// <summary>How many order book levels a side: absent gives the default; present must be a whole number from 1 to the maximum.</summary>
    public int Depth(int defaultValue, int max)
    {
        if (Single("depth") is not { } text)
        {
            return defaultValue;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1 || value > max)
        {
            _invalid.Add("depth");
            return defaultValue;
        }

        return value;
    }

    /// <summary>A lowercase status such as <c>active</c>.</summary>
    public string? Status() => Matching("status", StatusShape());

    /// <summary>An event ticker.</summary>
    public string? Event() => Matching("event", Ticker());

    /// <summary>A search text of 2 to 64 printable characters.</summary>
    public string? Search()
    {
        if (Single("q") is not { } text)
        {
            return null;
        }

        if (text.Length is < 2 or > 64 || text.Any(char.IsControl))
        {
            _invalid.Add("q");
            return null;
        }

        return text;
    }

    /// <summary>A time with its offset, or <see langword="null"/> when absent.</summary>
    public DateTime? Moment(string name)
    {
        if (Single(name) is not { } text)
        {
            return null;
        }

        if (!TryParseMoment(text, out var utc))
        {
            _invalid.Add(name);
            return null;
        }

        return utc;
    }

    /// <summary>The text inside the cursor, or <see langword="null"/> when there is none; a cursor that is not ours is invalid.</summary>
    public string? Cursor()
    {
        if (Single("cursor") is not { } text)
        {
            return null;
        }

        if (text.Length > MaxCursorLength || ApiFormat.DecodeCursor(text) is not { } inside)
        {
            _invalid.Add("cursor");
            return null;
        }

        return inside;
    }

    public MarketPosition? MarketCursor()
    {
        if (Cursor() is not { } text)
        {
            return null;
        }

        if (!IsTicker(text))
        {
            _invalid.Add("cursor");
            return null;
        }

        return new MarketPosition(text);
    }

    public TickPosition? TickCursor()
    {
        if (Cursor() is not { } text)
        {
            return null;
        }

        var parts = text.Split('|');
        if (parts.Length == 2 && TryParseMoment(parts[0], out var ts) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var skip) && skip <= MaxSkip)
        {
            return new TickPosition(ts, skip);
        }

        _invalid.Add("cursor");
        return null;
    }

    public TradePosition? TradeCursor()
    {
        if (Cursor() is not { } text)
        {
            return null;
        }

        var split = text.IndexOf('|', StringComparison.Ordinal);
        if (split > 0 && TryParseMoment(text[..split], out var ts) && TradeId().IsMatch(text[(split + 1)..]))
        {
            return new TradePosition(ts, text[(split + 1)..]);
        }

        _invalid.Add("cursor");
        return null;
    }

    /// <summary>Names a parameter as wrong; used for checks that span several parameters.</summary>
    public void Reject(params string[] names)
    {
        foreach (var name in names)
        {
            if (!_invalid.Contains(name))
            {
                _invalid.Add(name);
            }
        }
    }

    /// <summary>The window's end: the given one, or a second past now so a row written this instant is included.</summary>
    public static DateTime EndOrNow(DateTime? end, DateTime now) => end ?? now.AddSeconds(1);

    public string? Matching(string name, Regex shape)
    {
        if (Single(name) is not { } text)
        {
            return null;
        }

        if (!shape.IsMatch(text))
        {
            _invalid.Add(name);
            return null;
        }

        return text;
    }

    /// <summary>The one value of a parameter; <see langword="null"/> when absent, and invalid (also <see langword="null"/>) when given more than once.</summary>
    private string? Single(string name)
    {
        var values = query[name];
        switch (values.Count)
        {
            case 0:
                return null;
            case 1:
                return values[0];
            default:
                if (!_invalid.Contains(name))
                {
                    _invalid.Add(name);
                }

                return null;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ticker();

    [GeneratedRegex("^[a-z_]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex StatusShape();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TradeId();

    [GeneratedRegex("(Z|z|[+-][0-9]{2}:?[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Offset();
}
