using System.Data;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Markets;

/// <summary>
/// <see cref="IMarketStore"/> over <c>dbo.market</c>: one round trip per batch (bulk copy into a temporary table, then a single
/// <c>MERGE</c>), so a 1,000-market page costs the same as a handful.
/// </summary>
/// <param name="source">Opens connections.</param>
public sealed class MarketStore(DataSource source) : IMarketStore
{
    /// <summary>The status given to a tradable market that dropped out of the open listing.</summary>
    public const string UnlistedStatus = "unlisted";

    /// <summary>Statuses Kalshi uses for a market that can trade; only these are ever marked unlisted.</summary>
    public static IReadOnlyList<string> TradableStatuses { get; } = ["active", "open"];

    private const int SubTitleLength = 256;

    private const int TitleLength = 512;

    private const string CreateStage = """
        CREATE TABLE #market_stage (
            ticker varchar(128) NOT NULL PRIMARY KEY,
            event_ticker varchar(128) NOT NULL,
            market_type varchar(32) NOT NULL,
            status varchar(32) NOT NULL,
            yes_sub_title nvarchar(256) NOT NULL,
            no_sub_title nvarchar(256) NOT NULL,
            open_time datetime2(3) NULL,
            close_time datetime2(3) NULL,
            latest_expiration_time datetime2(3) NULL,
            settlement_ts datetime2(3) NULL,
            result varchar(16) NOT NULL,
            settlement_value_e6 bigint NULL,
            title nvarchar(512) NOT NULL)
        """;

    private const string Merge = """
        MERGE dbo.market WITH (HOLDLOCK) AS t
        USING #market_stage AS s ON t.ticker = s.ticker
        WHEN MATCHED THEN UPDATE SET
            t.event_ticker = s.event_ticker, t.market_type = s.market_type, t.status = s.status,
            t.yes_sub_title = s.yes_sub_title, t.no_sub_title = s.no_sub_title,
            t.open_time = s.open_time, t.close_time = s.close_time, t.latest_expiration_time = s.latest_expiration_time,
            t.settlement_ts = s.settlement_ts, t.result = s.result, t.settlement_value_e6 = s.settlement_value_e6, t.title = s.title,
            t.last_seen_at = @seen
        WHEN NOT MATCHED THEN INSERT
            (ticker, event_ticker, market_type, status, yes_sub_title, no_sub_title, open_time, close_time, latest_expiration_time,
             settlement_ts, result, settlement_value_e6, title, first_seen_at, last_seen_at)
            VALUES (s.ticker, s.event_ticker, s.market_type, s.status, s.yes_sub_title, s.no_sub_title, s.open_time, s.close_time,
                    s.latest_expiration_time, s.settlement_ts, s.result, s.settlement_value_e6, s.title, @seen, @seen)
        OUTPUT $action, inserted.market_id, inserted.ticker;
        """;

    /// <inheritdoc/>
    public async Task<MarketUpsertResult> UpsertAsync(IReadOnlyList<MarketRecord> records, DateTime seenAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return MarketUpsertResult.Empty;
        }

        var stage = BuildStage(records);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var create = new SqlCommand(CreateStage, connection, transaction))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "#market_stage", BulkCopyTimeout = 120 })
        {
            foreach (DataColumn column in stage.Columns)
            {
                copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await copy.WriteToServerAsync(stage, cancellationToken).ConfigureAwait(false);
        }

        var ids = new Dictionary<string, int>(stage.Rows.Count, StringComparer.Ordinal);
        var inserted = new List<string>();
        var matched = 0;
        await using (var merge = new SqlCommand(Merge, connection, transaction) { CommandTimeout = 120 })
        {
            merge.Parameters.Add("@seen", SqlDbType.DateTime2, 0).Value = seenAtUtc;
            merge.Parameters["@seen"].Scale = 3;
            await using var reader = await merge.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ticker = reader.GetString(2);
                ids[ticker] = reader.GetInt32(1);
                if (reader.GetString(0) == "INSERT")
                {
                    inserted.Add(ticker);
                }
                else
                {
                    matched++;
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MarketUpsertResult(ids, inserted, matched);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> MarkUnlistedAsync(DateTime passStartedUtc, CancellationToken cancellationToken = default)
    {
        var tradable = string.Join(", ", TradableStatuses.Select(s => "'" + s + "'"));
        var sql = $"UPDATE dbo.market SET status = '{UnlistedStatus}' OUTPUT inserted.ticker WHERE status IN ({tradable}) AND last_seen_at < @pass";
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.Add("@pass", SqlDbType.DateTime2, 0).Value = passStartedUtc;
        command.Parameters["@pass"].Scale = 3;
        var tickers = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, int>> LoadIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand("SELECT ticker, market_id FROM dbo.market", connection) { CommandTimeout = 120 };
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids[reader.GetString(0)] = reader.GetInt32(1);
        }

        return ids;
    }

    private static DataTable BuildStage(IReadOnlyList<MarketRecord> records)
    {
        var table = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        table.Columns.Add("ticker", typeof(string));
        table.Columns.Add("event_ticker", typeof(string));
        table.Columns.Add("market_type", typeof(string));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("yes_sub_title", typeof(string));
        table.Columns.Add("no_sub_title", typeof(string));
        table.Columns.Add("open_time", typeof(DateTime));
        table.Columns.Add("close_time", typeof(DateTime));
        table.Columns.Add("latest_expiration_time", typeof(DateTime));
        table.Columns.Add("settlement_ts", typeof(DateTime));
        table.Columns.Add("result", typeof(string));
        table.Columns.Add("settlement_value_e6", typeof(long));
        table.Columns.Add("title", typeof(string));

        // A ticker may appear twice in one batch (a page boundary that shifted); the MERGE would refuse it, so the last record wins.
        var last = new Dictionary<string, MarketRecord>(records.Count, StringComparer.Ordinal);
        foreach (var record in records)
        {
            last[record.Ticker] = record;
        }

        foreach (var r in last.Values)
        {
            table.Rows.Add(
                r.Ticker,
                r.EventTicker,
                r.MarketType,
                r.Status,
                Truncate(r.YesSubTitle),
                Truncate(r.NoSubTitle),
                (object?)r.OpenTime ?? DBNull.Value,
                (object?)r.CloseTime ?? DBNull.Value,
                (object?)r.LatestExpirationTime ?? DBNull.Value,
                (object?)r.SettlementTs ?? DBNull.Value,
                r.Result,
                (object?)r.SettlementValueE6 ?? DBNull.Value,
                r.Title.Length <= TitleLength ? r.Title : r.Title[..TitleLength]);
        }

        return table;
    }

    private static string Truncate(string text) => text.Length <= SubTitleLength ? text : text[..SubTitleLength];
}
