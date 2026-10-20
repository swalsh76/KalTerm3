using System.Data;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Candles;

/// <summary>Builds candles from the raw ticker and trade rows.</summary>
public interface ICandleBuilder
{
    /// <summary>
    /// (Re)builds the candles of one interval over [<paramref name="from"/>, <paramref name="until"/>): the rows already there for those buckets are replaced, so running it again is safe.
    /// Both ends must fall on bucket boundaries.
    /// </summary>
    /// <param name="intervalSeconds">The bucket length.</param>
    /// <param name="from">The first bucket's start (UTC).</param>
    /// <param name="until">The end of the last bucket (UTC, exclusive).</param>
    /// <param name="marketIds">Only these markets; <see langword="null"/> for every market.</param>
    /// <param name="now">The current UTC time; a gap that has not ended is taken to run until then.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many candles were written.</returns>
    Task<int> BuildAsync(int intervalSeconds, DateTime from, DateTime until, IReadOnlyCollection<int>? marketIds, DateTime now, CancellationToken cancellationToken = default);
}

/// <summary>Computes candles from the raw rows without storing them.</summary>
public interface ICandleComputer
{
    /// <summary>Computes the candles of one interval over [<paramref name="from"/>, <paramref name="until"/>) for some markets; nothing is stored.</summary>
    /// <param name="intervalSeconds">The bucket length.</param>
    /// <param name="from">The first bucket's start (UTC).</param>
    /// <param name="until">The end of the last bucket (UTC, exclusive).</param>
    /// <param name="marketIds">The markets.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The candles, oldest first.</returns>
    Task<IReadOnlyList<StoredCandle>> ComputeAsync(int intervalSeconds, DateTime from, DateTime until, IReadOnlyCollection<int> marketIds, DateTime now, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="ICandleBuilder"/> in SQL: one pass over the trades and one over the ticker rows of a slice of markets, grouped by bucket. The price is the yes price of the trades (open and close
/// are the first and last trade of the bucket by time, then trade id; high, low, volume and count over all of them); bid, ask and open interest are the last ticker update of the bucket.
/// Markets are processed in slices of ids so that a day over every market does not need one enormous sort. A bucket that overlaps a gap is flagged (see migration 0005).
/// </summary>
/// <param name="source">Opens connections.</param>
/// <param name="marketsPerPass">How many market ids one pass covers when every market is built.</param>
public sealed class CandleBuilder(DataSource source, int marketsPerPass = CandleBuilder.MarketsPerPass) : ICandleBuilder, ICandleComputer
{
    /// <summary>How many market ids one pass covers when every market is built.</summary>
    public const int MarketsPerPass = 25_000;

    private const string DeleteSql = """
        DELETE c FROM dbo.candle AS c
        WHERE c.interval_s = @i AND c.bucket_start >= @from AND c.bucket_start < @until AND {candleFilter};

        """;

    private const string Build = """
        SELECT started_at, COALESCE(ended_at, @now) AS ended_at, kind, market_id, CASE WHEN backfill_status IN ('done', 'skipped') THEN 0 ELSE 1 END AS trades_open
        INTO #gaps FROM dbo.ingest_gap
        WHERE started_at < @until AND COALESCE(ended_at, @now) > @from;

        WITH trades AS (
            SELECT t.market_id, t.ts, t.trade_id, t.yes_price_e6 AS p, t.count_e2 AS q,
                   DATEADD(second, (DATEDIFF_BIG(second, @origin, t.ts) / @i) * @i, @origin) AS b
            FROM dbo.trade AS t WHERE t.ts >= @from AND t.ts < @until AND {filter}),
        ranked AS (
            SELECT market_id, b, p, q,
                   ROW_NUMBER() OVER (PARTITION BY market_id, b ORDER BY ts, trade_id) AS r_first,
                   ROW_NUMBER() OVER (PARTITION BY market_id, b ORDER BY ts DESC, trade_id DESC) AS r_last
            FROM trades),
        trade_agg AS (
            SELECT market_id, b, MAX(p) AS high_p, MIN(p) AS low_p, SUM(q) AS volume, COUNT(*) AS n,
                   MAX(CASE WHEN r_first = 1 THEN p END) AS open_p, MAX(CASE WHEN r_last = 1 THEN p END) AS close_p
            FROM ranked GROUP BY market_id, b),
        ticks AS (
            SELECT k.market_id, k.yes_bid_e6, k.yes_ask_e6, k.open_interest_e2,
                   DATEADD(second, (DATEDIFF_BIG(second, @origin, k.ts) / @i) * @i, @origin) AS b,
                   ROW_NUMBER() OVER (PARTITION BY k.market_id, DATEADD(second, (DATEDIFF_BIG(second, @origin, k.ts) / @i) * @i, @origin)
                                      ORDER BY k.ts DESC, k.price_e6 DESC, k.yes_bid_e6 DESC, k.yes_ask_e6 DESC, k.yes_bid_size_e2 DESC, k.yes_ask_size_e2 DESC, k.volume_e2 DESC, k.open_interest_e2 DESC, k.last_trade_size_e2 DESC) AS r
            FROM dbo.ticker AS k WHERE k.ts >= @from AND k.ts < @until AND {tickFilter}),
        last_tick AS (SELECT market_id, b, yes_bid_e6, yes_ask_e6, open_interest_e2 FROM ticks WHERE r = 1),
        combined AS (
            SELECT COALESCE(a.market_id, k.market_id) AS market_id, COALESCE(a.b, k.b) AS b,
                   a.open_p, a.high_p, a.low_p, a.close_p, COALESCE(a.volume, 0) AS volume, COALESCE(a.n, 0) AS n,
                   k.yes_bid_e6, k.yes_ask_e6, k.open_interest_e2
            FROM trade_agg AS a FULL OUTER JOIN last_tick AS k ON a.market_id = k.market_id AND a.b = k.b)
        INSERT {target} (interval_s, market_id, bucket_start, open_e6, high_e6, low_e6, close_e6, volume_e2, trade_count, yes_bid_e6, yes_ask_e6, open_interest_e2,
                           trades_partial, book_partial, gap_kind, gap_seconds, built_at)
        SELECT @i, x.market_id, x.b, x.open_p, x.high_p, x.low_p, x.close_p, x.volume, x.n, x.yes_bid_e6, x.yes_ask_e6, x.open_interest_e2,
               COALESCE(g.trades_open, 0), CASE WHEN g.gaps IS NULL THEN 0 ELSE 1 END, g.kind, g.seconds, @now
        FROM combined AS x
        OUTER APPLY (
            SELECT COUNT(*) AS gaps, MAX(o.trades_open) AS trades_open, SUM(o.seconds) AS seconds,
                   (SELECT TOP (1) o2.kind FROM (
                        SELECT gp.kind, DATEDIFF(second, CASE WHEN gp.started_at > x.b THEN gp.started_at ELSE x.b END, CASE WHEN gp.ended_at < DATEADD(second, @i, x.b) THEN gp.ended_at ELSE DATEADD(second, @i, x.b) END) AS seconds
                        FROM #gaps AS gp WHERE gp.started_at < DATEADD(second, @i, x.b) AND gp.ended_at > x.b AND (gp.market_id IS NULL OR gp.market_id = x.market_id)) AS o2
                    ORDER BY o2.seconds DESC) AS kind
            FROM (
                SELECT gp.trades_open, DATEDIFF(second, CASE WHEN gp.started_at > x.b THEN gp.started_at ELSE x.b END, CASE WHEN gp.ended_at < DATEADD(second, @i, x.b) THEN gp.ended_at ELSE DATEADD(second, @i, x.b) END) AS seconds
                FROM #gaps AS gp WHERE gp.started_at < DATEADD(second, @i, x.b) AND gp.ended_at > x.b AND (gp.market_id IS NULL OR gp.market_id = x.market_id)) AS o
            HAVING COUNT(*) > 0
        ) AS g;
        SELECT @@ROWCOUNT AS written;
        DROP TABLE #gaps;

        """;

    /// <inheritdoc/>
    public async Task<int> BuildAsync(int intervalSeconds, DateTime from, DateTime until, IReadOnlyCollection<int>? marketIds, DateTime now, CancellationToken cancellationToken = default)
    {
        if (!CandleRules.Storable.Contains(intervalSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "not a storable candle interval");
        }

        if (from >= until || CandleRules.BucketStart(from, intervalSeconds) != from || CandleRules.BucketStart(until, intervalSeconds) != until)
        {
            throw new ArgumentException("the range must start and end on bucket boundaries and not be empty");
        }

        if (marketIds is { Count: 0 })
        {
            return 0;
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var written = 0;
        foreach (var (filter, parameters) in await SlicesAsync(connection, marketIds, cancellationToken).ConfigureAwait(false))
        {
            written += await BuildSliceAsync(connection, intervalSeconds, from, until, now, filter, parameters, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    private async Task<List<(string Filter, SqlParameter[] Parameters)>> SlicesAsync(SqlConnection connection, IReadOnlyCollection<int>? marketIds, CancellationToken cancellationToken)
    {
        if (marketIds is not null)
        {
            var list = string.Join(',', marketIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return [($"market_id IN ({list})", [])];
        }

        await using var command = new SqlCommand("SELECT ISNULL(MIN(market_id), 0), ISNULL(MAX(market_id), -1) FROM dbo.market", connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        int low = reader.GetInt32(0), high = reader.GetInt32(1);
        var slices = new List<(string, SqlParameter[])>();
        for (var start = low; start <= high; start += marketsPerPass)
        {
            slices.Add(($"market_id >= {start} AND market_id < {start + marketsPerPass}", []));
        }

        return slices;
    }

    /// <summary>
    /// Works out the candles of a few markets over a range from the raw rows without storing them, for an interval that is not stored (the API's fallback). The same SQL as
    /// <see cref="BuildAsync"/>, so the numbers are the ones a stored candle would have.
    /// </summary>
    /// <param name="intervalSeconds">The bucket length.</param>
    /// <param name="from">The first bucket's start (UTC).</param>
    /// <param name="until">The end of the last bucket (UTC, exclusive).</param>
    /// <param name="marketIds">The markets (at least one).</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The candles, by market and time.</returns>
    public async Task<IReadOnlyList<StoredCandle>> ComputeAsync(int intervalSeconds, DateTime from, DateTime until, IReadOnlyCollection<int> marketIds, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marketIds);
        if (!CandleRules.Storable.Contains(intervalSeconds) || from >= until || CandleRules.BucketStart(from, intervalSeconds) != from || CandleRules.BucketStart(until, intervalSeconds) != until)
        {
            throw new ArgumentException("the interval must be storable and the range must start and end on bucket boundaries");
        }

        if (marketIds.Count == 0)
        {
            return [];
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var filter = $"market_id IN ({string.Join(',', marketIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)))})";
        var sql = "SELECT TOP (0) * INTO #out FROM dbo.candle;" + Environment.NewLine + Fill(Build, filter).Replace("{target}", "#out", StringComparison.Ordinal) + "SELECT * FROM #out ORDER BY market_id, bucket_start;";
        await using var command = Command(connection, null, sql, intervalSeconds, from, until, now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);   // skip the row count of the insert
        var rows = new List<StoredCandle>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(CandleReader.Read(reader));
        }

        return rows;
    }

    private static string Fill(string sql, string filter) => sql
        .Replace("{candleFilter}", filter.Replace("market_id", "c.market_id", StringComparison.Ordinal), StringComparison.Ordinal)
        .Replace("{tickFilter}", filter.Replace("market_id", "k.market_id", StringComparison.Ordinal), StringComparison.Ordinal)
        .Replace("{filter}", filter.Replace("market_id", "t.market_id", StringComparison.Ordinal), StringComparison.Ordinal);

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql, int intervalSeconds, DateTime from, DateTime until, DateTime now)
    {
        var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@i", SqlDbType.Int) { Value = intervalSeconds });
        command.Parameters.Add(new SqlParameter("@origin", SqlDbType.DateTime2) { Scale = 0, Value = CandleRules.Origin(intervalSeconds) });
        command.Parameters.Add(new SqlParameter("@from", SqlDbType.DateTime2) { Scale = 3, Value = from });
        command.Parameters.Add(new SqlParameter("@until", SqlDbType.DateTime2) { Scale = 3, Value = until });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Scale = 3, Value = now });
        return command;
    }

    private static async Task<int> BuildSliceAsync(SqlConnection connection, int intervalSeconds, DateTime from, DateTime until, DateTime now, string filter, SqlParameter[] extra, CancellationToken cancellationToken)
    {
        var sql = Fill(DeleteSql + Build, filter).Replace("{target}", "dbo.candle", StringComparison.Ordinal);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction, sql, intervalSeconds, from, until, now);
        command.Parameters.AddRange(extra);
        var written = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }
}
