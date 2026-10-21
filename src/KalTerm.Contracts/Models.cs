namespace KalTerm.Contracts;

// Everything that crosses the API. Prices and counts are decimal strings and times are ISO-8601 UTC strings (see ApiFormat); property names are
// snake_case on the wire. Nulls are written, never omitted, so a client sees every field. A change to any of these is a change to the contract:
// the contract tests pin the exact JSON.

/// <summary>One market from the catalog.</summary>
/// <param name="Ticker">The market ticker.</param>
/// <param name="EventTicker">The event it belongs to.</param>
/// <param name="Type">The market type, for example <c>binary</c>.</param>
/// <param name="Status">Kalshi's status, or <c>unlisted</c> once it left the open listing.</param>
/// <param name="YesLabel">The yes side's label.</param>
/// <param name="NoLabel">The no side's label.</param>
/// <param name="OpenTime">When it opens.</param>
/// <param name="CloseTime">When it closes.</param>
/// <param name="ExpirationTime">The latest expiration.</param>
/// <param name="Result">The result once determined, else empty.</param>
/// <param name="SettlementValue">The settlement value in dollars, once settled.</param>
/// <param name="SettlementTime">When it settled.</param>
/// <param name="FirstSeenAt">When discovery first saw it.</param>
/// <param name="LastSeenAt">When discovery last saw it.</param>
/// <param name="Title">The market's own title, for example <c>Philadelphia wins</c>; empty until discovery has stored it.</param>
/// <param name="EventTitle">Its event's title, for example <c>Philadelphia vs New York</c>; empty until stored.</param>
/// <param name="EventSubTitle">Its event's sub-title, for example <c>PHI vs NYK (Oct 20)</c>; empty until stored.</param>
public sealed record MarketDto(
    string Ticker,
    string EventTicker,
    string Type,
    string Status,
    string YesLabel,
    string NoLabel,
    string? OpenTime,
    string? CloseTime,
    string? ExpirationTime,
    string Result,
    string? SettlementValue,
    string? SettlementTime,
    string FirstSeenAt,
    string LastSeenAt,
    string Title = "",
    string EventTitle = "",
    string EventSubTitle = "");

/// <summary>A page of markets in ticker order.</summary>
/// <param name="Markets">The markets.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; <see langword="null"/> on the last page.</param>
public sealed record MarketPage(IReadOnlyList<MarketDto> Markets, string? NextCursor);

/// <summary>One ticker update: the latest top of book, volume and open interest of a market.</summary>
/// <param name="Time">The server time of the update.</param>
/// <param name="Price">Last traded price, dollars.</param>
/// <param name="YesBid">Best yes bid, dollars.</param>
/// <param name="YesAsk">Best yes ask, dollars.</param>
/// <param name="YesBidSize">Size at the best bid, contracts.</param>
/// <param name="YesAskSize">Size at the best ask, contracts.</param>
/// <param name="Volume">Contracts traded in total.</param>
/// <param name="OpenInterest">Open interest, contracts.</param>
/// <param name="LastTradeSize">Size of the last trade, contracts.</param>
public sealed record TickDto(
    string Time,
    string? Price,
    string? YesBid,
    string? YesAsk,
    string? YesBidSize,
    string? YesAskSize,
    string? Volume,
    string? OpenInterest,
    string? LastTradeSize);

/// <summary>A page of ticker updates, oldest first.</summary>
/// <param name="Ticks">The updates.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; <see langword="null"/> on the last page.</param>
public sealed record TickPage(IReadOnlyList<TickDto> Ticks, string? NextCursor);

