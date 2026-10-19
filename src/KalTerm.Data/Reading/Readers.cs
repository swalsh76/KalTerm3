using System.Data;
using Dapper;
using KalTerm.Data.State;

namespace KalTerm.Data.Reading;

/// <summary>Limits that keep every read cheap, whatever a client asks for.</summary>
public sealed record ReaderOptions
{
    /// <summary>How long one query may run before it is cancelled. Default 15 seconds.</summary>
    public int CommandTimeoutSeconds { get; init; } = 15;

    /// <summary>In latest-first mode (no start given) how far before the window's end a read looks. Default 7 days.</summary>
    public TimeSpan LatestLookback { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if a value cannot work.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(CommandTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LatestLookback, TimeSpan.Zero);
    }
}

/// <summary>Shared plumbing for the readers: a connection, a command with the timeout, and the parameter types that keep the indexes usable.</summary>
internal static class ReaderSupport
{
    public const int MaxLimit = 1_000;

    /// <summary>A varchar parameter. Dapper sends strings as nvarchar, which turns a varchar comparison into a scan; this keeps it a seek.</summary>
    public static DbString Ansi(string? value, int length) => new() { Value = value, IsAnsi = true, Length = length, IsFixedLength = false };

    public static CommandDefinition Command(string sql, object? parameters, ReaderOptions options, CancellationToken cancellationToken) =>
        new(sql, parameters, commandTimeout: options.CommandTimeoutSeconds, cancellationToken: cancellationToken);

    public static void CheckLimit(int limit) => ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)(limit - 1), (uint)(MaxLimit - 1), nameof(limit));

    /// <summary>The id of a market, or <see langword="null"/> when there is none.</summary>
    public static async Task<int?> MarketIdAsync(IDbConnection connection, string ticker, ReaderOptions options, CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<int?>(
            Command("SELECT market_id FROM dbo.market WHERE ticker = @ticker", new { ticker = Ansi(ticker, 128) }, options, cancellationToken)).ConfigureAwait(false);

    /// <summary>Escapes <c>\ % _ [</c> so a client's text is matched literally by <c>LIKE ... ESCAPE '\'</c>.</summary>
    public static string LikePattern(string text) =>
        "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";
}

/// <summary><see cref="IMarketReader"/> over <c>dbo.market</c>.</summary>
/// <param name="source">Opens connections.</param>
/// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
public sealed class MarketReader(DataSource source, ReaderOptions? options = null) : IMarketReader
{
    private const string Columns = """
        ticker AS Ticker, event_ticker AS EventTicker, market_type AS MarketType, status AS Status, yes_sub_title AS YesSubTitle, no_sub_title AS NoSubTitle,
        open_time AS OpenTime, close_time AS CloseTime, latest_expiration_time AS LatestExpirationTime, settlement_ts AS SettlementTs, result AS Result,
        settlement_value_e6 AS SettlementValueE6, first_seen_at AS FirstSeenAt, last_seen_at AS LastSeenAt
        """;

    private readonly ReaderOptions _options = Checked(options);

    /// <inheritdoc/>
    public async Task<Page<StoredMarket, MarketPosition>> ListAsync(MarketFilter filter, MarketPosition? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ReaderSupport.CheckLimit(limit);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<StoredMarket>(ReaderSupport.Command(
            $"""
            SELECT TOP (@take) {Columns} FROM dbo.market
            WHERE ticker > @after
              AND (@status IS NULL OR status = @status)
              AND (@event IS NULL OR event_ticker = @event)
              AND (@pattern IS NULL OR ticker LIKE @pattern ESCAPE '\')
            ORDER BY ticker
            """,
            new
            {
                take = limit + 1,
                after = ReaderSupport.Ansi(after?.Ticker ?? string.Empty, 128),
                status = ReaderSupport.Ansi(filter.Status, 32),
                @event = ReaderSupport.Ansi(filter.EventTicker, 128),
                pattern = ReaderSupport.Ansi(filter.Search is null ? null : ReaderSupport.LikePattern(filter.Search), 300),
            },
            _options,
            cancellationToken)).ConfigureAwait(false)).ToList();

