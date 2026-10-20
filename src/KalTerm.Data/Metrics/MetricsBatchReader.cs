using Dapper;
using KalTerm.Data.Reading;

namespace KalTerm.Data.Metrics;

/// <summary>One market's inputs, as the set-based reader finds them.</summary>
/// <param name="MarketId">The market.</param>
/// <param name="Inputs">What its metrics are made of.</param>
public sealed record MarketInputs(int MarketId, MetricsInputs Inputs);

/// <summary>Reads the metrics inputs of every active market in a few set-based queries.</summary>
public interface IMetricsBatchReader
{
    /// <summary>
    /// Finds every market with a ticker update in the last hour, a trade in the last 24 hours or an hourly candle in the last 25 hours, and hands its inputs over in batches (so memory stays bounded).
    /// </summary>
    /// <param name="asOf">The moment (UTC).</param>
    /// <param name="onBatch">Called with each non-empty batch, in market id order.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many markets were found.</returns>
    Task<int> ReadAsync(DateTime asOf, Func<IReadOnlyList<MarketInputs>, Task> onBatch, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IMetricsBatchReader"/> in SQL: per slice of market ids, one pass over the last hour of ticker rows (the newest per market), one over the 24 hours of trades (summed per market) and
/// one over the hourly candles (the newest 50 priced closes per market), all joined to the markets of the slice that are active. The parts go through <see cref="MetricsInputsBuilder"/>, the same
/// code the per-market reader uses. Ticker rows older than an hour are never read: a market quiet for longer shows no book figures instead of stale ones.
/// </summary>
/// <param name="source">Opens connections.</param>
/// <param name="idsPerBatch">How many market ids one slice covers.</param>
public sealed class MetricsBatchReader(DataSource source, int idsPerBatch = MetricsBatchReader.IdsPerBatch) : IMetricsBatchReader
{
    /// <summary>The default slice of market ids.</summary>
    public const int IdsPerBatch = 25_000;

    private static readonly TimeSpan TickWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan CandleLookback = TimeSpan.FromDays(30);
    private static readonly TimeSpan CandleActive = TimeSpan.FromHours(25);

    /// <inheritdoc/>
    public async Task<int> ReadAsync(DateTime asOf, Func<IReadOnlyList<MarketInputs>, Task> onBatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onBatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(idsPerBatch, 1);
        var from = asOf - MetricsInputsBuilder.Window;
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var maxId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT MAX(market_id) FROM dbo.market", commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(false) ?? 0;
        var gaps = (await connection.QueryAsync<GapRow>(new CommandDefinition(
            "SELECT market_id AS MarketId FROM dbo.ingest_gap WHERE started_at < @asOf AND (ended_at IS NULL OR ended_at > @from)",
            new { asOf, from },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var everyMarketGapped = gaps.Any(g => g.MarketId is null);
        var gappedMarkets = gaps.Where(g => g.MarketId is not null).Select(g => g.MarketId!.Value).ToHashSet();

        var found = 0;
        for (var low = 1; low <= maxId; low += idsPerBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await ReadSliceAsync(connection, asOf, from, low, low + idsPerBatch - 1, everyMarketGapped, gappedMarkets, cancellationToken).ConfigureAwait(false);
            if (batch.Count > 0)
            {
                found += batch.Count;
                await onBatch(batch).ConfigureAwait(false);
            }
        }

        return found;
    }

    private static async Task<IReadOnlyList<MarketInputs>> ReadSliceAsync(
        System.Data.Common.DbConnection connection,
        DateTime asOf,
        DateTime from,
        int low,
        int high,
        bool everyMarketGapped,
        HashSet<int> gappedMarkets,
        CancellationToken cancellationToken)
    {
        var parameters = new { asOf, from, tickFrom = asOf - TickWindow, candleFrom = asOf - CandleLookback, activeFrom = asOf - CandleActive, low, high };
        // The temporary table is made by a command without parameters: one with parameters runs inside sp_executesql, which drops what it creates when it ends.
        await connection.ExecuteAsync(new CommandDefinition("CREATE TABLE #u (market_id int NOT NULL PRIMARY KEY)", commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT #u (market_id)
            SELECT market_id FROM dbo.ticker WHERE ts > @tickFrom AND ts <= @asOf AND market_id BETWEEN @low AND @high GROUP BY market_id
            UNION
            SELECT market_id FROM dbo.trade WHERE ts > @from AND ts <= @asOf AND market_id BETWEEN @low AND @high GROUP BY market_id
            UNION
            SELECT market_id FROM dbo.candle WHERE interval_s = 3600 AND bucket_start > @activeFrom AND bucket_start < @asOf AND market_id BETWEEN @low AND @high GROUP BY market_id;
            """,
            parameters,
            commandTimeout: 120,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        try
        {
            var ticks = (await connection.QueryAsync<TickRow>(new CommandDefinition(
                """
                SELECT MarketId, Ts, PriceE6, YesBidE6, YesAskE6, OpenInterestE2 FROM (
                    SELECT t.market_id AS MarketId, t.ts AS Ts, t.price_e6 AS PriceE6, t.yes_bid_e6 AS YesBidE6, t.yes_ask_e6 AS YesAskE6, t.open_interest_e2 AS OpenInterestE2,
                           ROW_NUMBER() OVER (PARTITION BY t.market_id ORDER BY t.ts DESC) AS rn
                    FROM dbo.ticker t WHERE t.ts > @tickFrom AND t.ts <= @asOf AND t.market_id BETWEEN @low AND @high) x
                WHERE rn = 1
                """,
                parameters,
                commandTimeout: 120,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToDictionary(t => t.MarketId);
            var trades = (await connection.QueryAsync<TradeRow>(new CommandDefinition(
                """
                SELECT market_id AS MarketId, COUNT_BIG(*) AS Count, SUM(CAST(count_e2 AS decimal(38, 0))) AS Volume, SUM(CAST(yes_price_e6 AS decimal(38, 0)) * count_e2) AS PriceTimesCount
                FROM dbo.trade WHERE ts > @from AND ts <= @asOf AND market_id BETWEEN @low AND @high GROUP BY market_id
                """,
                parameters,
                commandTimeout: 120,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToDictionary(t => t.MarketId);
            var candles = (await connection.QueryAsync<CandleRow>(new CommandDefinition(
                """
                SELECT MarketId, BucketStart, CloseE6, Partial FROM (
                    SELECT c.market_id AS MarketId, c.bucket_start AS BucketStart, c.close_e6 AS CloseE6, CAST(c.trades_partial | c.book_partial AS bit) AS Partial,
                           ROW_NUMBER() OVER (PARTITION BY c.market_id ORDER BY c.bucket_start DESC) AS rn
                    FROM dbo.candle c JOIN #u ON #u.market_id = c.market_id
                    WHERE c.interval_s = 3600 AND c.bucket_start >= @candleFrom AND c.bucket_start < @asOf AND c.close_e6 IS NOT NULL) x
                WHERE rn <= 50
                """,
                parameters,
                commandTimeout: 120,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToLookup(c => c.MarketId);
            var ids = (await connection.QueryAsync<int>(new CommandDefinition("SELECT market_id FROM #u ORDER BY market_id", commandTimeout: 60, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            var result = new List<MarketInputs>(ids.Count);
            foreach (var id in ids)
            {
                var tick = ticks.TryGetValue(id, out var t) ? new MetricsTick(DateTime.SpecifyKind(t.Ts, DateTimeKind.Utc), t.PriceE6, t.YesBidE6, t.YesAskE6, t.OpenInterestE2) : null;
                var sums = trades.TryGetValue(id, out var s) ? new MetricsTradeSums(s.Count, s.Volume, s.PriceTimesCount) : new MetricsTradeSums(0, 0m, 0m);
                var hourly = candles[id].Select(c => new HourlyClose(DateTime.SpecifyKind(c.BucketStart, DateTimeKind.Utc), c.CloseE6, c.Partial));
                result.Add(new MarketInputs(id, MetricsInputsBuilder.Build(asOf, tick, sums, everyMarketGapped || gappedMarkets.Contains(id), hourly)));
            }

            return result;
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition("DROP TABLE IF EXISTS #u", commandTimeout: 30, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
        }
    }

    private sealed record GapRow(int? MarketId);

    private sealed record TickRow(int MarketId, DateTime Ts, long? PriceE6, long? YesBidE6, long? YesAskE6, long? OpenInterestE2);

    private sealed record TradeRow(int MarketId, long Count, decimal Volume, decimal PriceTimesCount);

    private sealed record CandleRow(int MarketId, DateTime BucketStart, long? CloseE6, bool Partial);
}
