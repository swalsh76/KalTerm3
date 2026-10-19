using KalTerm.Data.Writing;
using KalTerm.Kalshi.Models;

namespace KalTerm.Ingest;

/// <summary>Maps the WebSocket data messages onto the rows the writers store.</summary>
public static class StreamMapper
{
    /// <summary>The row's time: the server's timestamp when the message carried a usable one, else the time it was received (UTC).</summary>
    /// <param name="tsMs">Server time in epoch milliseconds, if sent.</param>
    /// <param name="received">When the client read the message.</param>
    /// <returns>A UTC time.</returns>
    public static DateTime Timestamp(long? tsMs, DateTimeOffset received)
    {
        if (tsMs is { } ms and > 0)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                // A value outside the calendar: fall back to the receive time.
            }
        }

        return received.UtcDateTime;
    }

    /// <summary>Converts a ticker message; prices become dollars at 10^-6 and sizes contracts at 10^-2.</summary>
    /// <param name="message">The message.</param>
    /// <param name="marketId">The stored id of its market.</param>
    /// <param name="received">When the client read the message.</param>
    /// <returns>The row.</returns>
    public static TickerRow ToRow(TickerMsg message, int marketId, DateTimeOffset received)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new TickerRow(
            Timestamp(message.TsMs, received),
            marketId,
            message.PriceDollars?.Micros,
            message.YesBidDollars?.Micros,
            message.YesAskDollars?.Micros,
            message.YesBidSizeFp?.Hundredths,
            message.YesAskSizeFp?.Hundredths,
            message.VolumeFp?.Hundredths,
            message.OpenInterestFp?.Hundredths,
            message.LastTradeSizeFp?.Hundredths);
    }

    /// <summary>Converts a trade message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="marketId">The stored id of its market.</param>
    /// <param name="received">When the client read the message.</param>
    /// <returns>The row.</returns>
    public static TradeRow ToRow(TradeMsg message, int marketId, DateTimeOffset received)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new TradeRow(
            Timestamp(message.TsMs, received),
            marketId,
            message.TradeId,
            message.YesPriceDollars.Micros,
            message.NoPriceDollars.Micros,
            message.CountFp.Hundredths,
            TakerSideOf(message.TakerSide),
            message.IsBlockTrade);
    }

    /// <summary>Maps a trade read from REST (whole-second time) onto its stored row.</summary>
    /// <param name="trade">The REST trade.</param>
    /// <param name="marketId">The market's id.</param>
    /// <returns>The row.</returns>
    public static TradeRow ToRow(Trade trade, int marketId)
    {
        ArgumentNullException.ThrowIfNull(trade);
        return new TradeRow(
            trade.CreatedTime.UtcDateTime,
            marketId,
            trade.TradeId,
            trade.YesPriceDollars.Micros,
            trade.NoPriceDollars.Micros,
            trade.CountFp.Hundredths,
            TakerSideOf(trade.TakerSide),
            trade.IsBlockTrade);
    }

    private static TakerSide TakerSideOf(string? side) => side?.ToUpperInvariant() switch
    {
        "YES" => TakerSide.Yes,
        "NO" => TakerSide.No,
        _ => TakerSide.Unknown,
    };
}
