using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KalTerm.Kalshi.Models.Json;

/// <summary>
/// Reads an ISO 8601 date-time that states its UTC offset (<c>Z</c> or <c>+hh:mm</c>). A timestamp with no offset, a bare date,
/// a space instead of the <c>T</c>, or a number of epoch seconds is refused: it could mean different instants on different
/// machines, and the stock reader would quietly take it as local time. Up to nine fractional digits are accepted; the
/// instant keeps 100 ns resolution.
/// </summary>
internal sealed partial class StrictDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const int TickDigits = 7;

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"expected an ISO 8601 date-time string, found {reader.TokenType}");
        }

        var text = reader.GetString()!;
        var match = Iso8601().Match(text);
        if (!match.Success)
        {
            throw new JsonException($"'{text}' is not an ISO 8601 date-time with a UTC offset");
        }

        try
        {
            var fraction = match.Groups["fraction"].Success
                ? long.Parse(match.Groups["fraction"].Value.PadRight(TickDigits, '0')[..TickDigits], CultureInfo.InvariantCulture)
                : 0;
            var offset = match.Groups["zone"].Value == "Z"
                ? TimeSpan.Zero
                : new TimeSpan(
                    int.Parse(match.Groups["zone"].Value[1..3], CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["zone"].Value[4..6], CultureInfo.InvariantCulture),
                    0) * (match.Groups["zone"].Value[0] == '-' ? -1 : 1);

            return new DateTimeOffset(
                int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture),
                offset).AddTicks(fraction);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException($"'{text}' is not a real date-time: {ex.Message}", ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));

    [GeneratedRegex(
        @"^(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})T(?<hour>\d{2}):(?<minute>\d{2}):(?<second>\d{2})(?:\.(?<fraction>\d{1,9}))?(?<zone>Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Iso8601();
}
