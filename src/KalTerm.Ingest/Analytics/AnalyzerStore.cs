using System.Data.Common;
using System.Globalization;
using Dapper;
using KalTerm.Data;
using KalTerm.Data.State;

namespace KalTerm.Ingest.Analytics;

/// <summary>
/// <see cref="IAnalyzerStore"/> over <c>dbo.ingest_state</c> (last start and change counter, keys <c>analyzer.NAME.last</c> and <c>analyzer.NAME.version</c>) and <c>dbo.analyzer_run</c> (the history).
/// The lock is a session-scoped application lock (<c>sp_getapplock</c>) held on a connection of its own for as long as the analyzer runs; if the process dies the connection closes and the lock goes with it.
/// </summary>
/// <param name="source">Opens connections.</param>
public sealed class AnalyzerStore(DataSource source) : IAnalyzerStore
{
    private static string LastKey(string name) => "analyzer." + name + ".last";

    private static string VersionKey(string name) => "analyzer." + name + ".version";

    /// <inheritdoc/>
    public async Task<DateTime?> GetLastStartAsync(string name, CancellationToken cancellationToken = default)
    {
        var entry = await new IngestStateStore(source).GetAsync(LastKey(name), cancellationToken).ConfigureAwait(false);
        return entry is not null && DateTime.TryParseExact(entry.Value, "yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
    }

    /// <inheritdoc/>
    public Task SetLastStartAsync(string name, DateTime at, CancellationToken cancellationToken = default) =>
        new IngestStateStore(source).SetAsync(LastKey(name), at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture), at, cancellationToken);

    /// <inheritdoc/>
    public async Task<IAnalyzerLease?> TryLockAsync(string name, CancellationToken cancellationToken = default)
    {
        var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var code = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r",
                new { resource = "kalterm.analyzer." + name },
                commandTimeout: 30,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (code >= 0)
            {
                return new Lease(connection, name);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    /// <inheritdoc/>
    public async Task<long> BeginRunAsync(string name, DateTime startedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT dbo.analyzer_run (name, started_at, status) OUTPUT INSERTED.run_id VALUES (@name, @startedAt, 'running')",
            new { name = new DbString { Value = name, IsAnsi = true, Length = 64 }, startedAt },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task FinishRunAsync(long runId, DateTime finishedAt, string status, int? rows, bool? changed, string? message, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.analyzer_run SET finished_at = @finishedAt, status = @status, row_count = @rows, changed = @changed, message = @message WHERE run_id = @runId",
            new { runId, finishedAt, status = new DbString { Value = status, IsAnsi = true, Length = 16 }, rows, changed, message },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task BumpVersionAsync(string name, DateTime at, CancellationToken cancellationToken = default)
    {
        var state = new IngestStateStore(source);
        var current = await state.GetAsync(VersionKey(name), cancellationToken).ConfigureAwait(false);
        var next = (current is not null && long.TryParse(current.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0) + 1;
        await state.SetAsync(VersionKey(name), next.ToString(CultureInfo.InvariantCulture), at, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> PruneRunsAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition("DELETE dbo.analyzer_run WHERE started_at < @olderThan", new { olderThan }, commandTimeout: 60, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed class Lease(DbConnection connection, string name) : IAnalyzerLease
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'",
                    new { resource = "kalterm.analyzer." + name },
                    commandTimeout: 30)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // The connection is closing anyway, which releases the lock.
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
