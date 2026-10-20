using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KalTerm.Contracts;

/// <summary>
/// The JSON the API speaks: snake_case names, nulls written, no indentation. Source-generated, so serialization needs no reflection and every type
/// that crosses the API must be listed here (a test enforces it).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(MarketDto))]
[JsonSerializable(typeof(MarketPage))]
[JsonSerializable(typeof(TickDto))]
[JsonSerializable(typeof(TickPage))]
[JsonSerializable(typeof(CandleDto))]
[JsonSerializable(typeof(CandlePage))]
[JsonSerializable(typeof(TradeDto))]
[JsonSerializable(typeof(TradePage))]
[JsonSerializable(typeof(BackfillDto))]
[JsonSerializable(typeof(GapDto))]
[JsonSerializable(typeof(GapPage))]
[JsonSerializable(typeof(IngestStateDto))]
[JsonSerializable(typeof(StreamDto))]
[JsonSerializable(typeof(StreamsDto))]
[JsonSerializable(typeof(StorageDto))]
[JsonSerializable(typeof(GapSummaryDto))]
[JsonSerializable(typeof(BackupStatusDto))]
[JsonSerializable(typeof(BooksStatusDto))]
[JsonSerializable(typeof(BookLevelDto))]
[JsonSerializable(typeof(OrderBookDto))]
[JsonSerializable(typeof(OrderBookPage))]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(HealthDto))]
[JsonSerializable(typeof(ErrorDto))]
public sealed partial class ApiJson : JsonSerializerContext
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // The API never embeds its JSON in HTML, so '+' in a time stays '+' (as the reference sends it) instead of becoming +.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The context every API response and client read uses.</summary>
    public static ApiJson Wire { get; } = new(WireOptions);

    /// <summary>Serializes a value the way the API sends it.</summary>
    /// <typeparam name="T">A contract type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, typeof(T), Wire);

    /// <summary>Reads a value the way a client does.</summary>
    /// <typeparam name="T">A contract type.</typeparam>
    /// <param name="json">The JSON text.</param>
    /// <returns>The value, or <see langword="null"/> for JSON <c>null</c>.</returns>
    public static T? Deserialize<T>(string json)
        where T : class => (T?)JsonSerializer.Deserialize(json, typeof(T), Wire);
}
