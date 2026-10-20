using KalTerm.Contracts;
using KalTerm.Data.Books;
using KalTerm.Data.Candles;
using KalTerm.Data.Metrics;
using KalTerm.Data.Reading;

namespace KalTerm.Api;

/// <summary>Turns stored rows into the contract models: the one place where stored integers become the API's decimal strings and times.</summary>
public static class Mapping
{
    /// <summary>A catalog market.</summary>
    /// <param name="market">The stored market.</param>
    /// <returns>The model.</returns>
    public static MarketDto ToDto(StoredMarket market)
    {
        ArgumentNullException.ThrowIfNull(market);
        return new MarketDto(
            market.Ticker,
            market.EventTicker,
            market.MarketType,
            market.Status,
            market.YesSubTitle,
            market.NoSubTitle,
            ApiFormat.Moment(market.OpenTime),
            ApiFormat.Moment(market.CloseTime),
            ApiFormat.Moment(market.LatestExpirationTime),
            market.Result,
            ApiFormat.Dollars(market.SettlementValueE6),
            ApiFormat.Moment(market.SettlementTs),
            ApiFormat.Moment(market.FirstSeenAt),
            ApiFormat.Moment(market.LastSeenAt));
    }

    /// <summary>A ticker update.</summary>
    /// <param name="tick">The stored update.</param>
    /// <returns>The model.</returns>
    public static TickDto ToDto(StoredTick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        return new TickDto(
            ApiFormat.Moment(tick.Ts),
            ApiFormat.Dollars(tick.PriceE6),
            ApiFormat.Dollars(tick.YesBidE6),
            ApiFormat.Dollars(tick.YesAskE6),
            ApiFormat.Count(tick.YesBidSizeE2),
            ApiFormat.Count(tick.YesAskSizeE2),
            ApiFormat.Count(tick.VolumeE2),
            ApiFormat.Count(tick.OpenInterestE2),
            ApiFormat.Count(tick.LastTradeSizeE2));
    }

    /// <summary>A candle.</summary>
    /// <param name="candle">The candle.</param>
    /// <returns>The model.</returns>
    public static CandleDto ToDto(StoredCandle candle)
    {
        ArgumentNullException.ThrowIfNull(candle);
        return new CandleDto(
            ApiFormat.Moment(candle.BucketStart)!,
            ApiFormat.Dollars(candle.OpenE6),
            ApiFormat.Dollars(candle.HighE6),
            ApiFormat.Dollars(candle.LowE6),
            ApiFormat.Dollars(candle.CloseE6),
            ApiFormat.Count(candle.VolumeE2)!,
            candle.TradeCount,
            ApiFormat.Dollars(candle.YesBidE6),
            ApiFormat.Dollars(candle.YesAskE6),
            ApiFormat.Count(candle.OpenInterestE2),
            candle.Partial,
            candle.TradesPartial,
            candle.BookPartial,
            candle.GapKind,
            candle.GapSeconds);
    }

    /// <summary>The metrics of a market.</summary>
    /// <param name="ticker">The market.</param>
    /// <param name="metrics">The computed metrics.</param>
    /// <returns>The model.</returns>
    public static MetricsDto ToDto(string ticker, MarketMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return new MetricsDto(
            ticker,
            ApiFormat.Moment(metrics.AsOf),
            ApiFormat.Moment(metrics.LastTime),
            ApiFormat.Dollars(metrics.LastE6),
            ApiFormat.Dollars(metrics.Change24hE6),
            ApiFormat.Count(metrics.VolumeE2)!,
            metrics.TradeCount,
            ApiFormat.Count(metrics.OpenInterestE2),
            ApiFormat.Dollars(metrics.YesBidE6),
            ApiFormat.Dollars(metrics.YesAskE6),
            ApiFormat.Dollars(metrics.SpreadE6),
            ApiFormat.Dollars(metrics.Vwap24hE6),
            ApiFormat.Dollars(metrics.Sma20E6),
            ApiFormat.Dollars(metrics.Sma50E6),
            ApiFormat.Dollars(metrics.Volatility24hE6),
            metrics.Partial);
    }

    /// <summary>A trade.</summary>
    /// <param name="trade">The stored trade.</param>
    /// <returns>The model.</returns>
    public static TradeDto ToDto(StoredTrade trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        return new TradeDto(
            ApiFormat.Moment(trade.Ts),
            trade.TradeId,
            ApiFormat.Dollars(trade.YesPriceE6)!,
            ApiFormat.Dollars(trade.NoPriceE6)!,
            ApiFormat.Count(trade.CountE2)!,
            trade.TakerSide switch { 1 => "yes", 2 => "no", _ => null },
            trade.IsBlock);
    }

    /// <summary>A published order book, cut to <paramref name="depth"/> levels a side.</summary>
    /// <param name="book">The stored book.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="depth">The most levels per side.</param>
    /// <returns>The model.</returns>
    public static OrderBookDto ToDto(StoredBook book, DateTime now, int depth)
    {
        ArgumentNullException.ThrowIfNull(book);
        return new OrderBookDto(
            book.Ticker,
            ApiFormat.Moment(book.UpdatedAt),
            ApiFormat.Moment(book.PublishedAt),
            Math.Max(0, (long)(now - book.PublishedAt).TotalSeconds),
            book.Stale,
            book.Approximate,
            [.. book.YesLevels.Take(depth).Select(Level)],
            [.. book.NoLevels.Take(depth).Select(Level)]);
    }

    private static BookLevelDto Level(BookLevel level) => new(ApiFormat.Dollars(level.PriceE6)!, ApiFormat.Count(level.QuantityE2)!);

    /// <summary>A gap.</summary>
    /// <param name="gap">The stored gap.</param>
    /// <returns>The model.</returns>
    public static GapDto ToDto(StoredGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        return new GapDto(
            gap.Id,
            gap.Kind,
            ApiFormat.Moment(gap.StartedAt),
            ApiFormat.Moment(gap.EndedAt),
            gap.Detail,
            new BackfillDto(gap.BackfillStatus, gap.BackfillFound, gap.BackfillAdded, ApiFormat.Moment(gap.BackfillAt), gap.BackfillNote));
    }
}
