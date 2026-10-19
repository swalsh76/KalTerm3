using Microsoft.Data.SqlClient;

namespace KalTerm.Data.State;

/// <summary>The names used in <c>ingest_state</c>.</summary>
public static class IngestStateKeys
{
    /// <summary>
    /// The ingest process's state. The value is <see cref="Running"/> or <see cref="Stopped"/>; the time is the last heartbeat while running and the
    /// moment of a clean stop otherwise, so the time between that and the next start is known downtime.
    /// </summary>
    public const string Run = "run";

    /// <summary>The value of <see cref="Run"/> while the process is up (refreshed by the heartbeat).</summary>
    public const string Running = "running";

    /// <summary>The value of <see cref="Run"/> after a clean shutdown.</summary>
    public const string Stopped = "stopped";
}

/// <summary>A stored state value and when it was last written (UTC).</summary>
/// <param name="Value">The value.</param>
/// <param name="UpdatedAt">When it was written.</param>
public sealed record IngestStateEntry(string Value, DateTime UpdatedAt);

/// <summary>Small named values the ingest process keeps in the database.</summary>
public interface IIngestState
{
    /// <summary>Reads one value.</summary>
    /// <param name="name">The name, 1 to 64 characters.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The entry, or <see langword="null"/> if it was never written.</returns>
    Task<IngestStateEntry?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Writes one value, replacing any earlier one.</summary>
    /// <param name="name">The name, 1 to 64 characters.</param>
    /// <param name="value">The value, at most 400 characters (a longer one is refused, not cut).</param>
    /// <param name="at">The time to record (UTC).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when it is stored.</returns>
    Task SetAsync(string name, string value, DateTime at, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IIngestState"/> over <c>dbo.ingest_state</c>.</summary>
/// <param name="source">Opens connections.</param>
public sealed class IngestStateStore(DataSource source) : IIngestState
{
    private const int NameLength = 64;
    private const int ValueLength = 400;

    /// <inheritdoc/>
    public async Task<IngestStateEntry?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        CheckName(name);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand("SELECT value, updated_at FROM dbo.ingest_state WHERE name = @name", connection);
        command.Parameters.Add("@name", System.Data.SqlDbType.VarChar, NameLength).Value = name;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new IngestStateEntry(reader.GetString(0), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc))
            : null;
    }

    /// <inheritdoc/>
    public async Task SetAsync(string name, string value, DateTime at, CancellationToken cancellationToken = default)
    {
        CheckName(name);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > ValueLength)
        {
            // A parameter would cut it silently; a state value that is not what was written is worse than a refusal.
            throw new ArgumentException($"a state value can hold at most {ValueLength} characters", nameof(value));
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            """
            MERGE dbo.ingest_state WITH (HOLDLOCK) AS t
            USING (VALUES (@name, @value, @at)) AS s (name, value, updated_at) ON t.name = s.name
            WHEN MATCHED THEN UPDATE SET value = s.value, updated_at = s.updated_at
            WHEN NOT MATCHED THEN INSERT (name, value, updated_at) VALUES (s.name, s.value, s.updated_at);
            """,
            connection);
        command.Parameters.Add("@name", System.Data.SqlDbType.VarChar, NameLength).Value = name;
        command.Parameters.Add("@value", System.Data.SqlDbType.NVarChar, ValueLength).Value = value;
        var when = command.Parameters.Add("@at", System.Data.SqlDbType.DateTime2);
        when.Scale = 3;
        when.Value = at;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > NameLength)
        {
            throw new ArgumentException($"a state name must be 1 to {NameLength} characters", nameof(name));
        }
    }
}