/// <summary>One candle: a market's trades and last book state over one bucket of time. Prices are the yes price.</summary>
/// <param name="Time">The start of the bucket (UTC).</param>
/// <param name="Open">The first trade's price, dollars; <see langword="null"/> when nothing traded in the bucket.</param>
/// <param name="High">The highest trade price.</param>
/// <param name="Low">The lowest trade price.</param>
/// <param name="Close">The last trade's price.</param>
/// <param name="Volume">Contracts traded.</param>
/// <param name="TradeCount">Number of trades.</param>
/// <param name="YesBid">Best yes bid at the last ticker update of the bucket, dollars.</param>
/// <param name="YesAsk">Best yes ask at the last ticker update.</param>
/// <param name="OpenInterest">Open interest at the last ticker update, contracts.</param>
/// <param name="Partial">Whether any part of the candle may be incomplete because data is missing (see the gap fields).</param>
/// <param name="TradesPartial">Whether trades may be missing (a trade backfill has not yet filled the gap).</param>
/// <param name="BookPartial">Whether ticker updates may be missing for good.</param>
/// <param name="GapKind">The kind of the gap that covers the most of the bucket.</param>
/// <param name="GapSeconds">Seconds of the bucket covered by gaps.</param>
public sealed record CandleDto(
    string Time,
    string? Open,
    string? High,
    string? Low,
    string? Close,
    string Volume,
    int TradeCount,
    string? YesBid,
    string? YesAsk,
    string? OpenInterest,
    bool Partial,
    bool TradesPartial,
    bool BookPartial,
    string? GapKind,
    int? GapSeconds);

/// <summary>A page of candles, oldest first.</summary>
/// <param name="Interval">The bucket length as asked, for example <c>15m</c>.</param>
/// <param name="Source">How the candles were obtained: stored, rolled up from a stored shorter interval, or computed from raw rows.</param>
/// <param name="Candles">The candles.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; <see langword="null"/> on the last page.</param>
public sealed record CandlePage(string Interval, string Source, IReadOnlyList<CandleDto> Candles, string? NextCursor);

/// <summary>The metrics of one market, computed when asked from the stored ticker, trade and candle rows. A metric without enough data is <see langword="null"/>.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="AsOf">The moment described (UTC).</param>
/// <param name="LastTime">When the latest ticker update was.</param>
/// <param name="Last">The last traded price, dollars.</param>
/// <param name="Change24h">The last price minus the close 24 hours ago, dollars.</param>
/// <param name="Volume24h">Contracts traded in the last 24 hours.</param>
/// <param name="TradeCount24h">Trades in the last 24 hours.</param>
/// <param name="OpenInterest">Open interest, contracts.</param>
/// <param name="YesBid">Best yes bid, dollars.</param>
/// <param name="YesAsk">Best yes ask, dollars.</param>
/// <param name="Spread">Ask minus bid, dollars.</param>
/// <param name="Vwap24h">The volume-weighted average price of the last 24 hours of trades, dollars.</param>
/// <param name="Sma20">The mean of the last 20 hourly closes, dollars.</param>
/// <param name="Sma50">The mean of the last 50 hourly closes, dollars.</param>
/// <param name="Volatility24h">The standard deviation of hourly close-to-close changes (last 24 hourly closes), dollars.</param>
/// <param name="Partial">Whether a gap overlaps the 24 hours or a candle used is flagged partial, so a figure may be incomplete.</param>
public sealed record MetricsDto(
    string Ticker,
    string AsOf,
    string? LastTime,
    string? Last,
    string? Change24h,
    string Volume24h,
    long TradeCount24h,
    string? OpenInterest,
    string? YesBid,
    string? YesAsk,
    string? Spread,
    string? Vwap24h,
    string? Sma20,
    string? Sma50,
    string? Volatility24h,
    bool Partial);

