using KalTerm.Data.Books;
using KalTerm.Data.State;

namespace KalTerm.Data.Reading;

// What the readers return: the stored values as they are (prices at 10^-6, counts at 10^-2, times in UTC). Turning them into the API's text is the API's job.

/// <summary>A catalog market.</summary>
public sealed record StoredMarket(
    string Ticker,
    string EventTicker,
    string MarketType,
    string Status,
    string YesSubTitle,
    string NoSubTitle,
    DateTime? OpenTime,
    DateTime? CloseTime,
    DateTime? LatestExpirationTime,
    DateTime? SettlementTs,
    string Result,
    long? SettlementValueE6,
    DateTime FirstSeenAt,
    DateTime LastSeenAt);

/// <summary>A ticker update.</summary>
public sealed record StoredTick(
    DateTime Ts,
    long? PriceE6,
    long? YesBidE6,
    long? YesAskE6,
    long? YesBidSizeE2,
    long? YesAskSizeE2,
    long? VolumeE2,
    long? OpenInterestE2,
    long? LastTradeSizeE2);

/// <summary>An executed trade. <see cref="TakerSide"/> is 0 unknown, 1 yes, 2 no.</summary>
public sealed record StoredTrade(DateTime Ts, string TradeId, long YesPriceE6, long NoPriceE6, long CountE2, byte TakerSide, bool IsBlock);

/// <summary>A gap in the stream data.</summary>
public sealed record StoredGap(
    long Id,
    string Kind,
    DateTime StartedAt,
    DateTime? EndedAt,
    string? Detail,
    string BackfillStatus = "pending",
    int? BackfillFound = null,
    int? BackfillAdded = null,
    DateTime? BackfillAt = null,
    string? BackfillNote = null);

/// <summary>How many gaps are waiting for, or have failed, their trade backfill.</summary>
/// <param name="Pending">Pending or running.</param>
/// <param name="Failed">Out of attempts.</param>
public sealed record BackfillCounts(int Pending, int Failed);

/// <summary>Where to resume a market listing: after this ticker.</summary>
/// <param name="Ticker">The last ticker of the previous page.</param>
public sealed record MarketPosition(string Ticker);

/// <summary>Where to resume ticker updates: at this time, after skipping <see cref="Skip"/> rows that share it (updates can share a millisecond).</summary>
/// <param name="Ts">The time of the last row of the previous page.</param>
/// <param name="Skip">How many rows from the first row at <paramref name="Ts"/> on were already returned.</param>
public sealed record TickPosition(DateTime Ts, int Skip);

/// <summary>Where to resume trades: after this trade (ordered by time, then id).</summary>
/// <param name="Ts">The time of the last trade of the previous page.</param>
/// <param name="TradeId">Its id.</param>
public sealed record TradePosition(DateTime Ts, string TradeId);

/// <summary>One page of rows and, when there are more, where the next page starts.</summary>
/// <typeparam name="TRow">The row type.</typeparam>
/// <typeparam name="TNext">The position type.</typeparam>
/// <param name="Rows">The rows.</param>
/// <param name="Next">The start of the next page; <see langword="null"/> on the last page (and always in latest-first mode).</param>
public sealed record Page<TRow, TNext>(IReadOnlyList<TRow> Rows, TNext? Next)
    where TNext : class;

/// <summary>Which markets to list. A <see langword="null"/> filter matches everything.</summary>
/// <param name="Status">Exact status.</param>
/// <param name="EventTicker">Exact event ticker.</param>
/// <param name="Search">A substring of the ticker.</param>
public sealed record MarketFilter(string? Status = null, string? EventTicker = null, string? Search = null);

