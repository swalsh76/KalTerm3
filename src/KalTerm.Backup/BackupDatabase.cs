using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using KalTerm.Data;
using Microsoft.Data.SqlClient;

namespace KalTerm.Backup;

/// <summary>What the backup and restore services need from SQL Server.</summary>
public interface IBackupDatabase
{
    /// <summary>The name of the live database.</summary>
    string Name { get; }

    /// <summary>The folder SQL Server writes backups to by default; its own service account can write there.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The folder.</returns>
    Task<string> DefaultBackupDirectoryAsync(CancellationToken cancellationToken = default);

    /// <summary>The SQL Server version.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The version text.</returns>
    Task<string> VersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Row counts per UTC day for the time-series tables of a database.</summary>
    /// <param name="database">The database; <see langword="null"/> for the live one.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The counts.</returns>
    Task<DayCounts> CountsAsync(string? database = null, CancellationToken cancellationToken = default);

    /// <summary>Writes a backup of the live database to a file, with checksums and compression, over any file already there.</summary>
    /// <param name="kind">Full or differential.</param>
    /// <param name="path">The file SQL Server writes.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the file is written.</returns>
    Task BackupAsync(BackupKind kind, string path, CancellationToken cancellationToken = default);

    /// <summary>Reads a backup file back and checks its checksums, without restoring it.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the file is verified; it throws if it is not good.</returns>
    Task VerifyAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Whether a database exists.</summary>
    /// <param name="database">The name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>True if it exists.</returns>
    Task<bool> ExistsAsync(string database, CancellationToken cancellationToken = default);

    /// <summary>Restores a full backup, and optionally a differential on top of it, into a database that does not exist yet.</summary>
    /// <param name="database">The new database's name.</param>
    /// <param name="fullPath">The full backup.</param>
    /// <param name="differentialPath">The differential backup, if any.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the database is online.</returns>
    Task RestoreAsync(string database, string fullPath, string? differentialPath, CancellationToken cancellationToken = default);

    /// <summary>Checks a database's consistency. It throws if anything is wrong.</summary>
    /// <param name="database">The database.</param>
    /// <param name="physicalOnly">Only the physical structure and page checksums (much faster on a big database).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the check is clean.</returns>
    Task CheckAsync(string database, bool physicalOnly, CancellationToken cancellationToken = default);