/// <summary>One market in the screener: what it is and its metrics (see <see cref="MetricsDto"/>).</summary>
/// <param name="Ticker">The market.</param>
/// <param name="EventTicker">Its event.</param>
/// <param name="Status">Its status.</param>
/// <param name="YesLabel">The yes outcome's label.</param>
/// <param name="ChangedAt">When these figures last changed.</param>
/// <param name="LastTime">When the latest ticker update (within the last hour) was.</param>
/// <param name="Last">The last traded price, dollars.</param>
/// <param name="Change24h">The last price minus the close 24 hours ago, dollars.</param>
/// <param name="Volume24h">Contracts traded in 24 hours.</param>
/// <param name="TradeCount24h">Trades in 24 hours.</param>
/// <param name="OpenInterest">Open interest, contracts.</param>
/// <param name="YesBid">Best yes bid, dollars.</param>
/// <param name="YesAsk">Best yes ask, dollars.</param>
/// <param name="Spread">Ask minus bid, dollars.</param>
/// <param name="Vwap24h">The 24-hour volume-weighted average price, dollars.</param>
/// <param name="Sma20">The mean of the last 20 hourly closes, dollars.</param>
/// <param name="Sma50">The mean of the last 50 hourly closes, dollars.</param>
/// <param name="Volatility24h">The standard deviation of hourly changes, dollars.</param>
/// <param name="Partial">Whether a gap may have left a figure incomplete.</param>
/// <param name="Title">The market's own title; empty until stored.</param>
/// <param name="EventTitle">Its event's title; empty until stored.</param>
/// <param name="EventSubTitle">Its event's sub-title; empty until stored.</param>
public sealed record ScreenerRowDto(
    string Ticker,
    string EventTicker,
    string Status,
    string YesLabel,
    string ChangedAt,
    string? LastTime,
    string? Last,
    string? Change24h,
    string Volume24h,
    long TradeCount24h,
    string? OpenInterest,
    string? YesBid,
    string? YesAsk,
    string? Spread,
    string? Vwap24h,
    string? Sma20,
    string? Sma50,
    string? Volatility24h,
    bool Partial,
    string Title = "",
    string EventTitle = "",
    string EventSubTitle = "");

/// <summary>A page of the screener.</summary>
/// <param name="Sort">What the list is ranked by.</param>
/// <param name="Order">asc or desc; markets without a value for the sort are last either way.</param>
/// <param name="RefreshedAt">The moment the figures describe (when the newest successful run started); <see langword="null"/> when none has finished.</param>
/// <param name="AgeSeconds">How long ago that was; a stalled runner shows here.</param>
/// <param name="Rows">The markets.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; <see langword="null"/> on the last page.</param>
public sealed record ScreenerPage(string Sort, string Order, string? RefreshedAt, long? AgeSeconds, IReadOnlyList<ScreenerRowDto> Rows, string? NextCursor);

/// <summary>A watchlist in a list of them.</summary>
/// <param name="Name">The name.</param>
/// <param name="Markets">How many markets it holds.</param>
/// <param name="CreatedAt">When it was made.</param>
public sealed record WatchlistSummaryDto(string Name, int Markets, string CreatedAt);

/// <summary>The caller's watchlists.</summary>
/// <param name="Watchlists">The watchlists, by name.</param>
public sealed record WatchlistListDto(IReadOnlyList<WatchlistSummaryDto> Watchlists);

/// <summary>A watchlist and its markets.</summary>
/// <param name="Name">The name.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="Markets">Its market tickers, in order.</param>
public sealed record WatchlistDto(string Name, string CreatedAt, IReadOnlyList<string> Markets);

/// <summary>The body that makes a watchlist.</summary>
/// <param name="Name">A name of lower-case letters, digits, dots, dashes and underscores, starting with a letter or digit, up to 64 characters.</param>
public sealed record CreateWatchlistRequest(string Name);

/// <summary>An alert of the caller. <c>Threshold</c> and the fire <c>Value</c> are in the unit of the kind: dollars for <c>price_above</c>, <c>price_below</c> and <c>spread_above</c>; a whole factor for <c>volume_spike</c>; contracts for <c>oi_change</c>.</summary>
/// <param name="Id">The alert's id.</param>
/// <param name="Ticker">The market it watches.</param>
/// <param name="Kind">price_above, price_below, spread_above, volume_spike or oi_change.</param>
/// <param name="Threshold">The threshold.</param>
/// <param name="CooldownSeconds">The least time between two fires.</param>
/// <param name="Paused">Whether it is paused.</param>
/// <param name="CreatedAt">When it was made.</param>
/// <param name="LastEvaluatedAt">When it was last judged; <see langword="null"/> if it never was.</param>
/// <param name="LastFiredAt">When it last fired; <see langword="null"/> if it never did.</param>
public sealed record AlertDto(int Id, string Ticker, string Kind, string Threshold, int CooldownSeconds, bool Paused, string CreatedAt, string? LastEvaluatedAt, string? LastFiredAt);

