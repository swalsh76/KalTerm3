using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KalTerm.Core.Fees;
using KalTerm.Core.Money;

namespace KalTerm.Kalshi.Models.Json;

/// <summary>
/// Reads a fixed-point value exactly from JSON text. Kalshi sends prices and counts as strings ("0.5600", "10.00");
/// a JSON number is accepted too and read from its token text, so no value ever passes through a floating-point type.
/// A value finer than the type's scale, or not a number, is a <see cref="JsonException"/>: refused, never rounded.
/// Written back as a fixed-point string.
/// </summary>
internal abstract class FixedPointJsonConverter<T> : JsonConverter<T>
    where T : struct
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()!,
            JsonTokenType.Number => Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray()),
            _ => throw new JsonException($"expected a fixed-point number as a string or a number, found {reader.TokenType}"),
        };

        return ParseExact(text);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));

    internal T ParseExact(string text)
    {
        try
        {
            return Parse(text);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            throw new JsonException($"'{text}' is not an exact {typeof(T).Name} value: {ex.Message}", ex);
        }
    }

    protected abstract T Parse(string text);

    protected abstract string Format(T value);
}

internal sealed class DollarsJsonConverter : FixedPointJsonConverter<Dollars>
{
    protected override Dollars Parse(string text) => Dollars.Parse(text);

    protected override string Format(Dollars value) => value.ToString();
}

internal sealed class ContractsJsonConverter : FixedPointJsonConverter<Contracts>
{
    protected override Contracts Parse(string text) => Contracts.Parse(text);

    protected override string Format(Contracts value) => value.ToString();
}

internal sealed class LevelJsonConverter : FixedPointJsonConverter<Level>
{
    protected override Level Parse(string text) => Level.Parse(text);

    protected override string Format(Level value) => value.ToString();
}

internal sealed class FeeMultiplierJsonConverter : FixedPointJsonConverter<FeeMultiplier>
{
    protected override FeeMultiplier Parse(string text) => FeeMultiplier.Parse(text);

    protected override string Format(FeeMultiplier value) => value.ToString();
}

/// <summary>Reads fixed-point values out of a parsed <see cref="JsonElement"/> with the same exactness rules.</summary>
internal static class FixedPointJson
{
    internal static Dollars ReadDollars(JsonElement element) => new DollarsJsonConverter().ParseExact(Text(element));

    internal static Contracts ReadContracts(JsonElement element) => new ContractsJsonConverter().ParseExact(Text(element));

    private static string Text(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Number => element.GetRawText(),
        _ => throw new JsonException($"expected a fixed-point number as a string or a number, found {element.ValueKind}"),
    };
}
