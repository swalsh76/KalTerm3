using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Models.Json;

namespace KalTerm.Kalshi.Tests.Models;

/// <summary>
/// Differential tests for the public WebSocket messages: each payload ran through the reference's pydantic models
/// (ws_models.json), same rules as <see cref="ModelVectorTests"/>. The envelope case compares the envelope plus its typed
/// payload.
/// </summary>
public class WsModelVectorTests
{
    private const string File = "ws_models.json";

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in VectorFile.Load(File).GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("model").GetString()!, c.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WsModelsMatchTheReference(string model, string name)
    {
        var entry = VectorFile.Load(File).GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("model").GetString() == model && c.GetProperty("name").GetString() == name);
        var json = entry.GetProperty("payload").GetRawText();
        var expected = entry.GetProperty("expect");
        var portRefuses = entry.TryGetProperty("port", out var port) && port.GetString() == "error";

        if (expected.ValueKind == JsonValueKind.String && expected.GetString() == "error" || portRefuses)
        {
            Assert.Throws<KalshiResponseException>(() => Run(model, json));
            return;
        }

        ModelVectorTests.Compare(expected, Run(model, json), path: model, name: string.Empty);
    }

    [Fact]
    public void TheVectorFileHasTheExpectedBreadth()
    {
        var file = VectorFile.Load(File);
        var cases = file.GetProperty("cases").EnumerateArray().ToList();

        Assert.True(cases.Count >= 300);
        Assert.Equal(
            ["EventLifecycleMsg", "LifecycleMsg", "OrderbookDeltaMsg", "OrderbookSnapshotMsg", "TickerMsg", "TradeMsg", "WsMessage"],
            cases.Select(c => c.GetProperty("model").GetString()!).Distinct().Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(cases, c => c.TryGetProperty("port", out _));
        Assert.Equal(cases.Count, cases.Select(c => (c.GetProperty("model").GetString(), c.GetProperty("name").GetString())).Distinct().Count());
        Assert.Matches("^[0-9a-f]{40}$", file.GetProperty("referenceCommit").GetString());
    }

    private static JsonElement Run(string model, string json)
    {
        var ctx = KalshiJsonContext.Default;
        return model switch
        {
            "TickerMsg" => Round(json, ctx.TickerMsg),
            "TradeMsg" => Round(json, ctx.TradeMsg),
            "LifecycleMsg" => Round(json, ctx.LifecycleMsg),
            "EventLifecycleMsg" => Round(json, ctx.EventLifecycleMsg),
            "OrderbookSnapshotMsg" => Round(json, ctx.OrderbookSnapshotMsg),
            "OrderbookDeltaMsg" => Round(json, ctx.OrderbookDeltaMsg),
            "WsMessage" => Envelope(json),
            _ => throw new InvalidOperationException($"unknown model {model}"),
        };
    }

    private static JsonElement Round<T>(string json, JsonTypeInfo<T> typeInfo)
        where T : class =>
        JsonSerializer.SerializeToElement(KalshiJson.Parse(json, typeInfo, typeof(T).Name), typeInfo);

    /// <summary>The envelope as the reference dumps it, with the typed payload (or null) beside it.</summary>
    private static JsonElement Envelope(string json)
    {
        var ctx = KalshiJsonContext.Default;
        var message = KalshiJson.Parse(json, ctx.WsMessage, nameof(WsMessage));
        var payload = message.Payload();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in JsonSerializer.SerializeToElement(message, ctx.WsMessage).EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WritePropertyName("payload");
            switch (payload)
            {
                case null: writer.WriteNullValue(); break;
                case TickerMsg p: JsonSerializer.Serialize(writer, p, ctx.TickerMsg); break;
                case TradeMsg p: JsonSerializer.Serialize(writer, p, ctx.TradeMsg); break;
                case LifecycleMsg p: JsonSerializer.Serialize(writer, p, ctx.LifecycleMsg); break;
                case EventLifecycleMsg p: JsonSerializer.Serialize(writer, p, ctx.EventLifecycleMsg); break;
                case OrderbookSnapshotMsg p: JsonSerializer.Serialize(writer, p, ctx.OrderbookSnapshotMsg); break;
                case OrderbookDeltaMsg p: JsonSerializer.Serialize(writer, p, ctx.OrderbookDeltaMsg); break;
                default: throw new InvalidOperationException($"unexpected payload {payload.GetType().Name}");
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