/// <summary>The caller's alerts.</summary>
/// <param name="Alerts">The alerts, oldest first.</param>
public sealed record AlertListDto(IReadOnlyList<AlertDto> Alerts);

/// <summary>The body that makes an alert.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="Kind">price_above, price_below, spread_above, volume_spike or oi_change.</param>
/// <param name="Threshold">The threshold as text: dollars (<c>0.60</c>), a whole factor (<c>5</c>) or contracts (<c>100</c>), according to the kind.</param>
/// <param name="CooldownSeconds">The least time between two fires, 10 to 86,400; 60 when left out.</param>
public sealed record CreateAlertRequest(string Ticker, string Kind, string Threshold, int? CooldownSeconds = null);

/// <summary>The body that pauses or resumes an alert.</summary>
/// <param name="Paused">The new state.</param>
public sealed record PatchAlertRequest(bool Paused);

/// <summary>An alert that fired.</summary>
/// <param name="Id">The fire's id; they only grow.</param>
/// <param name="AlertId">The alert.</param>
/// <param name="Ticker">The market.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Threshold">The threshold when it fired.</param>
/// <param name="Value">What was observed.</param>
/// <param name="FiredAt">When.</param>
public sealed record AlertFireDto(long Id, int AlertId, string Ticker, string Kind, string Threshold, string Value, string FiredAt);

/// <summary>Alerts that fired, oldest first.</summary>
/// <param name="Fires">The fires.</param>
/// <param name="LastId">The largest id among them (pass as <c>after</c> to get what came next); <see langword="null"/> when there are none.</param>
public sealed record AlertFiresPage(IReadOnlyList<AlertFireDto> Fires, long? LastId);

/// <summary>A setting of the caller.</summary>
/// <param name="Name">The setting, for example <c>time_zone</c>.</param>
/// <param name="Value">Its value; <see langword="null"/> when it is not set.</param>
public sealed record SettingDto(string Name, string? Value);

/// <summary>The body that sets a setting.</summary>
/// <param name="Value">The value.</param>
public sealed record SettingValueRequest(string Value);

/// <summary>One executed trade.</summary>
/// <param name="Time">When it executed.</param>
/// <param name="TradeId">Kalshi's trade id.</param>
/// <param name="YesPrice">The yes price, dollars.</param>
/// <param name="NoPrice">The no price, dollars.</param>
/// <param name="Count">Contracts traded.</param>
/// <param name="TakerSide"><c>yes</c> or <c>no</c>; <see langword="null"/> when not reported.</param>
/// <param name="IsBlockTrade">Whether it was a block trade.</param>
public sealed record TradeDto(string Time, string TradeId, string YesPrice, string NoPrice, string Count, string? TakerSide, bool IsBlockTrade);

/// <summary>A page of trades, oldest first.</summary>
/// <param name="Trades">The trades.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; <see langword="null"/> on the last page.</param>
public sealed record TradePage(IReadOnlyList<TradeDto> Trades, string? NextCursor);

/// <summary>A period for which stream data may be missing.</summary>
/// <param name="Id">The gap's id.</param>
/// <param name="Kind">What kind of gap, for example <c>ingest_down</c> or <c>ws_reconnected</c>.</param>
/// <param name="From">When the missing period began.</param>
/// <param name="To">When it ended; <see langword="null"/> if it has not.</param>
/// <param name="Detail">What the ingest process noted.</param>
/// <param name="Backfill">How the trades of the gap are being recovered from REST. Ticker updates and order books are never recovered.</param>
public sealed record GapDto(long Id, string Kind, string From, string? To, string? Detail, BackfillDto? Backfill = null);

