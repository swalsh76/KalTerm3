using Dapper;
using KalTerm.Data.Reading;

namespace KalTerm.Data.Metrics;

/// <summary>What the screener can rank markets by.</summary>
public enum ScreenerSort
{
    /// <summary>Contracts traded in the last 24 hours.</summary>
    Volume,

    /// <summary>Trades in the last 24 hours.</summary>
    Trades,

    /// <summary>The last price minus the close 24 hours ago.</summary>
    Change,

    /// <summary>The last traded price.</summary>
    Last,

    /// <summary>Ask minus bid.</summary>
    Spread,

    /// <summary>Open interest.</summary>
    OpenInterest,

    /// <summary>The standard deviation of hourly changes.</summary>
    Volatility,
}

/// <summary>Where a screener page continues: the sort key of the last row (a missing value is stored as the extreme that sorts last) and its market id.</summary>
/// <param name="Key">The sort key.</param>
/// <param name="MarketId">The market id, the tie-breaker.</param>
public sealed record ScreenerPosition(long Key, int MarketId);

/// <summary>A screener request.</summary>
/// <param name="Sort">What to rank by.</param>
/// <param name="Descending">Largest first; a market without a value for the sort always comes last either way.</param>
/// <param name="Status">Only markets with this status, or <see langword="null"/>.</param>
/// <param name="MinVolumeE2">Only markets with at least this volume (10^-2 contracts), or <see langword="null"/>.</param>
/// <param name="After">Continue after this position, or <see langword="null"/> for the first page.</param>
/// <param name="Limit">Rows per page, 1 to 1,000.</param>
/// <param name="OnlyMarkets">Only these markets (the ids of a watchlist), or <see langword="null"/> for every market.</param>
/// <param name="Search">Only markets whose ticker, yes label, title, event title or event sub-title contains this text (matched literally, any case), or <see langword="null"/>.</param>
public sealed record ScreenerQuery(ScreenerSort Sort, bool Descending, string? Status, long? MinVolumeE2, ScreenerPosition? After, int Limit, IReadOnlyCollection<int>? OnlyMarkets = null, string? Search = null);

/// <summary>One market in the screener: its catalog identity and its stored metrics.</summary>
/// <param name="MarketId">The market id.</param>
/// <param name="Ticker">The ticker.</param>
/// <param name="EventTicker">The event.</param>
/// <param name="Status">The market status.</param>
/// <param name="YesSubTitle">The yes outcome's label.</param>
/// <param name="ChangedAt">When these figures last changed (UTC).</param>
/// <param name="Metrics">The figures (the <c>AsOf</c> is the run that last wrote them, see <see cref="ScreenerResult.RefreshedAt"/>).</param>
/// <param name="Title">The market's own title, for example <c>Philadelphia wins</c>; empty when not stored yet.</param>
/// <param name="EventTitle">Its event's title, for example <c>Philadelphia vs New York</c>; empty when not stored yet.</param>
/// <param name="EventSubTitle">Its event's sub-title.</param>
public sealed record ScreenerRow(int MarketId, string Ticker, string EventTicker, string Status, string YesSubTitle, DateTime ChangedAt, MarketMetrics Metrics, string Title = "", string EventTitle = "", string EventSubTitle = "");

/// <summary>A page of the screener.</summary>
/// <param name="Rows">The markets, in order.</param>
/// <param name="Next">Where the next page starts; <see langword="null"/> on the last page.</param>
/// <param name="RefreshedAt">When the newest successful metrics run started (UTC), that is the moment the figures describe; <see langword="null"/> when none has finished.</param>
public sealed record ScreenerResult(IReadOnlyList<ScreenerRow> Rows, ScreenerPosition? Next, DateTime? RefreshedAt);