/// <summary>Everything the status report needs from the database, in one read.</summary>
/// <param name="Run">The ingest run state, if ever written.</param>
/// <param name="LastTickerAt">The newest ticker row's time within the last day, or <see langword="null"/> if none.</param>
/// <param name="LastTradeAt">The newest trade row's time within the last day, or <see langword="null"/> if none.</param>
/// <param name="DataFileBytes">The data file's current size.</param>
/// <param name="MaxFileBytes">The data file's hard maximum; <see langword="null"/> when unlimited.</param>
/// <param name="DiskFreeBytes">Free space on the data file's disk; <see langword="null"/> when the server would not say.</param>
/// <param name="MarketsByStatus">Market counts by status.</param>
/// <param name="GapsByKind">Gaps started within the window, by kind.</param>
/// <param name="Books">The published order books, in total; <see langword="null"/> when the database has no book table yet.</param>
/// <param name="DataUsedBytes">Space holding data inside the file; <see langword="null"/> when not read.</param>
/// <param name="OldestDataDay">The storage governor's last record of the oldest raw day (<c>yyyy-MM-dd</c> or <c>none</c>); <see langword="null"/> if it never ran.</param>
/// <param name="GovernorLast">The storage governor's last check; <see langword="null"/> if it never ran.</param>
/// <param name="Backfill">Gaps waiting for or failing their trade backfill; <see langword="null"/> when the database predates schema v3.</param>
/// <param name="BackupRecord">What the backup job has recorded; <see langword="null"/> when nothing was.</param>
/// <param name="Analytics">The metrics analyzer's recent runs; <see langword="null"/> when the database predates schema v6.</param>
public sealed record StatusSnapshot(
    IngestStateEntry? Run,
    DateTime? LastTickerAt,
    DateTime? LastTradeAt,
    long DataFileBytes,
    long? MaxFileBytes,
    long? DiskFreeBytes,
    IReadOnlyDictionary<string, long> MarketsByStatus,
    IReadOnlyDictionary<string, long> GapsByKind,
    BookStatus? Books = null,
    long? DataUsedBytes = null,
    string? OldestDataDay = null,
    IngestStateEntry? GovernorLast = null,
    BackfillCounts? Backfill = null,
    BackupRecord? BackupRecord = null,
    AnalyticsRecord? Analytics = null);

/// <summary>One market's published order book (a row of <c>dbo.orderbook_state</c>), levels best first.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="UpdatedAt">When the book last changed (UTC).</param>
/// <param name="PublishedAt">When the ingest process last wrote the row (UTC).</param>
/// <param name="Stale">Messages were missed (or there is no book yet) and recovery has not finished.</param>
/// <param name="Approximate">Rebuilt from a REST snapshot.</param>
/// <param name="YesLevels">Yes bids.</param>
/// <param name="NoLevels">No bids.</param>
public sealed record StoredBook(string Ticker, DateTime UpdatedAt, DateTime PublishedAt, bool Stale, bool Approximate, IReadOnlyList<BookLevel> YesLevels, IReadOnlyList<BookLevel> NoLevels);

/// <summary>What the backup job has recorded in the state table.</summary>
/// <param name="Configured">The backup target and when backups began to be expected; <see langword="null"/> when none is set.</param>
/// <param name="Last">The last attempt.</param>
/// <param name="LastGood">The newest finished backup.</param>
/// <param name="LastVerify">The last proof by restore.</param>
public sealed record BackupRecord(IngestStateEntry? Configured, IngestStateEntry? Last, IngestStateEntry? LastGood, IngestStateEntry? LastVerify);

/// <summary>One run of an analyzer, from <c>dbo.analyzer_run</c>.</summary>
/// <param name="Status">running, ok, failed or timeout.</param>
/// <param name="StartedAt">When it started (UTC).</param>
/// <param name="FinishedAt">When it ended (UTC); <see langword="null"/> while it runs.</param>
/// <param name="Rows">How many rows it looked at.</param>
/// <param name="Message">A few words from the run.</param>
public sealed record AnalyzerRunRecord(string Status, DateTime StartedAt, DateTime? FinishedAt, int? Rows, string? Message);

/// <summary>What the metrics analyzer has done lately.</summary>
/// <param name="Last">The newest run of any status; <see langword="null"/> if it never ran.</param>
/// <param name="LastOk">The newest successful run; <see langword="null"/> if none.</param>
public sealed record AnalyticsRecord(AnalyzerRunRecord? Last, AnalyzerRunRecord? LastOk);

/// <summary>The published order books in total.</summary>
/// <param name="Watched">How many books are published.</param>
/// <param name="Stale">How many of them are stale.</param>
/// <param name="Approximate">How many were rebuilt from REST.</param>
/// <param name="NewestPublishedAt">The latest write of any row (UTC), or <see langword="null"/> when there are none.</param>
public sealed record BookStatus(int Watched, int Stale, int Approximate, DateTime? NewestPublishedAt);

/// <summary>Lists markets.</summary>
public interface IMarketReader
{
    /// <summary>Markets in ticker order.</summary>
    /// <param name="filter">Which markets.</param>
    /// <param name="after">Resume after this position, or <see langword="null"/> for the first page.</param>
    /// <param name="limit">Page size, 1 to 1,000.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page.</returns>
    Task<Page<StoredMarket, MarketPosition>> ListAsync(MarketFilter filter, MarketPosition? after, int limit, CancellationToken cancellationToken = default);

