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
    string LastSeenAt);

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
public sealed record GapDto(long Id, string Kind, string From, string? To, string? Detail);

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
public sealed record StorageDto(long DataFileBytes, long? MaxBytes, long? DiskFreeBytes);

/// <summary>Gaps in a recent window.</summary>
/// <param name="WindowHours">How far back this looks.</param>
/// <param name="Count">How many gaps started in the window.</param>
/// <param name="ByKind">Gap counts by kind.</param>
/// <param name="Note">What a gap means for the data.</param>
public sealed record GapSummaryDto(int WindowHours, long Count, IReadOnlyDictionary<string, long> ByKind, string Note);

/// <summary>The health report. Anything that needs attention is in <see cref="Problems"/>; an empty list means healthy.</summary>
/// <param name="Time">When the report was made.</param>
/// <param name="Problems">What needs attention, most important first.</param>
/// <param name="Ingest">The ingest process.</param>
/// <param name="Streams">Stream freshness.</param>
/// <param name="Storage">Storage use.</param>
/// <param name="Markets">Market counts by status.</param>
/// <param name="Gaps">Recent gaps.</param>
public sealed record StatusDto(
    string Time,
    IReadOnlyList<string> Problems,
    IngestStateDto Ingest,
    StreamsDto Streams,
    StorageDto Storage,
    IReadOnlyDictionary<string, long> Markets,
    GapSummaryDto Gaps);

/// <summary>The body of the health checks.</summary>
/// <param name="Status"><c>ok</c> for liveness; <c>ready</c> or <c>not_ready</c> for readiness.</param>
public sealed record HealthDto(string Status);

/// <summary>The body of every error response.</summary>
/// <param name="Error">A short code: <c>invalid_parameter</c>, <c>not_found</c>, <c>unavailable</c>.</param>
/// <param name="Fields">For <c>invalid_parameter</c>, which parameters were wrong (never their values).</param>
public sealed record ErrorDto(string Error, IReadOnlyList<string>? Fields = null);