/// <summary>The recovery of one gap's trades.</summary>
/// <param name="Status"><c>pending</c>, <c>running</c>, <c>done</c>, <c>failed</c> or <c>skipped</c>.</param>
/// <param name="Found">Trades REST returned for the window.</param>
/// <param name="Added">Of those, trades that were missing and were written.</param>
/// <param name="At">When the last attempt started or finished.</param>
/// <param name="Note">What happened, for example the window recovered or why it failed.</param>
public sealed record BackfillDto(string Status, int? Found, int? Added, string? At, string? Note);

/// <summary>Gaps, newest first.</summary>
/// <param name="Gaps">The gaps.</param>
public sealed record GapPage(IReadOnlyList<GapDto> Gaps);

/// <summary>The ingest process's state as the database knows it.</summary>
/// <param name="State"><c>running</c>, <c>stopped</c> or <c>unknown</c> (never recorded).</param>
/// <param name="LastHeartbeat">The last heartbeat (running) or the clean-stop time (stopped).</param>
/// <param name="HeartbeatAgeSeconds">How long ago that was.</param>
public sealed record IngestStateDto(string State, string? LastHeartbeat, long? HeartbeatAgeSeconds);

/// <summary>How fresh one data stream is.</summary>
/// <param name="LastTime">The newest row's time.</param>
/// <param name="AgeSeconds">How long ago that was.</param>
public sealed record StreamDto(string? LastTime, long? AgeSeconds);

/// <summary>Freshness of the two streams.</summary>
/// <param name="Ticker">Ticker updates.</param>
/// <param name="Trade">Trades.</param>
public sealed record StreamsDto(StreamDto Ticker, StreamDto Trade);

/// <summary>Database and disk usage.</summary>
/// <param name="DataFileBytes">The data file's current size.</param>
/// <param name="MaxBytes">The data file's hard maximum; <see langword="null"/> when unlimited.</param>
/// <param name="DiskFreeBytes">Free space on the data file's disk; <see langword="null"/> when not known.</param>
/// <param name="UsedBytes">Space holding data inside the file. Dropping a day frees pages here but never shrinks the file, so this is the figure the budget uses.</param>
/// <param name="UsedPercent">The share of <paramref name="MaxBytes"/> in use, whole percent; <see langword="null"/> when unlimited.</param>
/// <param name="OldestDataDay">The UTC day of the oldest raw ticker or trade row (<c>yyyy-MM-dd</c>), or <c>none</c>; <see langword="null"/> when the governor has not reported.</param>
/// <param name="GovernorLast">What the storage governor's last check did, for example <c>ok used=3% dropped=</c>; <see langword="null"/> when it has not run.</param>
/// <param name="GovernorAgeSeconds">How long ago that check ran.</param>
public sealed record StorageDto(
    long DataFileBytes,
    long? MaxBytes,
    long? DiskFreeBytes,
    long UsedBytes,
    int? UsedPercent,
    string? OldestDataDay,
    string? GovernorLast,
    long? GovernorAgeSeconds);

/// <summary>Gaps in a recent window.</summary>
/// <param name="WindowHours">How far back this looks.</param>
/// <param name="Count">How many gaps started in the window.</param>
/// <param name="ByKind">Gap counts by kind.</param>
/// <param name="Note">What a gap means for the data.</param>
/// <param name="BackfillPending">Gaps (of any age) whose trades are still to be recovered, including one in progress.</param>
/// <param name="BackfillFailed">Gaps (of any age) whose trades could not be recovered after every attempt.</param>
public sealed record GapSummaryDto(int WindowHours, long Count, IReadOnlyDictionary<string, long> ByKind, string Note, int BackfillPending = 0, int BackfillFailed = 0);

