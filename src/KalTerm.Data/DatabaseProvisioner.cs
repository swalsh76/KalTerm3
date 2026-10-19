using System.Globalization;
using Microsoft.Data.SqlClient;

namespace KalTerm.Data;

/// <summary>What <see cref="DatabaseProvisioner"/> did.</summary>
/// <param name="Created">Whether the database was created by this call.</param>
/// <param name="Changes">Settings that were wrong and were corrected, as text.</param>
public sealed record ProvisionResult(bool Created, IReadOnlyList<string> Changes);

/// <summary>The durability and storage settings of the KalTerm database.</summary>
/// <param name="DelayedDurability">Should be <c>DISABLED</c>.</param>
/// <param name="PageVerify">Should be <c>CHECKSUM</c>.</param>
/// <param name="Recovery">The recovery model; <c>SIMPLE</c> until backups are designed (M8).</param>
/// <param name="DataFileName">The logical name of the data file.</param>
/// <param name="MaxSizeBytes">The data file's hard maximum in bytes, or -1 for unlimited.</param>
public sealed record DatabaseSettings(string DelayedDurability, string PageVerify, string Recovery, string DataFileName, long MaxSizeBytes);

/// <summary>Creates the KalTerm database and pins the settings the durability and storage rules depend on.</summary>
public static class DatabaseProvisioner
{
    private const long Megabyte = 1024 * 1024;

    /// <summary>Creates the database if missing, then sets or corrects durability and storage settings. Safe to run again.</summary>
    /// <param name="options">The connection options (server and database name).</param>
    /// <param name="maxSizeBytes">The hard maximum size of the data file, rounded up to a whole megabyte. Required: there is no default.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the database was created, and any settings that were corrected.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSizeBytes"/> is not positive.</exception>
    public static async Task<ProvisionResult> EnsureAsync(DatabaseConnectionOptions options, long maxSizeBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSizeBytes);

        var changes = new List<string>();
        await using var connection = new SqlConnection(options.MasterConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var db = Quote(options.Database);
        var created = !await ExistsAsync(connection, options.Database, cancellationToken).ConfigureAwait(false);
        if (created)
        {
            await ExecAsync(connection, "CREATE DATABASE " + db, cancellationToken).ConfigureAwait(false);
        }

        var state = await WaitUntilReadableAsync(connection, options.Database, cancellationToken).ConfigureAwait(false);

        if (state.DelayedDurability != "DISABLED")
        {
            await ExecAsync(connection, $"ALTER DATABASE {db} SET DELAYED_DURABILITY = DISABLED", cancellationToken).ConfigureAwait(false);
            changes.Add($"DELAYED_DURABILITY was {state.DelayedDurability}");
        }

        if (state.PageVerify != "CHECKSUM")
        {
            await ExecAsync(connection, $"ALTER DATABASE {db} SET PAGE_VERIFY CHECKSUM", cancellationToken).ConfigureAwait(false);
            changes.Add($"PAGE_VERIFY was {state.PageVerify}");
        }

        if (state.Recovery != "SIMPLE")
        {
            await ExecAsync(connection, $"ALTER DATABASE {db} SET RECOVERY SIMPLE", cancellationToken).ConfigureAwait(false);
            changes.Add($"RECOVERY was {state.Recovery}");
        }

        var wantedMb = (maxSizeBytes + Megabyte - 1) / Megabyte;
        if (state.MaxSizeBytes != wantedMb * Megabyte)
        {
            var modify = string.Format(
                CultureInfo.InvariantCulture,
                "ALTER DATABASE {0} MODIFY FILE (NAME = {1}, MAXSIZE = {2}MB)",
                db,
                Quote(state.DataFileName),
                wantedMb);
            await ExecAsync(connection, modify, cancellationToken).ConfigureAwait(false);
            changes.Add("data file MAXSIZE was " + (state.MaxSizeBytes < 0 ? "unlimited" : state.MaxSizeBytes.ToString(CultureInfo.InvariantCulture) + " bytes"));
        }

        return new ProvisionResult(created, changes);
    }

    /// <summary>Reads the settings of an existing database without changing anything.</summary>
    /// <param name="options">The connection options.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The settings, or <see langword="null"/> when the database does not exist or is not online.</returns>
    public static async Task<DatabaseSettings?> ReadSettingsAsync(DatabaseConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await using var connection = new SqlConnection(options.MasterConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, options.Database, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A database that was just created (or is still recovering) can be listed before its settings are readable; wait, bounded.</summary>
    private static async Task<DatabaseSettings> WaitUntilReadableAsync(SqlConnection connection, string database, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var settings = await ReadAsync(connection, database, cancellationToken).ConfigureAwait(false);
            if (settings is not null)
            {
                return settings;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"Database '{database}' did not come online within 60 seconds.");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ExistsAsync(SqlConnection connection, string database, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @n", connection);
        command.Parameters.AddWithValue("@n", database);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0;
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static async Task ExecAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DatabaseSettings?> ReadAsync(SqlConnection connection, string database, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.state_desc, d.delayed_durability_desc, d.page_verify_option_desc, d.recovery_model_desc, f.name,
                   CASE WHEN f.max_size = -1 THEN CAST(-1 AS bigint) ELSE CAST(f.max_size AS bigint) * 8192 END
            FROM sys.databases d
            JOIN sys.master_files f ON f.database_id = d.database_id AND f.type = 0
            WHERE d.name = @n
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@n", database);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // Not ready yet: offline or recovering, or the settings are not populated until it finishes coming up.
        for (var i = 0; i < 6; i++)
        {
            if (reader.IsDBNull(i))
            {
                return null;
            }
        }

        return reader.GetString(0) == "ONLINE"
            ? new DatabaseSettings(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5))
            : null;
    }
}
