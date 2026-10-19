using KalTerm.Contracts;

namespace KalTerm.Client;

/// <summary>Which markets to ask for. A <see langword="null"/> value is not sent.</summary>
/// <param name="Status">Exact status, for example <c>active</c>.</param>
/// <param name="Event">Exact event ticker.</param>
/// <param name="Search">A substring of the ticker (at least 2 characters).</param>
/// <param name="Limit">Page size.</param>
/// <param name="Cursor">The <c>next_cursor</c> of the previous page.</param>
public sealed record MarketsQuery(string? Status = null, string? Event = null, string? Search = null, int? Limit = null, string? Cursor = null);

/// <summary>A time window and paging for ticks, trades and gaps. A <see langword="null"/> value is not sent; times are UTC.</summary>
/// <param name="Start">The first moment wanted (inclusive).</param>
/// <param name="End">The end of the window (exclusive).</param>
/// <param name="Limit">Page size.</param>
/// <param name="Cursor">The <c>next_cursor</c> of the previous page (ticks and trades).</param>
public sealed record WindowQuery(DateTime? Start = null, DateTime? End = null, int? Limit = null, string? Cursor = null);

/// <summary>
/// The KalTerm API as a program sees it: typed models in, typed models out. An interface so commands and views can be tested without a server, and so
/// the later UI can reuse the real client.
/// </summary>
public interface IKalTermApi
{
    /// <summary>The server's health report.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The report.</returns>
    Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>A page of markets in ticker order.</summary>
    /// <param name="query">Filters and paging.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    Task<MarketPage> GetMarketsAsync(MarketsQuery query, CancellationToken cancellationToken = default);

    /// <summary>One market.</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The market.</returns>
    /// <exception cref="ApiException">The API refused or has no such market (<c>not_found</c>).</exception>
    Task<MarketDto> GetMarketAsync(string ticker, CancellationToken cancellationToken = default);

    /// <summary>Ticker updates of one market, oldest first (or the latest ones when no start is given).</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="query">Window and paging.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    Task<TickPage> GetTicksAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default);

    /// <summary>Trades of one market, oldest first (or the latest ones when no start is given).</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="query">Window and paging.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    Task<TradePage> GetTradesAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default);

    /// <summary>The published order books of every watched market, in ticker order.</summary>
    /// <param name="depth">The most levels a side; the server's default (10) when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The books; empty when no market is watched.</returns>
    Task<OrderBookPage> GetOrderBooksAsync(int? depth = null, CancellationToken cancellationToken = default);

    /// <summary>The published order book of one watched market.</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="depth">The most levels a side; the server's default (10) when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The book.</returns>
    /// <exception cref="ApiException">The API refused or the market is not watched (<c>not_found</c>).</exception>
    Task<OrderBookDto> GetOrderBookAsync(string ticker, int? depth = null, CancellationToken cancellationToken = default);

    /// <summary>Gaps overlapping a window, newest first.</summary>
    /// <param name="query">Window and limit (a cursor is ignored: gaps are not paged).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The gaps.</returns>
    Task<GapPage> GetGapsAsync(WindowQuery query, CancellationToken cancellationToken = default);
}