/// <summary>The health report. Anything that needs attention is in <see cref="Problems"/>; an empty list means healthy.</summary>
/// <param name="Time">When the report was made.</param>
/// <param name="Problems">What needs attention, most important first.</param>
/// <param name="Ingest">The ingest process.</param>
/// <param name="Streams">Stream freshness.</param>
/// <param name="Storage">Storage use.</param>
/// <param name="Markets">Market counts by status.</param>
/// <param name="Gaps">Recent gaps.</param>
/// <param name="Books">The published order books.</param>
/// <param name="Backup">The backups; <see langword="null"/> from a server that predates them.</param>
/// <param name="Analytics">The metrics analyzer; <see langword="null"/> from a server that predates it.</param>
public sealed record StatusDto(
    string Time,
    IReadOnlyList<string> Problems,
    IngestStateDto Ingest,
    StreamsDto Streams,
    StorageDto Storage,
    IReadOnlyDictionary<string, long> Markets,
    GapSummaryDto Gaps,
    BooksStatusDto Books,
    BackupStatusDto? Backup = null,
    AnalyticsStatusDto? Analytics = null);

/// <summary>The state of the analyzer that keeps the screener's table.</summary>
/// <param name="Last">What the last run did: <c>ok</c>, <c>failed</c>, <c>timeout</c> or <c>running</c>; <see langword="null"/> if it never ran.</param>
/// <param name="LastMessage">A few words from that run.</param>
/// <param name="LastAt">When that run started.</param>
/// <param name="LastOkAt">When the newest successful run started, which is the moment the screener's figures describe.</param>
/// <param name="LastOkAgeSeconds">How long ago that was.</param>
/// <param name="LastOkSeconds">How long that run took.</param>
/// <param name="LastOkRows">How many markets that run covered.</param>
public sealed record AnalyticsStatusDto(
    string? Last,
    string? LastMessage,
    string? LastAt,
    string? LastOkAt,
    long? LastOkAgeSeconds,
    long? LastOkSeconds,
    int? LastOkRows);

/// <summary>The state of the backups.</summary>
/// <param name="Configured">Whether a backup target is set, so backups are expected.</param>
/// <param name="LastGood">The name of the newest finished backup.</param>
/// <param name="LastGoodAt">When it finished.</param>
/// <param name="LastGoodAgeSeconds">How long ago that was.</param>
/// <param name="Last">What the last attempt did (<c>ok ...</c> or <c>error ...</c>).</param>
/// <param name="LastAt">When that attempt ended.</param>
/// <param name="LastVerify">What the last proof by restore did (<c>ok ...</c> or <c>error ...</c>).</param>
/// <param name="LastVerifyAt">When that ended.</param>
public sealed record BackupStatusDto(
    bool Configured,
    string? LastGood,
    string? LastGoodAt,
    long? LastGoodAgeSeconds,
    string? Last,
    string? LastAt,
    string? LastVerify,
    string? LastVerifyAt);

/// <summary>The published order books in total.</summary>
/// <param name="Watched">How many markets have a published book.</param>
/// <param name="Stale">How many of them are stale: messages were missed and recovery has not finished.</param>
/// <param name="Approximate">How many were rebuilt from a REST snapshot, which cannot be aligned with the live stream.</param>
/// <param name="PublishedAgeSeconds">How long ago the newest row was written; <see langword="null"/> when none is published.</param>
public sealed record BooksStatusDto(int Watched, int Stale, int Approximate, long? PublishedAgeSeconds);

/// <summary>One price level of an order book.</summary>
/// <param name="Price">The price, dollars.</param>
/// <param name="Quantity">The resting contracts.</param>
public sealed record BookLevelDto(string Price, string Quantity);

