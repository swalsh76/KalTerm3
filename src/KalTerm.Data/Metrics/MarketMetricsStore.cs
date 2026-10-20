using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Metrics;

/// <summary>Writes the <c>dbo.market_metrics</c> result table.</summary>
public interface IMarketMetricsStore
{
    /// <summary>Inserts the markets that have no row and updates the ones whose figures differ; a market whose figures are the same is not written.</summary>
    /// <param name="rows">The metrics of a batch of markets.</param>
    /// <param name="changedAt">The moment recorded on every row that is written.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many rows were inserted or updated.</returns>
    Task<int> UpsertAsync(IReadOnlyList<(int MarketId, MarketMetrics Metrics)> rows, DateTime changedAt, CancellationToken cancellationToken = default);

    /// <summary>Deletes the rows of markets that are not in the set (the ones idle for 24 hours).</summary>
    /// <param name="keep">The ids to keep.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many rows were deleted.</returns>
    Task<int> DeleteOthersAsync(IReadOnlyCollection<int> keep, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IMarketMetricsStore"/> over <c>dbo.market_metrics</c>, with bulk copy into a temporary table and one set-based statement per batch.</summary>
/// <param name="source">Opens connections.</param>
public sealed class MarketMetricsStore(DataSource source) : IMarketMetricsStore
{
    private const string Columns = "market_id, last_time, last_e6, change_24h_e6, volume_e2, trade_count, open_interest_e2, yes_bid_e6, yes_ask_e6, spread_e6, vwap_e6, sma20_e6, sma50_e6, volatility_e6, is_partial";

    /// <inheritdoc/>
    public async Task<int> UpsertAsync(IReadOnlyList<(int MarketId, MarketMetrics Metrics)> rows, DateTime changedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return 0;
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE TABLE #stage (market_id int NOT NULL PRIMARY KEY, last_time datetime2(3) NULL, last_e6 bigint NULL, change_24h_e6 bigint NULL, volume_e2 bigint NOT NULL, trade_count bigint NOT NULL, open_interest_e2 bigint NULL, yes_bid_e6 bigint NULL, yes_ask_e6 bigint NULL, spread_e6 bigint NULL, vwap_e6 bigint NULL, sma20_e6 bigint NULL, sma50_e6 bigint NULL, volatility_e6 bigint NULL, is_partial bit NOT NULL)",
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        using (var table = ToTable(rows))
        {
            using var copy = new SqlBulkCopy((SqlConnection)connection) { DestinationTableName = "#stage", BulkCopyTimeout = 120 };
            foreach (DataColumn column in table.Columns)
            {
                copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await copy.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
        }

        // A row is written only when a figure differs (EXCEPT treats two NULLs as equal), so an unchanged market costs no log.
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"""
            UPDATE t SET last_time = s.last_time, last_e6 = s.last_e6, change_24h_e6 = s.change_24h_e6, volume_e2 = s.volume_e2, trade_count = s.trade_count, open_interest_e2 = s.open_interest_e2,
                         yes_bid_e6 = s.yes_bid_e6, yes_ask_e6 = s.yes_ask_e6, spread_e6 = s.spread_e6, vwap_e6 = s.vwap_e6, sma20_e6 = s.sma20_e6, sma50_e6 = s.sma50_e6,
                         volatility_e6 = s.volatility_e6, is_partial = s.is_partial, changed_at = @changedAt
            FROM dbo.market_metrics t JOIN #stage s ON s.market_id = t.market_id
            WHERE EXISTS (SELECT s.last_time, s.last_e6, s.change_24h_e6, s.volume_e2, s.trade_count, s.open_interest_e2, s.yes_bid_e6, s.yes_ask_e6, s.spread_e6, s.vwap_e6, s.sma20_e6, s.sma50_e6, s.volatility_e6, s.is_partial
                          EXCEPT
                          SELECT t.last_time, t.last_e6, t.change_24h_e6, t.volume_e2, t.trade_count, t.open_interest_e2, t.yes_bid_e6, t.yes_ask_e6, t.spread_e6, t.vwap_e6, t.sma20_e6, t.sma50_e6, t.volatility_e6, t.is_partial);
            DECLARE @updated int = @@ROWCOUNT;
            INSERT dbo.market_metrics (changed_at, {Columns})
            SELECT @changedAt, {Columns} FROM #stage s WHERE NOT EXISTS (SELECT 1 FROM dbo.market_metrics t WHERE t.market_id = s.market_id);
            SELECT @updated + @@ROWCOUNT;
            """,
            new { changedAt },
            commandTimeout: 120,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteOthersAsync(IReadOnlyCollection<int> keep, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keep);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("CREATE TABLE #keep (market_id int NOT NULL PRIMARY KEY)", commandTimeout: 30, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (keep.Count > 0)
        {
            using var table = new DataTable();
            table.Columns.Add("market_id", typeof(int));
            foreach (var id in keep)
            {
                table.Rows.Add(id);
            }

            using var copy = new SqlBulkCopy((SqlConnection)connection) { DestinationTableName = "#keep", BulkCopyTimeout = 120 };
            copy.ColumnMappings.Add("market_id", "market_id");
            await copy.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
        }

        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE t FROM dbo.market_metrics t WHERE NOT EXISTS (SELECT 1 FROM #keep k WHERE k.market_id = t.market_id)",
            commandTimeout: 120,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static DataTable ToTable(IReadOnlyList<(int MarketId, MarketMetrics Metrics)> rows)
    {
        var table = new DataTable();
        table.Columns.Add("market_id", typeof(int));
        table.Columns.Add("last_time", typeof(DateTime));
        foreach (var name in new[] { "last_e6", "change_24h_e6", "volume_e2", "trade_count", "open_interest_e2", "yes_bid_e6", "yes_ask_e6", "spread_e6", "vwap_e6", "sma20_e6", "sma50_e6", "volatility_e6" })
        {
            table.Columns.Add(name, typeof(long));
        }

        table.Columns.Add("is_partial", typeof(bool));
        foreach (var (id, m) in rows)
        {
            table.Rows.Add(id, Db(m.LastTime), Db(m.LastE6), Db(m.Change24hE6), m.VolumeE2, m.TradeCount, Db(m.OpenInterestE2), Db(m.YesBidE6), Db(m.YesAskE6), Db(m.SpreadE6), Db(m.Vwap24hE6), Db(m.Sma20E6), Db(m.Sma50E6), Db(m.Volatility24hE6), m.Partial);
        }

        return table;
    }

    private static object Db<T>(T? value)
        where T : struct => value is { } present ? present : DBNull.Value;
}