        return rows.Count > limit
            ? new Page<StoredMarket, MarketPosition>(rows[..limit], new MarketPosition(rows[limit - 1].Ticker))
            : new Page<StoredMarket, MarketPosition>(rows, null);
    }

    /// <inheritdoc/>
    public async Task<StoredMarket?> GetAsync(string ticker, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<StoredMarket>(ReaderSupport.Command(
            $"SELECT {Columns} FROM dbo.market WHERE ticker = @ticker",
            new { ticker = ReaderSupport.Ansi(ticker, 128) },
            _options,
            cancellationToken)).ConfigureAwait(false);
    }

    private static ReaderOptions Checked(ReaderOptions? options)
    {
        options ??= new ReaderOptions();
        options.Validate();
        return options;
    }
}

/// <summary><see cref="ITickReader"/> over <c>dbo.ticker</c>.</summary>
/// <param name="source">Opens connections.</param>
/// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
public sealed class TickReader(DataSource source, ReaderOptions? options = null) : ITickReader
{
    private const string Columns = "ts AS Ts, price_e6 AS PriceE6, yes_bid_e6 AS YesBidE6, yes_ask_e6 AS YesAskE6, yes_bid_size_e2 AS YesBidSizeE2, yes_ask_size_e2 AS YesAskSizeE2, volume_e2 AS VolumeE2, open_interest_e2 AS OpenInterestE2, last_trade_size_e2 AS LastTradeSizeE2";

    // Updates can share a millisecond and the table has no key, so order by every column: the order is then the same on every read, which exact paging needs.
    private const string OrderAscending = "ts, price_e6, yes_bid_e6, yes_ask_e6, yes_bid_size_e2, yes_ask_size_e2, volume_e2, open_interest_e2, last_trade_size_e2";
    private const string OrderDescending = "ts DESC, price_e6 DESC, yes_bid_e6 DESC, yes_ask_e6 DESC, yes_bid_size_e2 DESC, yes_ask_size_e2 DESC, volume_e2 DESC, open_interest_e2 DESC, last_trade_size_e2 DESC";

    private readonly ReaderOptions _options = ReaderOptionsCheck.Checked(options);

    /// <inheritdoc/>
    public async Task<Page<StoredTick, TickPosition>?> ReadAsync(string ticker, DateTime? start, DateTime until, TickPosition? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ReaderSupport.CheckLimit(limit);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await ReaderSupport.MarketIdAsync(connection, ticker, _options, cancellationToken).ConfigureAwait(false) is not { } marketId)
        {
            return null;
        }

        if (start is null && after is null)
        {
            var latest = (await connection.QueryAsync<StoredTick>(ReaderSupport.Command(
                $"SELECT TOP (@limit) {Columns} FROM dbo.ticker WHERE market_id = @marketId AND ts >= @floor AND ts < @end ORDER BY {OrderDescending}",
                new { limit, marketId, floor = until - _options.LatestLookback, end = until },
                _options,
                cancellationToken)).ConfigureAwait(false)).ToList();
            latest.Reverse();
            return new Page<StoredTick, TickPosition>(latest, null);
        }

        var from = after?.Ts ?? start!.Value;
        var skip = after?.Skip ?? 0;
        var rows = (await connection.QueryAsync<StoredTick>(ReaderSupport.Command(
            $"SELECT {Columns} FROM dbo.ticker WHERE market_id = @marketId AND ts >= @from AND ts < @end ORDER BY {OrderAscending} OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY",
            new { marketId, from, end = until, skip, take = limit + 1 },
            _options,
            cancellationToken)).ConfigureAwait(false)).ToList();
        if (rows.Count <= limit)
        {
            return new Page<StoredTick, TickPosition>(rows, null);
        }

        var page = rows[..limit];
        var last = page[^1].Ts;
        var sharing = page.Count(r => r.Ts == last);
        return new Page<StoredTick, TickPosition>(page, new TickPosition(last, sharing + (last == from ? skip : 0)));
    }
}

