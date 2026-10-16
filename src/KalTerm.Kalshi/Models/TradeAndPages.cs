using KalTerm.Core.Money;

namespace KalTerm.Kalshi.Models;

/// <summary>One executed trade from <c>GET /markets/trades</c> (all markets, combos included).</summary>
/// <param name="TradeId">Kalshi's trade id.</param>
/// <param name="Ticker">The market.</param>
/// <param name="YesPriceDollars">The yes price.</param>
/// <param name="NoPriceDollars">The no price.</param>
/// <param name="CountFp">Contracts traded.</param>
/// <param name="CreatedTime">When it happened; microsecond precision is preserved.</param>
/// <param name="TakerSide">The side that took liquidity.</param>
/// <param name="IsBlockTrade">Whether it was a block trade.</param>
public sealed record Trade(
    string TradeId,
    string Ticker,
    Dollars YesPriceDollars,
    Dollars NoPriceDollars,
    Contracts CountFp,
    DateTimeOffset CreatedTime,
    string? TakerSide = null,
    bool IsBlockTrade = false);

/// <summary>One page of markets; <paramref name="Cursor"/> is empty on the last page.</summary>
/// <param name="Markets">The markets on this page.</param>
/// <param name="Cursor">The cursor for the next page.</param>
public sealed record MarketsPage(IReadOnlyList<Market> Markets, string Cursor = "");

/// <summary>One page of trades; <paramref name="Cursor"/> is empty on the last page.</summary>
/// <param name="Trades">The trades on this page.</param>
/// <param name="Cursor">The cursor for the next page.</param>
public sealed record TradesPage(IReadOnlyList<Trade> Trades, string Cursor = "");

/// <summary>One page of events; <paramref name="Cursor"/> is empty on the last page.</summary>
/// <param name="Events">The events on this page.</param>
/// <param name="Cursor">The cursor for the next page.</param>
public sealed record EventsPage(IReadOnlyList<KalshiEvent> Events, string Cursor = "");

/// <summary>A list of series.</summary>
/// <param name="Series">The series.</param>
public sealed record SeriesList(IReadOnlyList<Series> Series);
