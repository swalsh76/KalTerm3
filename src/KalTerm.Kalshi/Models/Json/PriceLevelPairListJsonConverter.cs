using System.Text.Json;
using System.Text.Json.Serialization;

namespace KalTerm.Kalshi.Models.Json;

/// <summary>
/// Reads and writes a list of order book levels in Kalshi's wire shape, an array of <c>[price, quantity]</c> pairs (the
/// WebSocket snapshot). Each value is read exactly; a row that is not a two-element array is an error.
/// </summary>
internal sealed class PriceLevelPairListJsonConverter : JsonConverter<IReadOnlyList<PriceLevel>>
{
    public override IReadOnlyList<PriceLevel> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var rows = JsonElement.ParseValue(ref reader);
        if (rows.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("levels must be an array of [price, quantity] pairs");
        }

        var levels = new List<PriceLevel>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 2)
            {
                throw new JsonException("each level must be a [price, quantity] pair");
            }

            levels.Add(new PriceLevel(FixedPointJson.ReadDollars(row[0]), FixedPointJson.ReadContracts(row[1])));
        }

        return levels;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<PriceLevel> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var level in value)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(level.Price.ToString());
            writer.WriteStringValue(level.Quantity.ToString());
            writer.WriteEndArray();
        }

        writer.WriteEndArray();
    }
}