/// <summary><see cref="ITradeReader"/> over <c>dbo.trade</c>.</summary>
/// <param name="source">Opens connections.</param>
/// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
public sealed class TradeReader(DataSource source, ReaderOptions? options = null) : ITradeReader
{
    private const string Columns = "ts AS Ts, trade_id AS TradeId, yes_price_e6 AS YesPriceE6, no_price_e6 AS NoPriceE6, count_e2 AS CountE2, taker_side AS TakerSide, is_block AS IsBlock";

    private readonly ReaderOptions _options = ReaderOptionsCheck.Checked(options);

    /// <inheritdoc/>
    public async Task<Page<StoredTrade, TradePosition>?> ReadAsync(string ticker, DateTime? start, DateTime until, TradePosition? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ReaderSupport.CheckLimit(limit);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await ReaderSupport.MarketIdAsync(connection, ticker, _options, cancellationToken).ConfigureAwait(false) is not { } marketId)
        {
            return null;
        }

        if (start is null && after is null)
        {
            var latest = (await connection.QueryAsync<StoredTrade>(ReaderSupport.Command(
                $"SELECT TOP (@limit) {Columns} FROM dbo.trade WHERE market_id = @marketId AND ts >= @floor AND ts < @end ORDER BY ts DESC, trade_id DESC",
                new { limit, marketId, floor = until - _options.LatestLookback, end = until },
                _options,
                cancellationToken)).ConfigureAwait(false)).ToList();
            latest.Reverse();
            return new Page<StoredTrade, TradePosition>(latest, null);
        }

        var from = after?.Ts ?? start!.Value;
        var resume = after is null ? string.Empty : " AND (ts > @afterTs OR (ts = @afterTs AND trade_id > @afterId))";
        var rows = (await connection.QueryAsync<StoredTrade>(ReaderSupport.Command(
            $"SELECT TOP (@take) {Columns} FROM dbo.trade WHERE market_id = @marketId AND ts >= @from AND ts < @end{resume} ORDER BY ts, trade_id",
            new { take = limit + 1, marketId, from, end = until, afterTs = after?.Ts, afterId = ReaderSupport.Ansi(after?.TradeId, 64) },
            _options,
            cancellationToken)).ConfigureAwait(false)).ToList();
        return rows.Count > limit
            ? new Page<StoredTrade, TradePosition>(rows[..limit], new TradePosition(rows[limit - 1].Ts, rows[limit - 1].TradeId))
            : new Page<StoredTrade, TradePosition>(rows, null);
    }
}

/// <summary><see cref="IGapReader"/> over <c>dbo.ingest_gap</c>.</summary>
/// <param name="source">Opens connections.</param>
/// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
public sealed class GapReader(DataSource source, ReaderOptions? options = null) : IGapReader
{
    private readonly ReaderOptions _options = ReaderOptionsCheck.Checked(options);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredGap>> ReadAsync(DateTime start, DateTime until, int limit, CancellationToken cancellationToken = default)
    {
        ReaderSupport.CheckLimit(limit);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<StoredGap>(ReaderSupport.Command(
            """
            SELECT TOP (@limit) gap_id AS Id, kind AS Kind, started_at AS StartedAt, ended_at AS EndedAt, detail AS Detail FROM dbo.ingest_gap
            WHERE started_at < @end AND (ended_at IS NULL OR ended_at >= @start)
            ORDER BY started_at DESC, gap_id DESC
            """,
            new { limit, start, end = until },
            _options,
            cancellationToken)).ConfigureAwait(false)).ToList();
    }
}

