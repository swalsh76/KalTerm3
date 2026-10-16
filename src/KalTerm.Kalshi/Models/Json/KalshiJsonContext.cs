using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace KalTerm.Kalshi.Models.Json;

/// <summary>
/// Source-generated JSON metadata for every Kalshi model: no reflection, trim-safe, snake_case property names. Strict where
/// it matters: a missing required field, a null for a non-nullable property or a wrong type is an error; unknown fields
/// are ignored because Kalshi adds fields over time.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    Converters =
    [
        typeof(StrictDateTimeOffsetConverter),
        typeof(DollarsJsonConverter),
        typeof(ContractsJsonConverter),
        typeof(LevelJsonConverter),
        typeof(FeeMultiplierJsonConverter),
    ])]
[JsonSerializable(typeof(ExchangeStatus))]
[JsonSerializable(typeof(Market))]
[JsonSerializable(typeof(KalshiEvent))]
[JsonSerializable(typeof(Series))]
[JsonSerializable(typeof(Trade))]
[JsonSerializable(typeof(MarketsPage))]
[JsonSerializable(typeof(TradesPage))]
[JsonSerializable(typeof(EventsPage))]
[JsonSerializable(typeof(SeriesList))]
[JsonSerializable(typeof(OrderBook))]
internal sealed partial class KalshiJsonContext : JsonSerializerContext
{
}

/// <summary>Deserialises a response body, turning any JSON problem into a <see cref="KalshiResponseException"/>.</summary>
internal static class KalshiJson
{
    internal static T Parse<T>(string json, JsonTypeInfo<T> typeInfo, string what)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(typeInfo);

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo) ?? throw new KalshiResponseException($"{what}: the response body is empty");
        }
        catch (JsonException ex)
        {
            // A converter's own error keeps the field path on JsonException.Path but not in its message: say which field.
            var where = string.IsNullOrEmpty(ex.Path) || ex.Message.Contains(ex.Path, StringComparison.Ordinal) ? string.Empty : $" (at {ex.Path})";
            throw new KalshiResponseException($"{what}: {ex.Message}{where}", ex);
        }
    }
}
