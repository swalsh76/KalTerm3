using System.Globalization;
using System.Text;
using Dapper;

namespace KalTerm.Data.Alerts;

/// <summary>Reads what the alert evaluator needs about a set of markets.</summary>
public interface IAlertDataReader
{
    /// <summary>What can be seen of each market at a moment.</summary>
    /// <param name="marketIds">The markets that have alerts.</param>
    /// <param name="now">The moment (UTC).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>One observation per market.</returns>
    Task<IReadOnlyDictionary<int, AlertObservation>> ReadAsync(IReadOnlyCollection<int> marketIds, DateTime now, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IAlertDataReader"/> over the raw ticker and trade rows, only for the markets asked for (so the cost follows the number of alerts, not the number of markets): the latest ticker update
/// within a day, the open interest an hour ago, the trade volume of the last 15 minutes and of the 24 hours before, and the gaps that overlap those windows. A gap that covers every market, or the
/// market itself, makes the windows it overlaps partial.
/// </summary>
/// <param name="source">Opens connections.</param>
public sealed class AlertDataReader(DataSource source) : IAlertDataReader
{
    private static readonly TimeSpan[] TickWindows = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(24)];
    private static readonly TimeSpan PriceWindow = TimeSpan.FromMinutes(1);

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<int, AlertObservation>> ReadAsync(IReadOnlyCollection<int> marketIds, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marketIds);
        var result = new Dictionary<int, AlertObservation>();
        if (marketIds.Count == 0)
        {
            return result;
        }

        var recentFrom = now - AlertKinds.SpikeWindow;
        var baselineFrom = recentFrom - TimeSpan.FromHours(24);
        var hourAgo = now - AlertKinds.OiWindow;
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("CREATE TABLE #m (market_id int NOT NULL PRIMARY KEY)", commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(false);
        try
        {
            foreach (var chunk in marketIds.Distinct().Order().Chunk(1_000))
            {
                var values = new StringBuilder();
                foreach (var id in chunk)
                {
                    values.Append(CultureInfo.InvariantCulture, $"({id}),");
                }

                await connection.ExecuteAsync(new CommandDefinition($"INSERT #m (market_id) VALUES {values.ToString().TrimEnd(',')}", commandTimeout: 60, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            var parameters = new { now, recentFrom, baselineFrom, hourAgo };
            // Looking a market's newest tick up is cheap when the market ids are in the statement itself (the ordered columnstore can then skip whole segments) and very costly when they come from a
            // join (every market is scanned: ten seconds a lookup). So the ids are written into the statement and the lookups widen step by step: busy markets are found in the first minute and
            // only the quiet ones need the longer windows.
            var ids = marketIds.Distinct().Order().ToList();
            var latest = await LatestAsync(connection, ids, now, string.Empty, TickWindows, cancellationToken).ConfigureAwait(false);
            var latestPrice = await LatestAsync(connection, ids, now, "AND price_e6 IS NOT NULL", TickWindows, cancellationToken).ConfigureAwait(false);
            var hourAgoOi = await LatestAsync(connection, ids, hourAgo, "AND open_interest_e2 IS NOT NULL", TickWindows, cancellationToken).ConfigureAwait(false);
            var volumes = (await connection.QueryAsync<VolumeRow>(new CommandDefinition(
                """
                SELECT t.market_id AS MarketId,
                       ISNULL(SUM(CASE WHEN t.ts > @recentFrom THEN t.count_e2 ELSE 0 END), 0) AS Recent,
                       ISNULL(SUM(CASE WHEN t.ts <= @recentFrom THEN t.count_e2 ELSE 0 END), 0) AS Baseline
                FROM dbo.trade t JOIN #m ON #m.market_id = t.market_id
                WHERE t.ts > @baselineFrom AND t.ts <= @now
                GROUP BY t.market_id
                """,
                parameters,
                commandTimeout: 60,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToDictionary(v => v.MarketId);
            var gaps = (await connection.QueryAsync<GapRow>(new CommandDefinition(
                "SELECT market_id AS MarketId, started_at AS StartedAt, ended_at AS EndedAt FROM dbo.ingest_gap WHERE started_at < @now AND (ended_at IS NULL OR ended_at > @hourAgo)",
                parameters,
                commandTimeout: 30,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            foreach (var id in marketIds)
            {
                latest.TryGetValue(id, out var tick);
                latestPrice.TryGetValue(id, out var priceTick);
                hourAgoOi.TryGetValue(id, out var before);
                volumes.TryGetValue(id, out var volume);
                bool Overlaps(DateTime from) => gaps.Any(g => (g.MarketId is null || g.MarketId == id) && g.StartedAt < now && (g.EndedAt is null || g.EndedAt > from));
                result[id] = new AlertObservation(
                    priceTick?.PriceE6,
                    tick?.AskE6 is { } ask && tick.BidE6 is { } bid ? ask - bid : null,
                    volume?.Recent ?? 0,
                    volume?.Baseline ?? 0,
                    tick?.OpenInterestE2,
                    before?.OpenInterestE2,
                    Overlaps(now - PriceWindow),
                    Overlaps(recentFrom),
                    Overlaps(hourAgo));
            }

            return result;
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition("DROP TABLE IF EXISTS #m", commandTimeout: 30, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <summary>The newest row at or before <paramref name="upper"/> that satisfies <paramref name="extra"/> for each market, looked for in widening windows.</summary>
    private static async Task<Dictionary<int, TickRow>> LatestAsync(System.Data.Common.DbConnection connection, IReadOnlyList<int> ids, DateTime upper, string extra, TimeSpan[] windows, CancellationToken cancellationToken)
    {
        var found = new Dictionary<int, TickRow>();
        foreach (var window in windows)
        {
            var remaining = ids.Where(id => !found.ContainsKey(id)).ToList();
            if (remaining.Count == 0)
            {
                break;
            }

            foreach (var chunk in remaining.Chunk(500))
            {
                var rows = await connection.QueryAsync<TickRow>(new CommandDefinition(
                    $"""
                    SELECT MarketId, PriceE6, BidE6, AskE6, OpenInterestE2 FROM (
                        SELECT market_id AS MarketId, price_e6 AS PriceE6, yes_bid_e6 AS BidE6, yes_ask_e6 AS AskE6, open_interest_e2 AS OpenInterestE2,
                               ROW_NUMBER() OVER (PARTITION BY market_id ORDER BY ts DESC) AS rn
                        FROM dbo.ticker WHERE market_id IN ({string.Join(',', chunk)}) AND ts > @from AND ts <= @upper {extra}) x
                    WHERE rn = 1
                    """,
                    new { from = upper - window, upper },
                    commandTimeout: 60,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    found[row.MarketId] = row;
                }
            }
        }

        return found;
    }

    private sealed record TickRow(int MarketId, long? PriceE6, long? BidE6, long? AskE6, long? OpenInterestE2);

    private sealed record VolumeRow(int MarketId, long Recent, long Baseline);

    private sealed record GapRow(int? MarketId, DateTime StartedAt, DateTime? EndedAt);
}
