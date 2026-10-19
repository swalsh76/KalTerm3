using System.Globalization;
using KalTerm.Data.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Backup;

/// <summary>What a restore found.</summary>
/// <param name="Database">The database that was created (and dropped again, unless it was kept).</param>
/// <param name="Backups">The backup files used: the full one, then the differential if there was one.</param>
/// <param name="Kept">Whether the restored database was left in place.</param>
/// <param name="Comparison">How the restored row counts compare with the manifest.</param>
/// <param name="Elapsed">How long it took.</param>
public sealed record RestoreResult(string Database, IReadOnlyList<string> Backups, bool Kept, CountComparison Comparison, TimeSpan Elapsed);

/// <summary>Restores backups into new databases and proves them.</summary>
public interface IRestoreService
{
    /// <summary>
    /// Restores a backup (and the full backup it builds on) into a <b>new</b> database, checks the copy with <c>DBCC CHECKDB</c> and compares its row counts with the manifest.
    /// It never touches the live database and refuses an existing name.
    /// </summary>
    /// <param name="database">The new database's name.</param>
    /// <param name="backupName">The backup to restore; <see langword="null"/> for the newest.</param>
    /// <param name="keep">Leave the restored database in place (to switch to it by hand); otherwise it is dropped.</param>
    /// <param name="physicalOnly">Check only the physical structure and page checksums.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What it found.</returns>
    Task<RestoreResult> RestoreAsync(string database, string? backupName, bool keep, bool physicalOnly, CancellationToken cancellationToken = default);

    /// <summary>A restore into a scratch database that is dropped afterwards; records the result for <c>status</c>. Never throws except when cancelled.</summary>
    /// <param name="physicalOnly">Check only the physical structure and page checksums.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the newest backup restores cleanly and agrees with its manifest.</returns>
    Task<bool> VerifyAsync(bool physicalOnly, CancellationToken cancellationToken = default);
}

/// <summary>
/// A backup is proven only when it has been restored into a new database and that copy agrees with the manifest. A backup on a network share is copied to the staging folder first
/// (SQL Server cannot read the share), and the copy is hashed against the manifest.
/// </summary>
public sealed partial class RestoreService : IRestoreService
{
    private readonly IBackupDatabase _database;
    private readonly BackupTarget _target;
    private readonly IIngestState _state;
    private readonly BackupOptions _options;
    private readonly IShareConnector? _connector;
    private readonly Func<ShareCredential?>? _credential;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="database">SQL Server.</param>
    /// <param name="state">Where verification results are recorded.</param>
    /// <param name="options">Settings (the same as the backup's).</param>
    /// <param name="connector">Connects to a network share; <see langword="null"/> for a local target.</param>
    /// <param name="credential">Supplies the share credential.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives the log.</param>
    public RestoreService(IBackupDatabase database, IIngestState state, BackupOptions options, IShareConnector? connector = null, Func<ShareCredential?>? credential = null, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _database = database;
        _state = state;
        _options = options;
        _target = new BackupTarget(options.TargetPath);
        _connector = connector;
        _credential = credential;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task<RestoreResult> RestoreAsync(string database, string? backupName, bool keep, bool physicalOnly, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (SqlBackupDatabase.Quote(database) is null)
        {
            throw new ArgumentException("a database name holds only letters, digits and underscores", nameof(database));
        }

        if (string.Equals(database, _database.Name, StringComparison.OrdinalIgnoreCase) || await _database.ExistsAsync(database, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"database {database} already exists; a restore only creates a new database");
        }

        using var connection = Connect();
        var manifests = await _target.ListAsync(cancellationToken).ConfigureAwait(false);
        var chosen = backupName is null ? (manifests.Count > 0 ? manifests[0] : null) : manifests.FirstOrDefault(m => m.Name == backupName);
        if (chosen is null)
        {
            throw new InvalidOperationException(backupName is null ? "there is no backup on the target" : $"there is no backup named {backupName} on the target");
        }

        var full = chosen.Kind == BackupKind.Full
            ? chosen
            : manifests.FirstOrDefault(m => m.Name == chosen.BaseFull) ?? throw new InvalidOperationException($"the full backup {chosen.BaseFull} that {chosen.Name} builds on is not on the target");
        var staging = _options.StagingPath ?? await _database.DefaultBackupDirectoryAsync(cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(staging);
        var copies = new List<string>();
        var created = false;
        try
        {
            var files = new List<BackupManifest> { full };
            if (!ReferenceEquals(full, chosen))
            {
                files.Add(chosen);
            }

            foreach (var manifest in files)
            {
                var copy = Path.Combine(staging, "restore-" + manifest.Name);
                copies.Add(copy);
                File.Copy(_target.FilePath(manifest.Name), copy, overwrite: true);
                if (await BackupTarget.HashAsync(copy, cancellationToken).ConfigureAwait(false) != manifest.Sha256)
                {
                    throw new InvalidDataException($"{manifest.Name} does not match its manifest (hash differs): the backup on the target is damaged");
                }
            }

            created = true;
            await _database.RestoreAsync(database, copies[0], copies.Count > 1 ? copies[1] : null, cancellationToken).ConfigureAwait(false);
            await _database.CheckAsync(database, physicalOnly, cancellationToken).ConfigureAwait(false);
            var comparison = ManifestCheck.Compare(chosen, await _database.CountsAsync(database, cancellationToken).ConfigureAwait(false));
            if (!keep)
            {
                await _database.DropAsync(database, cancellationToken).ConfigureAwait(false);
                created = false;
            }

            return new RestoreResult(database, [.. files.Select(f => f.Name)], keep, comparison, timer.Elapsed);
        }
        finally
        {
            if (created && !keep)
            {
                await DropQuietlyAsync(database).ConfigureAwait(false);
            }

            foreach (var copy in copies)
            {
                try
                {
                    File.Delete(copy);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogCleanup(_logger, copy, ex.Message);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<bool> VerifyAsync(bool physicalOnly, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var scratch = string.Create(CultureInfo.InvariantCulture, $"{_database.Name}_verify_{now:yyyyMMddHHmmss}");
        string text;
        var good = false;
        try
        {
            var result = await RestoreAsync(scratch, null, keep: false, physicalOnly, cancellationToken).ConfigureAwait(false);
            good = result.Comparison.Matches;
            text = good
                ? string.Create(CultureInfo.InvariantCulture, $"ok {string.Join('+', result.Backups)} restored and checked in {(int)result.Elapsed.TotalSeconds} s")
                : "error the restored copy disagrees with its manifest: " + string.Join("; ", result.Comparison.Differences.Take(3));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            text = "error " + ex.Message;
        }

        LogVerified(_logger, good, text);
        try
        {
            await _state.SetAsync(BackupStateKeys.LastVerify, text.Length > 400 ? text[..400] : text, _time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCleanup(_logger, "state", ex.Message);
        }

        return good;
    }

    private IDisposable? Connect()
    {
        if (_connector is null || _credential?.Invoke() is not { } credential || !_options.TargetPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        return _connector.Connect(_options.TargetPath, credential);
    }

    private async Task DropQuietlyAsync(string database)
    {
        try
        {
            await _database.DropAsync(database, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCleanup(_logger, database, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "backup verification {Good}: {Text}")]
    private static partial void LogVerified(ILogger logger, bool good, string text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "restore cleanup of {What} failed: {Reason}")]
    private static partial void LogCleanup(ILogger logger, string what, string reason);
}
