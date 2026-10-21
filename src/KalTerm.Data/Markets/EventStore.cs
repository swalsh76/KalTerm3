using System.Data;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Markets;

/// <summary>
/// What is known about one event, as stored in <c>dbo.market_event</c>. A <see cref="Known"/> of <see langword="false"/> is the memory that Kalshi had no such event when it was asked.
/// This is a plain record so that <c>KalTerm.Data</c> knows nothing about Kalshi's models.
/// </summary>
/// <param name="EventTicker">The event ticker the markets carry.</param>
/// <param name="SeriesTicker">The series it belongs to.</param>
/// <param name="Title">The event title, for example <c>Philadelphia vs New York</c>.</param>
/// <param name="SubTitle">The event sub-title, for example <c>PHI vs NYK (Oct 20)</c>.</param>
/// <param name="Known">Whether Kalshi returned the event.</param>
public sealed record EventRecord(string EventTicker, string SeriesTicker, string Title, string SubTitle, bool Known = true);

/// <summary>The event titles' storage.</summary>
public interface IEventStore
{
    /// <summary>
    /// Inserts new events and updates existing ones (matched on the event ticker) in one transaction. An event Kalshi does not know (<see cref="EventRecord.Known"/> false) never replaces one that
    /// is known; it only records that the question was asked.
    /// </summary>
    /// <param name="events">The events. A ticker repeated within the batch counts once, the last record winning.</param>
    /// <param name="fetchedAtUtc">When they were read from Kalshi.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the batch is stored.</returns>
    Task UpsertAsync(IReadOnlyList<EventRecord> events, DateTime fetchedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the title of stored markets (matched on ticker). A ticker that is not stored is ignored (the catalog is filled by discovery, not from here), and an empty title never replaces one that is there.
    /// </summary>
    /// <param name="titles">The tickers and their titles; a ticker repeated within the batch counts once, the last winning.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many markets changed.</returns>
    Task<int> SetMarketTitlesAsync(IReadOnlyList<(string Ticker, string Title)> titles, CancellationToken cancellationToken = default);

    /// <summary>The event tickers some stored market mentions that have no event stored (never asked first, the most recently seen first), or whose event Kalshi did not know when it was last asked before <paramref name="askedBeforeUtc"/>.</summary>
    /// <param name="limit">The most to return.</param>
    /// <param name="askedBeforeUtc">An unknown event asked about at or after this moment is left alone.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The event tickers: never-asked ones first, and among them the events whose markets were seen most recently (what a person is looking at now).</returns>
    Task<IReadOnlyList<string>> MissingAsync(int limit, DateTime askedBeforeUtc, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IEventStore"/> over <c>dbo.market_event</c>: one round trip per batch (bulk copy into a temporary table, then a single <c>MERGE</c>).</summary>
/// <param name="source">Opens connections.</param>
public sealed class EventStore(DataSource source) : IEventStore
{
    private const int TitleLength = 512;

    private const string CreateStage = """
        CREATE TABLE #event_stage (
            event_ticker varchar(128) NOT NULL PRIMARY KEY,
            series_ticker varchar(128) NOT NULL,
            title nvarchar(512) NOT NULL,
            sub_title nvarchar(512) NOT NULL,
            known bit NOT NULL)
        """;

    private const string Merge = """
        MERGE dbo.market_event WITH (HOLDLOCK) AS t
        USING #event_stage AS s ON t.event_ticker = s.event_ticker
        WHEN MATCHED THEN UPDATE SET
            t.series_ticker = CASE WHEN s.known = 1 OR t.known = 0 THEN s.series_ticker ELSE t.series_ticker END,
            t.title = CASE WHEN s.known = 1 OR t.known = 0 THEN s.title ELSE t.title END,
            t.sub_title = CASE WHEN s.known = 1 OR t.known = 0 THEN s.sub_title ELSE t.sub_title END,
            t.known = CASE WHEN s.known = 1 OR t.known = 0 THEN s.known ELSE t.known END,
            t.fetched_at = @fetched
        WHEN NOT MATCHED THEN INSERT (event_ticker, series_ticker, title, sub_title, known, fetched_at)
            VALUES (s.event_ticker, s.series_ticker, s.title, s.sub_title, s.known, @fetched);
        """;

    /// <inheritdoc/>
    public async Task UpsertAsync(IReadOnlyList<EventRecord> events, DateTime fetchedAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        var stage = BuildStage(events);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var create = new SqlCommand(CreateStage, connection, transaction))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "#event_stage", BulkCopyTimeout = 120 })
        {
            foreach (DataColumn column in stage.Columns)
            {
                copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await copy.WriteToServerAsync(stage, cancellationToken).ConfigureAwait(false);
        }

        await using (var merge = new SqlCommand(Merge, connection, transaction) { CommandTimeout = 120 })
        {
            merge.Parameters.Add("@fetched", SqlDbType.DateTime2, 0).Value = fetchedAtUtc;
            merge.Parameters["@fetched"].Scale = 3;
            await merge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> MissingAsync(int limit, DateTime askedBeforeUtc, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        const string sql = """
            SELECT TOP (@limit) m.event_ticker
            FROM (SELECT event_ticker, MAX(last_seen_at) AS seen FROM dbo.market GROUP BY event_ticker) m
            LEFT JOIN dbo.market_event e ON e.event_ticker = m.event_ticker
            WHERE e.event_ticker IS NULL OR (e.known = 0 AND e.fetched_at < @before)
            ORDER BY CASE WHEN e.event_ticker IS NULL THEN 0 ELSE 1 END, m.seen DESC, m.event_ticker
            """;
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@before", SqlDbType.DateTime2, 0).Value = askedBeforeUtc;
        command.Parameters["@before"].Scale = 3;
        var tickers = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
    }

    /// <inheritdoc/>
    public async Task<int> SetMarketTitlesAsync(IReadOnlyList<(string Ticker, string Title)> titles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var last = new Dictionary<string, string>(titles.Count, StringComparer.Ordinal);
        foreach (var (ticker, title) in titles)
        {
            if (title.Length > 0)
            {
                last[ticker] = Truncate(title);
            }
        }

        if (last.Count == 0)
        {
            return 0;
        }

        var stage = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        stage.Columns.Add("ticker", typeof(string));
        stage.Columns.Add("title", typeof(string));
        foreach (var (ticker, title) in last)
        {
            stage.Rows.Add(ticker, title);
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var create = new SqlCommand("CREATE TABLE #title_stage (ticker varchar(128) NOT NULL PRIMARY KEY, title nvarchar(512) NOT NULL)", connection, transaction))
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "#title_stage", BulkCopyTimeout = 120 })
        {
            copy.ColumnMappings.Add("ticker", "ticker");
            copy.ColumnMappings.Add("title", "title");
            await copy.WriteToServerAsync(stage, cancellationToken).ConfigureAwait(false);
        }

        int changed;
        await using (var update = new SqlCommand("UPDATE m SET m.title = s.title FROM dbo.market m JOIN #title_stage s ON s.ticker = m.ticker WHERE m.title <> s.title", connection, transaction) { CommandTimeout = 120 })
        {
            changed = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private static DataTable BuildStage(IReadOnlyList<EventRecord> events)
    {
        var table = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        table.Columns.Add("event_ticker", typeof(string));
        table.Columns.Add("series_ticker", typeof(string));
        table.Columns.Add("title", typeof(string));
        table.Columns.Add("sub_title", typeof(string));
        table.Columns.Add("known", typeof(bool));

        // The same event can come twice in one batch (a page boundary that shifted); the MERGE would refuse it, so the last record wins.
        var last = new Dictionary<string, EventRecord>(events.Count, StringComparer.Ordinal);
        foreach (var record in events)
        {
            last[record.EventTicker] = record;
        }

        foreach (var e in last.Values)
        {
            table.Rows.Add(e.EventTicker, e.SeriesTicker, Truncate(e.Title), Truncate(e.SubTitle), e.Known);
        }

        return table;
    }

    private static string Truncate(string text) => text.Length <= TitleLength ? text : text[..TitleLength];
}
