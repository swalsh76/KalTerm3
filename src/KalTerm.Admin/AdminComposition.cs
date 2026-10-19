using KalTerm.Backup;
using KalTerm.Data;
using KalTerm.Data.Auth;
using KalTerm.Data.State;

namespace KalTerm.Admin;

/// <summary>Builds the admin services from the same settings the ingest host reads (<c>KALTERM_Ingest__*</c>).</summary>
internal static class AdminComposition
{
    /// <summary>Builds the user, token and certificate services; nothing connects to SQL Server until a command runs.</summary>
    /// <param name="get">Reads a setting by name (the <c>Ingest</c> section for the database, <c>Api</c> for the TLS folder); <see langword="null"/> when it is not set.</param>
    /// <returns>The services.</returns>
    public static AccessServices CreateAccess(Func<string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        var connection = new DatabaseConnectionOptions
        {
            Server = get("Server") ?? DatabaseConnectionOptions.SharedMemoryServer,
            Database = get("Database") ?? DatabaseConnectionOptions.DefaultDatabase,
            ApplicationName = "KalTerm.Admin",
        };
        return new AccessServices(new UserStore(new DataSource(connection)), get("TlsDirectory") ?? @"C:\KalTerm\tls", TimeProvider.System);
    }

    /// <summary>Builds the services.</summary>
    /// <param name="get">Reads a setting of the <c>Ingest</c> section by name; <see langword="null"/> when it is not set.</param>
    /// <returns>The services. Nothing connects to SQL Server until a command runs.</returns>
    /// <exception cref="InvalidOperationException">No backup target is configured, or a setting cannot work.</exception>
    public static AdminServices Create(Func<string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        var options = BackupSettings.Create(
            get("BackupTarget"),
            get("BackupStaging"),
            int.TryParse(get("BackupDaily"), out var daily) ? daily : 7,
            int.TryParse(get("BackupWeekly"), out var weekly) ? weekly : 4,
            int.TryParse(get("BackupMonthly"), out var monthly) ? monthly : 12,
            get("BackupAt")) ?? throw new InvalidOperationException("no backup target is configured: set KALTERM_Ingest__BackupTarget (a folder or \\\\host\\share\\folder)");
        var connection = new DatabaseConnectionOptions
        {
            Server = get("Server") ?? DatabaseConnectionOptions.SharedMemoryServer,
            Database = get("Database") ?? DatabaseConnectionOptions.DefaultDatabase,
            ApplicationName = "KalTerm.Admin",
        };
        var source = new DataSource(connection);
        var database = new SqlBackupDatabase(source, connection.Database);
        var state = new IngestStateStore(source);
        var credentials = new ShareCredentialStore(get("BackupCredentialPath") ?? BackupSettings.DefaultCredentialPath);
        var connector = new Win32ShareConnector();
        return new AdminServices(
            new BackupService(database, state, options, connector, credentials.Load),
            new RestoreService(database, state, options, connector, credentials.Load),
            new BackupTarget(options.TargetPath),
            credentials,
            connector);
    }
}
