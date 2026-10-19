using KalTerm.Data;
using Microsoft.Extensions.Logging;

namespace KalTerm.Ingest.Hosting;

/// <summary>Gets the database ready before anything runs: it exists (created only when the settings ask for it) and its schema is current.</summary>
public static partial class DatabasePreparer
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    /// <summary>Provisions (when asked) and migrates the database.</summary>
    /// <param name="options">The host settings.</param>
    /// <param name="logger">Receives what was done.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the database is ready.</returns>
    /// <exception cref="InvalidOperationException">The database does not exist and no size was given to create it.</exception>
    public static async Task PrepareAsync(IngestHostOptions options, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        var connection = options.ToConnectionOptions();

        if (options.ProvisionMaxSizeGb is { } gigabytes)
        {
            var provisioned = await DatabaseProvisioner.EnsureAsync(connection, gigabytes * Gigabyte, cancellationToken).ConfigureAwait(false);
            var changes = string.Join("; ", provisioned.Changes);
            LogProvisioned(logger, connection.Database, provisioned.Created, changes);
        }
        else if (await DatabaseProvisioner.ReadSettingsAsync(connection, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException($"database '{connection.Database}' does not exist on '{connection.Server}'; set Ingest:ProvisionMaxSizeGb to create it");
        }

        var applied = new SchemaMigrator(logger).Migrate(connection.ConnectionString);
        LogMigrated(logger, applied.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "database {Database}: created={Created}; changes: {Changes}")]
    private static partial void LogProvisioned(ILogger logger, string database, bool created, string changes);

    [LoggerMessage(Level = LogLevel.Information, Message = "schema is current ({Applied} migration(s) applied)")]
    private static partial void LogMigrated(ILogger logger, int applied);
}
