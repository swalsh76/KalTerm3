using System.Text.Json;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Models.Json;

namespace KalTerm.Kalshi.Models;

/// <summary>One order book level: a bid price in dollars and the contract quantity resting there.</summary>
/// <param name="Price">The bid price.</param>
/// <param name="Quantity">The contracts resting at that price.</param>
public sealed record PriceLevel(Dollars Price, Contracts Quantity);

/// <summary>
/// An order book. Bids only: a YES bid at X is equivalent to a NO ask at 1 - X.
/// </summary>
/// <param name="Yes">Yes bids.</param>
/// <param name="No">No bids.</param>
public sealed record OrderBook(IReadOnlyList<PriceLevel> Yes, IReadOnlyList<PriceLevel> No)
{
    /// <summary>
    /// Reads the <c>GET /markets/{ticker}/orderbook</c> response: <c>{"orderbook_fp": {"yes_dollars": [[price, quantity], ...],
    /// "no_dollars": [...]}}</c>. A missing or null side is empty. Throws <see cref="KalshiResponseException"/> for anything else.
    /// </summary>
    public static OrderBook FromResponse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("orderbook_fp", out var book)
                || book.ValueKind != JsonValueKind.Object)
            {
                throw new KalshiResponseException("order book response has no orderbook_fp object");
            }

            return new OrderBook(Levels(book, "yes_dollars"), Levels(book, "no_dollars"));
        }
        catch (JsonException ex)
        {
            throw new KalshiResponseException($"order book response is not valid: {ex.Message}", ex);
        }
    }

    private static List<PriceLevel> Levels(JsonElement book, string side)
    {
        var levels = new List<PriceLevel>();
        if (!book.TryGetProperty(side, out var rows) || rows.ValueKind == JsonValueKind.Null)
        {
            return levels;
        }

        if (rows.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"{side} must be an array of [price, quantity] pairs");
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 2)
            {
                throw new JsonException($"each {side} level must be a [price, quantity] pair");
            }

            levels.Add(new PriceLevel(FixedPointJson.ReadDollars(row[0]), FixedPointJson.ReadContracts(row[1])));
        }

        return levels;
    }
}
