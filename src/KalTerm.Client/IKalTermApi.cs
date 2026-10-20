using KalTerm.Contracts;

namespace KalTerm.Client;

/// <summary>Which markets to ask for. A <see langword="null"/> value is not sent.</summary>
/// <param name="Status">Exact status, for example <c>active</c>.</param>
/// <param name="Event">Exact event ticker.</param>
/// <param name="Search">A substring of the ticker (at least 2 characters).</param>
/// <param name="Limit">Page size.</param>
/// <param name="Cursor">The <c>next_cursor</c> of the previous page.</param>
public sealed record MarketsQuery(string? Status = null, string? Event = null, string? Search = null, int? Limit = null, string? Cursor = null);

/// <summary>What the screener is asked. A <see langword="null"/> value is not sent and the server's default applies.</summary>
/// <param name="Sort">volume, trades, change, last, spread, open_interest or volatility.</param>
/// <param name="Order">asc or desc.</param>
/// <param name="Status">Only markets with this status.</param>
/// <param name="MinVolume">Only markets with at least this many contracts traded in 24 hours, for example <c>1000</c>.</param>
/// <param name="Limit">Page size.</param>
/// <param name="Cursor">The <c>next_cursor</c> of the previous page.</param>
/// <param name="Watchlist">Only the markets of this watchlist of the caller.</param>
public sealed record ScreenQuery(string? Sort = null, string? Order = null, string? Status = null, string? MinVolume = null, int? Limit = null, string? Cursor = null, string? Watchlist = null);

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

    /// <summary>Markets ranked by their metrics (24-hour volume by default), from the table the analyzer keeps.</summary>
    /// <param name="query">What to rank and filter by.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    /// <exception cref="ApiException">The API refused a parameter (<c>invalid_parameter</c>).</exception>
    Task<ScreenerPage> GetScreenerAsync(ScreenQuery query, CancellationToken cancellationToken = default);

    /// <summary>The caller's watchlists.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The watchlists.</returns>
    Task<WatchlistListDto> GetWatchlistsAsync(CancellationToken cancellationToken = default);

    /// <summary>One of the caller's watchlists with its markets.</summary>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The watchlist.</returns>
    /// <exception cref="ApiException">There is no such watchlist (<c>not_found</c>).</exception>
    Task<WatchlistDto> GetWatchlistAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Makes an empty watchlist.</summary>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new watchlist.</returns>
    /// <exception cref="ApiException">The name is not usable (<c>invalid_parameter</c>), is taken (<c>already_exists</c>) or the caller has too many (<c>limit_reached</c>).</exception>
    Task<WatchlistDto> CreateWatchlistAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes a watchlist.</summary>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when it is deleted.</returns>
    /// <exception cref="ApiException">There is no such watchlist (<c>not_found</c>).</exception>
    Task DeleteWatchlistAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Adds a market to a watchlist.</summary>
    /// <param name="name">The watchlist.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when it is added.</returns>
    /// <exception cref="ApiException">No such watchlist (<c>not_found</c>), no such market (<c>market_not_found</c>) or the list is full (<c>limit_reached</c>).</exception>
    Task AddToWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default);

    /// <summary>Takes a market off a watchlist.</summary>
    /// <param name="name">The watchlist.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when it is removed.</returns>
    /// <exception cref="ApiException">No such watchlist or the market is not on it (<c>not_found</c>).</exception>
    Task RemoveFromWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default);

    /// <summary>The caller's alerts.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The alerts.</returns>
    Task<AlertListDto> GetAlertsAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes an alert.</summary>
    /// <param name="request">What to watch for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new alert.</returns>
    /// <exception cref="ApiException">A parameter is not usable (<c>invalid_parameter</c>), no such market (<c>market_not_found</c>) or too many alerts (<c>limit_reached</c>).</exception>
    Task<AlertDto> CreateAlertAsync(CreateAlertRequest request, CancellationToken cancellationToken = default);

    /// <summary>Pauses or resumes an alert.</summary>
    /// <param name="id">The alert.</param>
    /// <param name="paused">The new state.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when it is done.</returns>
    /// <exception cref="ApiException">There is no such alert (<c>not_found</c>).</exception>
    Task SetAlertPausedAsync(int id, bool paused, CancellationToken cancellationToken = default);

    /// <summary>Deletes an alert.</summary>
    /// <param name="id">The alert.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when it is deleted.</returns>
    /// <exception cref="ApiException">There is no such alert (<c>not_found</c>).</exception>
    Task DeleteAlertAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>The caller's alerts that fired.</summary>
    /// <param name="after">Only fires with a larger id; <see langword="null"/> for the newest.</param>
    /// <param name="limit">How many; the server's default when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The fires, oldest first.</returns>
    Task<AlertFiresPage> GetAlertFiresAsync(long? after = null, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the stream of the caller's alert fires and yields what it says until the server closes it. The first event says where the stream starts; after that come the fires. A dropped connection
    /// ends the sequence with an exception or normally: use <see cref="AlertWatcher"/> to reopen it without missing a fire.
    /// </summary>
    /// <param name="after">Replay fires after this id first; <see langword="null"/> for new fires only.</param>
    /// <param name="cancellationToken">Closes the stream.</param>
    /// <returns>The events.</returns>
    /// <exception cref="ApiException">The server refused the stream (<c>unauthorized</c>, <c>too_many_streams</c>).</exception>
    IAsyncEnumerable<AlertStreamEvent> StreamAlertFiresAsync(long? after, CancellationToken cancellationToken = default);

    /// <summary>The caller's stored time zone.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The setting; its value is <see langword="null"/> when none is stored.</returns>
    Task<SettingDto> GetTimeZoneSettingAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the caller's time zone (<see langword="null"/> clears it).</summary>
    /// <param name="zone">The zone name, for example <c>America/Denver</c>, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when it is stored.</returns>
    /// <exception cref="ApiException">The server does not know the zone (<c>invalid_parameter</c>).</exception>
    Task SetTimeZoneSettingAsync(string? zone, CancellationToken cancellationToken = default);

    /// <summary>The metrics of one market (last, 24-hour change, volume, VWAP, spread, averages, volatility).</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The metrics.</returns>
    /// <exception cref="ApiException">The API has no such market (<c>not_found</c>).</exception>
    Task<MetricsDto> GetMetricsAsync(string ticker, CancellationToken cancellationToken = default);

    /// <summary>Ticker updates of one market, oldest first (or the latest ones when no start is given).</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="query">Window and paging.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    Task<TickPage> GetTicksAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default);

    /// <summary>Candles of one market, oldest first (or the latest ones when no start is given).</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="interval">The bucket length, for example <c>15m</c>, <c>4h</c>, <c>1d</c> or <c>1w</c>.</param>
    /// <param name="query">Window and paging.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    /// <exception cref="ApiException">The API refused the interval or range (<c>invalid_parameter</c>) or has no such market (<c>not_found</c>).</exception>
    Task<CandlePage> GetCandlesAsync(string ticker, string interval, WindowQuery query, CancellationToken cancellationToken = default);

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