/// <summary>
/// One market's order book as the ingest process last published it. Kalshi sends bids only; the asks are the other side's bids turned around
/// (a yes ask at <c>1 - p</c> for a no bid at <c>p</c>), so a client derives them. Levels are best (highest) price first.
/// </summary>
/// <param name="Ticker">The market.</param>
/// <param name="UpdatedAt">When the book last changed.</param>
/// <param name="PublishedAt">When the ingest process last wrote it.</param>
/// <param name="AgeSeconds">How long ago that was.</param>
/// <param name="Stale">Messages were missed (or there is no book yet) and recovery has not finished: do not trust the levels.</param>
/// <param name="Approximate">Rebuilt from a REST snapshot, which cannot be aligned with the live stream.</param>
/// <param name="YesBids">Yes bids, best first, cut to the requested depth.</param>
/// <param name="NoBids">No bids, best first, cut to the requested depth.</param>
public sealed record OrderBookDto(
    string Ticker,
    string UpdatedAt,
    string PublishedAt,
    long AgeSeconds,
    bool Stale,
    bool Approximate,
    IReadOnlyList<BookLevelDto> YesBids,
    IReadOnlyList<BookLevelDto> NoBids);

/// <summary>The published order books, in ticker order.</summary>
/// <param name="Books">The books.</param>
public sealed record OrderBookPage(IReadOnlyList<OrderBookDto> Books);

/// <summary>The body of the health checks.</summary>
/// <param name="Status"><c>ok</c> for liveness; <c>ready</c> or <c>not_ready</c> for readiness.</param>
public sealed record HealthDto(string Status);

/// <summary>The body of every error response.</summary>
/// <param name="Error">A short code: <c>invalid_parameter</c>, <c>not_found</c>, <c>unavailable</c>, <c>unauthorized</c> (no valid token), <c>too_many_attempts</c>.</param>
/// <param name="Fields">For <c>invalid_parameter</c>, which parameters were wrong (never their values).</param>
public sealed record ErrorDto(string Error, IReadOnlyList<string>? Fields = null);

/// <summary>One ticker update of one market, as pushed on the stream.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="Tick">The update.</param>
public sealed record MarketTickDto(string Ticker, TickDto Tick);

/// <summary>The first event of a market stream: what the server accepted, and where live data starts.</summary>
/// <param name="StreamId">This stream's id.</param>
/// <param name="ResumeFrom">Where the live ticks begin (UTC, milliseconds): send it as <c>after</c> or <c>Last-Event-ID</c> to resume from here if nothing newer was received.</param>
/// <param name="Ticks">The markets whose ticks are pushed.</param>
/// <param name="Books">The markets whose order books are pushed.</param>
/// <param name="Unknown">Requested tickers that are not markets here (nothing is pushed for them).</param>
/// <param name="NoBook">Requested book tickers that have no published book (only watched markets have one).</param>
/// <param name="Depth">How many price levels each side of a pushed book carries.</param>
public sealed record StreamReadyDto(string StreamId, string ResumeFrom, IReadOnlyList<string> Ticks, IReadOnlyList<string> Books, IReadOnlyList<string> Unknown, IReadOnlyList<string> NoBook, int Depth);

/// <summary>The stream cannot continue from where the client was: reload over REST, then connect again without a resume point.</summary>
/// <param name="Reason">Why, for example <c>too_far_behind</c> or <c>slow_consumer</c>.</param>
public sealed record StreamResetDto(string Reason);

/// <summary>An open market stream changed what it watches (the confirmation of a live add or remove).</summary>
/// <param name="Kind"><c>ticks</c> or <c>books</c>.</param>
/// <param name="Ticker">The market.</param>
/// <param name="From">For a new ticks subscription, where its live ticks begin (UTC, milliseconds): earlier ticks are the client's to load over REST. <see langword="null"/> for a removal, for a books subscription, and when the market was already watched.</param>
/// <param name="Unknown">The ticker is not a market here (nothing is pushed until it appears).</param>
/// <param name="NoBook">For a books subscription, the market has no published book (only watched markets have one).</param>
public sealed record StreamSubscriptionDto(string Kind, string Ticker, string? From, bool Unknown, bool NoBook);
