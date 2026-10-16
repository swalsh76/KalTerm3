using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Models.Json;

namespace KalTerm.Kalshi.Tests.Models;

/// <summary>
/// Differential tests: each payload ran through the reference's pydantic models (models.json). The port must either agree
/// field for field, or refuse it where the case is marked as a deliberate difference. Prices and counts are compared
/// numerically (the reference keeps trailing zeros; the port prints fixed-point), datetimes as instants, and the key sets
/// must be identical.
/// </summary>
public class ModelVectorTests
{
    private static readonly string[] NumericSuffixes = ["_dollars", "_fp", "_strike", "fee_multiplier", "price", "quantity"];
    private static readonly string[] TimeSuffixes = ["_time", "_ts", "_date"];

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in VectorFile.Load("models.json").GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("model").GetString()!, c.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ModelsMatchTheReference(string model, string name)
    {
        var entry = VectorFile.Load("models.json").GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("model").GetString() == model && c.GetProperty("name").GetString() == name);
        var json = entry.GetProperty("payload").GetRawText();
        var expected = entry.GetProperty("expect");
        var portRefuses = entry.TryGetProperty("port", out var port) && port.GetString() == "error";

        if (expected.ValueKind == JsonValueKind.String && expected.GetString() == "error" || portRefuses)
        {
            Assert.Throws<KalshiResponseException>(() => Run(model, json));
            return;
        }

        var actual = Run(model, json);

        Compare(expected, actual, path: model, name: string.Empty);
    }

    [Fact]
    public void TheVectorFileHasTheExpectedBreadth()
    {
        var file = VectorFile.Load("models.json");
        var cases = file.GetProperty("cases").EnumerateArray().ToList();

        Assert.True(cases.Count >= 200);
        Assert.Equal(
            ["Event", "EventsPage", "ExchangeStatus", "Market", "MarketsPage", "OrderBook", "Series", "SeriesList", "Trade", "TradesPage"],
            cases.Select(c => c.GetProperty("model").GetString()!).Distinct().Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(cases, c => c.TryGetProperty("port", out _));
        Assert.Equal(cases.Count, cases.Select(c => (c.GetProperty("model").GetString(), c.GetProperty("name").GetString())).Distinct().Count());
        Assert.Matches("^[0-9a-f]{40}$", file.GetProperty("referenceCommit").GetString());
    }

    private static JsonElement Run(string model, string json) => model switch
    {
        "Market" => Round(json, KalshiJsonContext.Default.Market),
        "Event" => Round(json, KalshiJsonContext.Default.KalshiEvent),
        "Series" => Round(json, KalshiJsonContext.Default.Series),
        "Trade" => Round(json, KalshiJsonContext.Default.Trade),
        "MarketsPage" => Round(json, KalshiJsonContext.Default.MarketsPage),
        "TradesPage" => Round(json, KalshiJsonContext.Default.TradesPage),
        "EventsPage" => Round(json, KalshiJsonContext.Default.EventsPage),
        "SeriesList" => Round(json, KalshiJsonContext.Default.SeriesList),
        "ExchangeStatus" => Round(json, KalshiJsonContext.Default.ExchangeStatus),
        "OrderBook" => JsonSerializer.SerializeToElement(OrderBook.FromResponse(json), KalshiJsonContext.Default.OrderBook),
        _ => throw new InvalidOperationException($"unknown model {model}"),
    };

    private static JsonElement Round<T>(string json, JsonTypeInfo<T> typeInfo)
        where T : class =>
        JsonSerializer.SerializeToElement(KalshiJson.Parse(json, typeInfo, typeof(T).Name), typeInfo);

    private static void Compare(JsonElement expected, JsonElement actual, string path, string name)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.True(actual.ValueKind == JsonValueKind.Object, $"{path}: expected an object, got {actual.ValueKind}");
                Assert.Equal(
                    expected.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
                    actual.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
                foreach (var property in expected.EnumerateObject())
                {
                    Compare(property.Value, actual.GetProperty(property.Name), $"{path}.{property.Name}", property.Name);
                }

                break;
            case JsonValueKind.Array:
                Assert.True(actual.ValueKind == JsonValueKind.Array, $"{path}: expected an array, got {actual.ValueKind}");
                Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (var i = 0; i < expected.GetArrayLength(); i++)
                {
                    Compare(expected[i], actual[i], $"{path}[{i}]", name);
                }

                break;
            case JsonValueKind.Null:
                Assert.True(actual.ValueKind == JsonValueKind.Null, $"{path}: expected null, got {actual.GetRawText()}");
                break;
            default:
                CompareLeaf(expected, actual, path, name);
                break;
        }
    }

    private static void CompareLeaf(JsonElement expected, JsonElement actual, string path, string name)
    {
        if (NumericSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)))
        {
            Assert.Equal(Decimal(expected), Decimal(actual));
            return;
        }

        if (TimeSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal)))
        {
            // A Python datetime holds microseconds; the port keeps 100 ns. Compare at the coarser resolution.
            Assert.Equal(Instant(expected).UtcTicks / 10, Instant(actual).UtcTicks / 10);
            return;
        }

        Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: expected {expected.ValueKind}, got {actual.ValueKind}");
        if (expected.ValueKind == JsonValueKind.Number)
        {
            Assert.Equal(Decimal(expected), Decimal(actual));
        }
        else
        {
            Assert.Equal(expected.ToString(), actual.ToString());
        }
    }

    private static decimal Decimal(JsonElement element) =>
        decimal.Parse(element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(JsonElement element) =>
        DateTimeOffset.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