    /// <summary>One market.</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The market, or <see langword="null"/> when there is none.</returns>
    Task<StoredMarket?> GetAsync(string ticker, CancellationToken cancellationToken = default);
}

/// <summary>Reads one market's ticker updates.</summary>
public interface ITickReader
{
    /// <summary>
    /// Updates oldest first, within [<paramref name="start"/>, <paramref name="until"/>). With a start (or a position) the read goes forward from there and pages;
    /// with neither it returns the latest <paramref name="limit"/> updates before <paramref name="until"/> (looking back at most the reader's lookback) and no next position.
    /// </summary>
    /// <param name="ticker">The market.</param>
    /// <param name="start">The first moment wanted; ignored when <paramref name="after"/> is given.</param>
    /// <param name="until">The end of the window (exclusive).</param>
    /// <param name="after">Resume here.</param>
    /// <param name="limit">Page size, 1 to 1,000.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page, or <see langword="null"/> when the market does not exist.</returns>
    Task<Page<StoredTick, TickPosition>?> ReadAsync(string ticker, DateTime? start, DateTime until, TickPosition? after, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Reads one market's trades.</summary>
public interface ITradeReader
{
    /// <summary>Trades oldest first, with the same windowing and latest-first mode as <see cref="ITickReader.ReadAsync"/>.</summary>
    /// <param name="ticker">The market.</param>
    /// <param name="start">The first moment wanted; ignored when <paramref name="after"/> is given.</param>
    /// <param name="until">The end of the window (exclusive).</param>
    /// <param name="after">Resume here.</param>
    /// <param name="limit">Page size, 1 to 1,000.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page, or <see langword="null"/> when the market does not exist.</returns>
    Task<Page<StoredTrade, TradePosition>?> ReadAsync(string ticker, DateTime? start, DateTime until, TradePosition? after, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Reads the published order books.</summary>
public interface IBookReader
{
    /// <summary>Every published book, in ticker order.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The books; empty when none are published.</returns>
    Task<IReadOnlyList<StoredBook>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One market's book.</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The book, or <see langword="null"/> when the market is not published.</returns>
    Task<StoredBook?> GetAsync(string ticker, CancellationToken cancellationToken = default);

    /// <summary>The books of several markets (those that are published), in ticker order.</summary>
    /// <param name="tickers">The tickers.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The books.</returns>
    Task<IReadOnlyList<StoredBook>> GetManyAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default);
}

/// <summary>One ticker update with the market it belongs to.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="Tick">The update.</param>
public sealed record StreamTick(string Ticker, StoredTick Tick);

/// <summary>What the push streams read: markets by ticker, and ticker updates of many markets over a window.</summary>
public interface IMarketStreamReader
{
    /// <summary>The market ids of the tickers that exist.</summary>
    /// <param name="tickers">The tickers.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Ticker to market id; a ticker that is not a market is absent.</returns>
    Task<IReadOnlyDictionary<string, int>> ResolveAsync(IReadOnlyCollection<string> tickers, CancellationToken cancellationToken = default);

    /// <summary>Updates of the given markets with <paramref name="from"/> &lt;= time &lt; <paramref name="until"/>, oldest first (ties in a fixed order), at most <paramref name="limit"/>.</summary>
    /// <param name="markets">Market id to ticker.</param>
    /// <param name="from">The window start (inclusive).</param>
    /// <param name="until">The window end (exclusive).</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The updates.</returns>
    Task<IReadOnlyList<StreamTick>> ReadTicksAsync(IReadOnlyDictionary<int, string> markets, DateTime from, DateTime until, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Reads the gap log.</summary>
public interface IGapReader
{
    /// <summary>Gaps that overlap [<paramref name="start"/>, <paramref name="until"/>), newest first; a gap with no end is still open.</summary>
    /// <param name="start">The window's start.</param>
    /// <param name="until">The window's end.</param>
    /// <param name="limit">How many, 1 to 1,000.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The gaps.</returns>
    Task<IReadOnlyList<StoredGap>> ReadAsync(DateTime start, DateTime until, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Reads what the status report needs.</summary>
public interface IStatusReader
{
    /// <summary>One consistent look at the database's health.</summary>
    /// <param name="now">The current UTC time.</param>
    /// <param name="gapWindow">How far back to count gaps.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The snapshot.</returns>
    Task<StatusSnapshot> ReadAsync(DateTime now, TimeSpan gapWindow, CancellationToken cancellationToken = default);
}