    /// <summary>Drops a database. It refuses the live one.</summary>
    /// <param name="database">The database.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when it is gone.</returns>
    Task DropAsync(string database, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IBackupDatabase"/> over SQL Server.</summary>
public sealed partial class SqlBackupDatabase : IBackupDatabase
{
    private readonly DataSource _source;

    /// <summary>Creates the access.</summary>
    /// <param name="source">Opens connections to the live database.</param>
    /// <param name="database">The live database's name.</param>
    public SqlBackupDatabase(DataSource source, string database)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        Name = Quote(database) is null ? throw new ArgumentException("not a database name", nameof(database)) : database;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <summary>The database name in brackets, or <see langword="null"/> when it holds anything but letters, digits and underscores.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The quoted name.</returns>
    public static string? Quote(string? name) => name is not null && NamePattern().IsMatch(name) ? "[" + name + "]" : null;

    /// <inheritdoc/>
    public async Task<string> DefaultBackupDirectoryAsync(CancellationToken cancellationToken = default) =>
        (await ScalarAsync("SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(512))", null, cancellationToken).ConfigureAwait(false) as string)
        ?? throw new InvalidOperationException("SQL Server reports no default backup path");

    /// <inheritdoc/>
    public async Task<string> VersionAsync(CancellationToken cancellationToken = default) =>
        (await ScalarAsync("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) + ' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(100))", null, cancellationToken).ConfigureAwait(false) as string) ?? "unknown";

    /// <inheritdoc/>
    public async Task<DayCounts> CountsAsync(string? database = null, CancellationToken cancellationToken = default)
    {
        var quoted = Quote(database ?? Name) ?? throw new ArgumentException("not a database name", nameof(database));
        return new DayCounts(
            await DaysAsync($"SELECT CONVERT(char(10), CAST(ts AS date), 23), COUNT_BIG(*) FROM {quoted}.dbo.ticker GROUP BY CAST(ts AS date)", cancellationToken).ConfigureAwait(false),
            await DaysAsync($"SELECT CONVERT(char(10), CAST(ts AS date), 23), COUNT_BIG(*) FROM {quoted}.dbo.trade GROUP BY CAST(ts AS date)", cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc/>
    public async Task BackupAsync(BackupKind kind, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            await ExecuteAsync(BackupSql(Name, kind, compress: true), [new SqlParameter("@path", path)], cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == CompressionNotSupported)
        {
            // Express editions (and LocalDB) restore compressed backups but cannot make them: the backup is the same, only bigger.
            await ExecuteAsync(BackupSql(Name, kind, compress: false), [new SqlParameter("@path", path)], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>SQL Server's error for <c>BACKUP ... WITH COMPRESSION</c> on an edition that cannot compress.</summary>
    public const int CompressionNotSupported = 1844;

    /// <summary>The backup statement.</summary>
    /// <param name="database">The database name (already validated).</param>
    /// <param name="kind">Full or differential.</param>
    /// <param name="compress">Whether to compress.</param>
    /// <returns>The statement, with the file as <c>@path</c>.</returns>
    public static string BackupSql(string database, BackupKind kind, bool compress) =>
        $"BACKUP DATABASE {Quote(database)} TO DISK = @path WITH {(kind == BackupKind.Differential ? "DIFFERENTIAL, " : string.Empty)}CHECKSUM, {(compress ? "COMPRESSION" : "NO_COMPRESSION")}, INIT, FORMAT"
        ;

    /// <inheritdoc/>
    public Task VerifyAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ExecuteAsync("RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM", [new SqlParameter("@path", path)], cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string database, CancellationToken cancellationToken = default) =>
        await ScalarAsync("SELECT DB_ID(@name)", [new SqlParameter("@name", database)], cancellationToken).ConfigureAwait(false) is not (null or DBNull);

    /// <inheritdoc/>
    public async Task RestoreAsync(string database, string fullPath, string? differentialPath, CancellationToken cancellationToken = default)
    {
        var quoted = Quote(database) ?? throw new ArgumentException("not a database name", nameof(database));
        if (string.Equals(database, Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("a restore never overwrites the live database; give a new name");
        }

        if (await ExistsAsync(database, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"database {database} already exists; a restore only creates a new one");
        }

        var dataDir = (await ScalarAsync("SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512))", null, cancellationToken).ConfigureAwait(false) as string) ?? throw new InvalidOperationException("SQL Server reports no default data path");
        var logDir = (await ScalarAsync("SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(512))", null, cancellationToken).ConfigureAwait(false) as string) ?? dataDir;
        var moves = new List<string>();
        await using (var connection = await _source.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var list = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @path", connection) { CommandTimeout = 300 })
        {
            list.Parameters.AddWithValue("@path", fullPath);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var number = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var logical = reader.GetString(reader.GetOrdinal("LogicalName"));
                var isLog = reader.GetString(reader.GetOrdinal("Type")) == "L";
                var file = string.Create(CultureInfo.InvariantCulture, $"{database}_{number++}{(isLog ? ".ldf" : ".mdf")}");
                var physical = System.IO.Path.Combine(isLog ? logDir : dataDir, file).Replace("'", "''", StringComparison.Ordinal);
                moves.Add($"MOVE N'{logical.Replace("'", "''", StringComparison.Ordinal)}' TO N'{physical}'");
            }
        }

        var move = string.Join(", ", moves);
        var recovery = differentialPath is null ? "RECOVERY" : "NORECOVERY";
        await ExecuteAsync($"RESTORE DATABASE {quoted} FROM DISK = @path WITH {move}, {recovery}, CHECKSUM", [new SqlParameter("@path", fullPath)], cancellationToken).ConfigureAwait(false);
        if (differentialPath is not null)
        {
            await ExecuteAsync($"RESTORE DATABASE {quoted} FROM DISK = @path WITH RECOVERY, CHECKSUM", [new SqlParameter("@path", differentialPath)], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task CheckAsync(string database, bool physicalOnly, CancellationToken cancellationToken = default)
    {
        var quoted = Quote(database) ?? throw new ArgumentException("not a database name", nameof(database));
        await using var connection = await _source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand($"DBCC CHECKDB ({quoted}) WITH NO_INFOMSGS, ALL_ERRORMSGS, TABLERESULTS{(physicalOnly ? ", PHYSICAL_ONLY" : string.Empty)}", connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var errors = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            errors.Add(Convert.ToString(reader["MessageText"], CultureInfo.InvariantCulture) ?? "an unnamed consistency error");
            if (errors.Count == 5)
            {
                break;
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException($"DBCC CHECKDB found errors in {database}: {string.Join(" | ", errors)}");
        }
    }

    /// <inheritdoc/>
    public async Task DropAsync(string database, CancellationToken cancellationToken = default)
    {
        var quoted = Quote(database) ?? throw new ArgumentException("not a database name", nameof(database));
        if (string.Equals(database, Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("refusing to drop the live database");
        }

        if (await ExistsAsync(database, cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync($"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted}", [], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyDictionary<string, long>> DaysAsync(string sql, CancellationToken cancellationToken)
    {
        var days = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = await _source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            days[reader.GetString(0)] = reader.GetInt64(1);
        }

        return days;
    }

    private async Task<object?> ScalarAsync(string sql, SqlParameter[]? parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        if (parameters is not null)
        {
            command.Parameters.AddRange(parameters);
        }

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql, SqlParameter[] parameters, CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
