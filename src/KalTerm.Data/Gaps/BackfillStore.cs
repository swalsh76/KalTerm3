using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Gaps;

/// <summary>The values of <c>ingest_gap.backfill_status</c>.</summary>
public static class BackfillStatus
{
    /// <summary>Not tried yet, or waiting to be retried.</summary>
    public const string Pending = "pending";

    /// <summary>An attempt is under way (or was when the process died).</summary>
    public const string Running = "running";

    /// <summary>The window was recovered.</summary>
    public const string Done = "done";

    /// <summary>Every attempt failed.</summary>
    public const string Failed = "failed";

    /// <summary>There is nothing to recover by a market-wide REST read.</summary>
    public const string Skipped = "skipped";
}

/// <summary>A gap whose trades are being recovered.</summary>
/// <param name="Id">The gap's id.</param>
/// <param name="Kind">The gap's kind.</param>
/// <param name="StartedAt">When the missing period began (UTC).</param>
/// <param name="EndedAt">When it ended (UTC).</param>
/// <param name="Attempts">How many attempts this is, counting the current one.</param>
public sealed record ClaimedGap(long Id, string Kind, DateTime StartedAt, DateTime EndedAt, int Attempts);

/// <summary>A stored trade, by the two values that identify it.</summary>
/// <param name="MarketId">The market.</param>
/// <param name="TradeId">Kalshi's trade id.</param>
public readonly record struct StoredTradeKey(int MarketId, string TradeId);

/// <summary>What the trade backfill needs from the database.</summary>
public interface IBackfillStore
{
    /// <summary>
    /// Takes the oldest closed gap that is waiting and marks it running, counting the attempt. A gap qualifies when it ended at least <paramref name="settle"/> ago (so the live rows
    /// around its end are stored), it is pending and its last attempt was longer ago than one minute per attempt already made, or it has been running for longer than
    /// <paramref name="staleRunning"/> (its process died).
    /// </summary>
    /// <param name="now">The current UTC time.</param>
    /// <param name="settle">How long a gap must have been closed.</param>
    /// <param name="staleRunning">How long a running gap is assumed to still be in progress.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The gap, or <see langword="null"/> when none is waiting.</returns>
    Task<ClaimedGap?> ClaimNextAsync(DateTime now, TimeSpan settle, TimeSpan staleRunning, CancellationToken cancellationToken = default);

    /// <summary>The trades already stored with a time in [<paramref name="from"/>, <paramref name="until"/>].</summary>
    /// <param name="from">The start (UTC).</param>
    /// <param name="until">The end (UTC).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Their keys.</returns>
    Task<HashSet<StoredTradeKey>> StoredTradesAsync(DateTime from, DateTime until, CancellationToken cancellationToken = default);

    /// <summary>Records a finished gap.</summary>
    /// <param name="gapId">The gap.</param>
    /// <param name="status"><see cref="BackfillStatus.Done"/> or <see cref="BackfillStatus.Skipped"/>.</param>
    /// <param name="found">Trades REST returned.</param>
    /// <param name="added">Trades written.</param>
    /// <param name="note">What happened, cut to 200 characters.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when it is stored.</returns>
    Task CompleteAsync(long gapId, string status, int found, int added, string note, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>Records a failed attempt: the gap goes back to pending, or to failed once <paramref name="maxAttempts"/> attempts have been made.</summary>
    /// <param name="gapId">The gap.</param>
    /// <param name="maxAttempts">How many attempts a gap gets.</param>
    /// <param name="note">Why it failed, cut to 200 characters.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when it is stored.</returns>
    Task FailAsync(long gapId, int maxAttempts, string note, DateTime now, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IBackfillStore"/> over <c>dbo.ingest_gap</c> and <c>dbo.trade</c>.</summary>
/// <param name="source">Opens connections.</param>
public sealed class BackfillStore(DataSource source) : IBackfillStore
{
    private const int NoteLength = 200;

    /// <inheritdoc/>
    public async Task<ClaimedGap?> ClaimNextAsync(DateTime now, TimeSpan settle, TimeSpan staleRunning, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<(long Id, string Kind, DateTime StartedAt, DateTime EndedAt, int Attempts)?>(new CommandDefinition(
            """
            WITH next AS (
                SELECT TOP (1) *
                FROM dbo.ingest_gap WITH (UPDLOCK, READPAST)
                WHERE ended_at IS NOT NULL AND ended_at <= @settledBefore AND market_id IS NULL
                  AND ((backfill_status = 'pending' AND (backfill_at IS NULL OR backfill_at <= DATEADD(minute, -backfill_attempts, @now)))
                    OR (backfill_status = 'running' AND backfill_at <= @staleBefore))
                ORDER BY started_at, gap_id)
            UPDATE next SET backfill_status = 'running', backfill_attempts = backfill_attempts + 1, backfill_at = @now
            OUTPUT inserted.gap_id AS Id, inserted.kind AS Kind, inserted.started_at AS StartedAt, inserted.ended_at AS EndedAt, CAST(inserted.backfill_attempts AS int) AS Attempts
            """,
            new { now, settledBefore = now - settle, staleBefore = now - staleRunning },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is { } found
            ? new ClaimedGap(found.Id, found.Kind, DateTime.SpecifyKind(found.StartedAt, DateTimeKind.Utc), DateTime.SpecifyKind(found.EndedAt, DateTimeKind.Utc), found.Attempts)
            : null;
    }

    /// <inheritdoc/>
    public async Task<HashSet<StoredTradeKey>> StoredTradesAsync(DateTime from, DateTime until, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var keys = new HashSet<StoredTradeKey>();
        await using var command = new SqlCommand("SELECT market_id, trade_id FROM dbo.trade WHERE ts >= @from AND ts <= @to", connection) { CommandTimeout = 120 };
        command.Parameters.Add(new SqlParameter("@from", SqlDbType.DateTime2) { Scale = 3, Value = from });
        command.Parameters.Add(new SqlParameter("@to", SqlDbType.DateTime2) { Scale = 3, Value = until });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(new StoredTradeKey(reader.GetInt32(0), reader.GetString(1)));
        }

        return keys;
    }

    /// <inheritdoc/>
    public async Task CompleteAsync(long gapId, string status, int found, int added, string note, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(note);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.ingest_gap SET backfill_status = @status, backfill_found = @found, backfill_added = @added, backfill_at = @now, backfill_note = @note WHERE gap_id = @gapId",
            new { gapId, status, found, added, now, note = Cut(note) },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task FailAsync(long gapId, int maxAttempts, string note, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.ingest_gap SET backfill_status = CASE WHEN backfill_attempts >= @maxAttempts THEN 'failed' ELSE 'pending' END, backfill_at = @now, backfill_note = @note WHERE gap_id = @gapId",
            new { gapId, maxAttempts, now, note = Cut(note) },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static string Cut(string text) => text.Length <= NoteLength ? text : text[..NoteLength];
}
