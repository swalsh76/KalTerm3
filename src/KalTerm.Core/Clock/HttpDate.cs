using System.Globalization;
using System.Text.RegularExpressions;

namespace KalTerm.Core.Clock;

/// <summary>
/// Parses an HTTP <c>Date</c> header. Accepts exactly the three formats HTTP requires receivers to take
/// (RFC 7231 section 7.1.1.1): the standard <c>Sun, 06 Nov 1994 08:49:37 GMT</c>, the obsolete RFC 850
/// <c>Sunday, 06-Nov-94 08:49:37 GMT</c>, and asctime <c>Sun Nov  6 08:49:37 1994</c>. Matching is case-insensitive;
/// the weekday must be a real weekday name but is not cross-checked against the date. Anything else, including numeric
/// zones, a missing zone or surrounding whitespace, is refused.
/// </summary>
public static partial class HttpDate
{
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>Throws <see cref="ClockCheckException"/> if the text is not one of the accepted formats or not a real date.</summary>
    public static DateTimeOffset Parse(string? text) =>
        TryParse(text, out var value) ? value : throw new ClockCheckException($"unparseable Date header: '{text}'");

    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var match = StandardFormat().Match(text);
        if (!match.Success)
        {
            match = Rfc850Format().Match(text);
        }

        if (!match.Success)
        {
            match = AscTimeFormat().Match(text);
        }

        if (!match.Success)
        {
            return false;
        }

        var month = Array.IndexOf(Months, match.Groups["month"].Value.ToLowerInvariant()) + 1;
        var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        if (match.Groups["year"].Length == 2)
        {
            year += year > 68 ? 1900 : 2000;   // the pivot the reference's date parser uses
        }

        try
        {
            value = new DateTimeOffset(
                year,
                month,
                int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture),
                TimeSpan.Zero);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;   // 32 October, hour 25, a leap second: not a real instant
        }
    }

    [GeneratedRegex(
        @"^(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun), (?<day>\d{1,2}) (?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) (?<year>\d{4}) (?<hour>\d{2}):(?<minute>\d{2}):(?<second>\d{2}) GMT$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandardFormat();

    [GeneratedRegex(
        @"^(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), (?<day>\d{1,2})-(?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)-(?<year>\d{2}) (?<hour>\d{2}):(?<minute>\d{2}):(?<second>\d{2}) GMT$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Rfc850Format();

    [GeneratedRegex(
        @"^(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun) (?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) +(?<day>\d{1,2}) (?<hour>\d{2}):(?<minute>\d{2}):(?<second>\d{2}) (?<year>\d{4})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AscTimeFormat();
}