/// <summary><see cref="IStatusReader"/>: small catalog and bounded recent-window queries, so the report stays fast however large the tables grow.</summary>
/// <param name="source">Opens connections.</param>
/// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
public sealed class StatusReader(DataSource source, ReaderOptions? options = null) : IStatusReader
{
    // The newest row is looked for in a few minutes first and only then in wider windows, so a healthy stream costs a scan of a few minutes of rows.
    private static readonly TimeSpan[] FreshnessWindows = [TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), TimeSpan.FromHours(24)];

    private readonly ReaderOptions _options = ReaderOptionsCheck.Checked(options);

    /// <inheritdoc/>
    public async Task<StatusSnapshot> ReadAsync(DateTime now, TimeSpan gapWindow, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(gapWindow, TimeSpan.Zero);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);

        var run = await connection.QuerySingleOrDefaultAsync<(string Value, DateTime UpdatedAt)?>(ReaderSupport.Command(
            "SELECT value AS Value, updated_at AS UpdatedAt FROM dbo.ingest_state WHERE name = @name",
            new { name = ReaderSupport.Ansi(IngestStateKeys.Run, 64) },
            _options,
            cancellationToken)).ConfigureAwait(false);

        var file = await connection.QuerySingleAsync<(long Bytes, long? MaxBytes)>(ReaderSupport.Command(
            """
            SELECT CAST(SUM(CAST(size AS bigint)) * 8192 AS bigint) AS Bytes,
                   CASE WHEN MIN(max_size) = -1 THEN NULL ELSE CAST(SUM(CAST(max_size AS bigint)) * 8192 AS bigint) END AS MaxBytes
            FROM sys.database_files WHERE type = 0
            """,
            null,
            _options,
            cancellationToken)).ConfigureAwait(false);

        var markets = (await connection.QueryAsync<(string Status, long Count)>(ReaderSupport.Command(
            "SELECT status AS Status, COUNT_BIG(*) AS Count FROM dbo.market GROUP BY status",
            null,
            _options,
            cancellationToken)).ConfigureAwait(false)).ToDictionary(r => r.Status, r => r.Count, StringComparer.Ordinal);

        var gaps = (await connection.QueryAsync<(string Kind, long Count)>(ReaderSupport.Command(
            "SELECT kind AS Kind, COUNT_BIG(*) AS Count FROM dbo.ingest_gap WHERE started_at >= @since GROUP BY kind",
            new { since = now - gapWindow },
            _options,
            cancellationToken)).ConfigureAwait(false)).ToDictionary(r => r.Kind, r => r.Count, StringComparer.Ordinal);

        return new StatusSnapshot(
            run is { } r ? new IngestStateEntry(r.Value, DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc)) : null,
            await NewestAsync(connection, "dbo.ticker", now, cancellationToken).ConfigureAwait(false),
            await NewestAsync(connection, "dbo.trade", now, cancellationToken).ConfigureAwait(false),
            file.Bytes,
            file.MaxBytes,
            await DiskFreeAsync(connection, cancellationToken).ConfigureAwait(false),
            markets,
            gaps);
    }

    private async Task<DateTime?> NewestAsync(IDbConnection connection, string table, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var window in FreshnessWindows)
        {
            var newest = await connection.ExecuteScalarAsync<DateTime?>(ReaderSupport.Command(
                $"SELECT MAX(ts) FROM {table} WHERE ts >= @since AND ts < @until",
                new { since = now - window, until = now.AddMinutes(1) },
                _options,
                cancellationToken)).ConfigureAwait(false);
            if (newest is { } found)
            {
                return DateTime.SpecifyKind(found, DateTimeKind.Utc);
            }
        }

        return null;
    }

    private async Task<long?> DiskFreeAsync(IDbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.ExecuteScalarAsync<long?>(ReaderSupport.Command(
                "SELECT TOP (1) vs.available_bytes FROM sys.database_files f CROSS APPLY sys.dm_os_volume_stats(DB_ID(), f.file_id) vs WHERE f.type = 0",
                null,
                _options,
                cancellationToken)).ConfigureAwait(false);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            // Needs VIEW SERVER PERFORMANCE STATE; a login without it just gets no figure.
            return null;
        }
    }
}

internal static class ReaderOptionsCheck
{
    public static ReaderOptions Checked(ReaderOptions? options)
    {
        options ??= new ReaderOptions();
        options.Validate();
        return options;
    }
}