/// <summary>Ranks markets by their stored metrics.</summary>
public interface IScreenerReader
{
    /// <summary>Reads one page.</summary>
    /// <param name="query">The request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page.</returns>
    Task<ScreenerResult> ReadAsync(ScreenerQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IScreenerReader"/> over <c>dbo.market_metrics</c> joined to <c>dbo.market</c>. Paging is by keyset on (sort key, market id) so a page never repeats or skips a row while the table
/// refreshes; the sort key of a missing value is the extreme that puts it last, so such markets are at the end whichever way the list runs.
/// </summary>
/// <param name="source">Opens connections.</param>
public sealed class ScreenerReader(DataSource source) : IScreenerReader
{
    /// <summary>The analyzer whose runs say how fresh the table is.</summary>
    public const string Analyzer = "market_metrics";

    private const int MaxLimit = 1_000;

    /// <inheritdoc/>
    public async Task<ScreenerResult> ReadAsync(ScreenerQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaxLimit);
        var column = Column(query.Sort);
        var nullKey = query.Descending ? long.MinValue : long.MaxValue;
        var direction = query.Descending ? "DESC" : "ASC";
        var past = query.Descending ? "<" : ">";
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT TOP (@take) m.market_id AS MarketId, m.ticker AS Ticker, m.event_ticker AS EventTicker, m.status AS Status, m.yes_sub_title AS YesSubTitle,
                   m.title AS Title, ISNULL(ev.title, N'') AS EventTitle, ISNULL(ev.sub_title, N'') AS EventSubTitle,
                   t.changed_at AS ChangedAt, t.last_time AS LastTime, t.last_e6 AS LastE6, t.change_24h_e6 AS Change24hE6, t.volume_e2 AS VolumeE2, t.trade_count AS TradeCount,
                   t.open_interest_e2 AS OpenInterestE2, t.yes_bid_e6 AS YesBidE6, t.yes_ask_e6 AS YesAskE6, t.spread_e6 AS SpreadE6, t.vwap_e6 AS Vwap24hE6,
                   t.sma20_e6 AS Sma20E6, t.sma50_e6 AS Sma50E6, t.volatility_e6 AS Volatility24hE6, t.is_partial AS Partial, k.v AS SortKey
            FROM dbo.market_metrics t
            JOIN dbo.market m ON m.market_id = t.market_id
            LEFT JOIN dbo.market_event ev ON ev.event_ticker = m.event_ticker
            CROSS APPLY (SELECT ISNULL(t.{column}, @nullKey) AS v) k
            WHERE (@status IS NULL OR m.status = @status)
              AND (@minVolume IS NULL OR t.volume_e2 >= @minVolume)
              AND (@onlyIds IS NULL OR t.market_id IN (SELECT CAST([value] AS int) FROM OPENJSON(@onlyIds)))
              AND (@pattern IS NULL OR m.ticker LIKE @pattern ESCAPE '\' OR m.yes_sub_title LIKE @pattern ESCAPE '\' OR m.title LIKE @pattern ESCAPE '\'
                   OR ev.title LIKE @pattern ESCAPE '\' OR ev.sub_title LIKE @pattern ESCAPE '\')
              AND (@afterId IS NULL OR k.v {past} @afterKey OR (k.v = @afterKey AND t.market_id > @afterId))
            ORDER BY k.v {direction}, t.market_id
            """,
            new
            {
                take = query.Limit + 1,
                nullKey,
                status = ReaderSupport.Ansi(query.Status, 32),
                minVolume = query.MinVolumeE2,
                onlyIds = query.OnlyMarkets is null ? null : "[" + string.Join(',', query.OnlyMarkets) + "]",
                pattern = query.Search is null ? null : ReaderSupport.LikePattern(query.Search),
                afterKey = query.After?.Key,
                afterId = query.After?.MarketId,
            },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var refreshed = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT TOP (1) started_at FROM dbo.analyzer_run WHERE name = @name AND status = 'ok' ORDER BY run_id DESC",
            new { name = ReaderSupport.Ansi(Analyzer, 64) },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var page = rows.Take(query.Limit).Select(ToRow).ToList();
        var next = rows.Count > query.Limit ? new ScreenerPosition(rows[query.Limit - 1].SortKey, rows[query.Limit - 1].MarketId) : null;
        return new ScreenerResult(page, next, refreshed is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null);
    }

    private static string Column(ScreenerSort sort) => sort switch
    {
        ScreenerSort.Volume => "volume_e2",
        ScreenerSort.Trades => "trade_count",
        ScreenerSort.Change => "change_24h_e6",
        ScreenerSort.Last => "last_e6",
        ScreenerSort.Spread => "spread_e6",
        ScreenerSort.OpenInterest => "open_interest_e2",
        ScreenerSort.Volatility => "volatility_e6",
        _ => throw new ArgumentOutOfRangeException(nameof(sort)),
    };

    private static ScreenerRow ToRow(Row r) => new(
        r.MarketId,
        r.Ticker,
        r.EventTicker,
        r.Status,
        r.YesSubTitle,
        DateTime.SpecifyKind(r.ChangedAt, DateTimeKind.Utc),
        new MarketMetrics(
            DateTime.SpecifyKind(r.ChangedAt, DateTimeKind.Utc),
            r.LastTime is { } time ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : null,
            r.LastE6,
            r.Change24hE6,
            r.VolumeE2,
            r.TradeCount,
            r.OpenInterestE2,
            r.YesBidE6,
            r.YesAskE6,
            r.SpreadE6,
            r.Vwap24hE6,
            r.Sma20E6,
            r.Sma50E6,
            r.Volatility24hE6,
            r.Partial),
        r.Title,
        r.EventTitle,
        r.EventSubTitle);

    private sealed record Row(
        int MarketId,
        string Ticker,
        string EventTicker,
        string Status,
        string YesSubTitle,
        string Title,
        string EventTitle,
        string EventSubTitle,
        DateTime ChangedAt,
        DateTime? LastTime,
        long? LastE6,
        long? Change24hE6,
        long VolumeE2,
        long TradeCount,
        long? OpenInterestE2,
        long? YesBidE6,
        long? YesAskE6,
        long? SpreadE6,
        long? Vwap24hE6,
        long? Sma20E6,
        long? Sma50E6,
        long? Volatility24hE6,
        bool Partial,
        long SortKey);
}
