using System.Diagnostics;
using System.Globalization;
using KalTerm.Data.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Backup;

/// <summary>Settings of the backup service.</summary>
public sealed record BackupOptions
{
    /// <summary>The folder the finished backups live in (local, or <c>\\host\share\folder</c>).</summary>
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>Where SQL Server writes a backup before it is verified and copied to the target; <see langword="null"/> for SQL Server's own default backup folder.</summary>
    public string? StagingPath { get; init; }

    /// <summary>A full backup is taken when none is this old. Default 7 days.</summary>
    public TimeSpan MaxFullAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The day of the week (UTC) on which the first backup is a full one. Default Sunday.</summary>
    public DayOfWeek FullOn { get; init; } = DayOfWeek.Sunday;

    /// <summary>The time of day (UTC) from which the daily backup runs. Default 08:00.</summary>
    public TimeOnly RunAt { get; init; } = new(8, 0);

    /// <summary>Space that must be free, as a multiple of the last backup's size, on the staging folder and on the target. Default 1.2.</summary>
    public double FreeSpaceFactor { get; init; } = 1.2;

    /// <summary>How many generations are kept.</summary>
    public GfsOptions Retention { get; init; } = new();

    /// <summary>Throws if a value cannot work.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(FreeSpaceFactor, 1.0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxFullAge, TimeSpan.Zero);
        Retention.Validate();
    }
}

/// <summary>What one backup run did.</summary>
/// <param name="Success">Whether a backup is now safely on the target.</param>
/// <param name="Name">The backup file's name, if one was made.</param>
/// <param name="Kind">Full or differential.</param>
/// <param name="SizeBytes">The file's size.</param>
/// <param name="Elapsed">How long the run took.</param>
/// <param name="Deleted">How many old backups retention removed.</param>
/// <param name="Problem">Why it failed.</param>
public sealed record BackupResult(bool Success, string? Name, BackupKind? Kind, long SizeBytes, TimeSpan Elapsed, int Deleted, string? Problem);

/// <summary>The keys the backup writes to <c>dbo.ingest_state</c>.</summary>
public static class BackupStateKeys
{
    /// <summary>What the last run did; its time is the run's end.</summary>
    public const string Last = "backup.last";

    /// <summary>The name of the last good backup; its time is when it finished. Status raises a problem when this is older than 36 hours.</summary>
    public const string LastGood = "backup.last_good";

    /// <summary>The backup target, written by the schedule when it changes; present means backups are expected, and its time is when that began.</summary>
    public const string Configured = "backup.configured";

    /// <summary>What the last verification (a restore into a scratch database) did.</summary>
    public const string LastVerify = "backup.last_verify";
}

/// <summary>Takes a backup.</summary>
public interface IBackupService
{
    /// <summary>Takes a backup now. Never throws except when cancelled: a failure is in the result, in the state and in the log.</summary>
    /// <param name="force">Force a kind; <see langword="null"/> lets the schedule decide.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What it did.</returns>
    Task<BackupResult> RunAsync(BackupKind? force = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Takes a backup with SQL Server writing to a staging folder (its service account cannot reach a network share), checks it, and moves it to the target: backup with checksums and compression
/// to a <c>.partial</c> file, <c>RESTORE VERIFYONLY</c>, a copy to the target with the hash compared, a rename, and last the manifest, so a crash or a full disk never leaves anything on the
/// target that looks like a backup. Then retention removes what is no longer wanted.
/// </summary>
public sealed partial class BackupService : IBackupService
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
    /// <param name="state">Where results are recorded.</param>
    /// <param name="options">Settings.</param>
    /// <param name="connector">Connects to a network share; <see langword="null"/> for a local target.</param>
    /// <param name="credential">Supplies the share credential; <see langword="null"/> when none is needed.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives the log.</param>
    public BackupService(IBackupDatabase database, IIngestState state, BackupOptions options, IShareConnector? connector = null, Func<ShareCredential?>? credential = null, TimeProvider? time = null, ILogger? logger = null)
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
    public async Task<BackupResult> RunAsync(BackupKind? force = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        string? stagingFile = null;
        string? name = null;
        try
        {
            using var connection = Connect();
            var existing = await _target.ListAsync(cancellationToken).ConfigureAwait(false);
            var kind = force ?? Decide(existing, _time.GetUtcNow().UtcDateTime);
            var lastSize = existing.Count == 0 ? 0 : existing.Max(m => m.SizeBytes);
            var needed = (long)(lastSize * _options.FreeSpaceFactor);
            _target.Check(needed);
            var staging = _options.StagingPath ?? await _database.DefaultBackupDirectoryAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(staging);
            if (BackupTarget.FreeBytes(staging) is { } stagingFree && stagingFree < needed)
            {
                throw new BackupTargetException($"the staging folder {staging} has {stagingFree / (1024 * 1024)} MB free but {needed / (1024 * 1024)} MB are needed");
            }

            var baseFull = kind == BackupKind.Differential ? existing.Where(m => m.Kind == BackupKind.Full).OrderByDescending(m => m.StartedAt).FirstOrDefault()?.Name ?? throw new BackupTargetException("a differential backup needs a full backup on the target first") : null;
            var started = _time.GetUtcNow().UtcDateTime;
            name = BackupNaming.FileName(kind, started);
            stagingFile = Path.Combine(staging, name + BackupNaming.PartialSuffix);
            _target.DeletePartials();

            var before = await _database.CountsAsync(null, cancellationToken).ConfigureAwait(false);
            await _database.BackupAsync(kind, stagingFile, cancellationToken).ConfigureAwait(false);
            await _database.VerifyAsync(stagingFile, cancellationToken).ConfigureAwait(false);
            var after = await _database.CountsAsync(null, cancellationToken).ConfigureAwait(false);
            var size = new FileInfo(stagingFile).Length;
            var hash = await BackupTarget.HashAsync(stagingFile, cancellationToken).ConfigureAwait(false);

            var partial = _target.FilePath(name + BackupNaming.PartialSuffix);
            File.Copy(stagingFile, partial, overwrite: true);
            if (await BackupTarget.HashAsync(partial, cancellationToken).ConfigureAwait(false) != hash)
            {
                File.Delete(partial);
                throw new InvalidDataException("the copy on the target does not match the backup (hash differs); it was removed");
            }

            File.Move(partial, _target.FilePath(name), overwrite: true);
            var finished = _time.GetUtcNow().UtcDateTime;
            var manifest = new BackupManifest(1, name, kind, baseFull, started, finished, _database.Name, size, hash, await _database.VersionAsync(cancellationToken).ConfigureAwait(false), before, after);
            var manifestPartial = _target.FilePath(BackupNaming.ManifestName(name) + BackupNaming.PartialSuffix);
            await File.WriteAllTextAsync(manifestPartial, manifest.ToJson(), cancellationToken).ConfigureAwait(false);
            File.Move(manifestPartial, _target.FilePath(BackupNaming.ManifestName(name)), overwrite: true);
            File.Delete(stagingFile);
            stagingFile = null;

            var deleted = await ApplyRetentionAsync(finished, cancellationToken).ConfigureAwait(false);
            var result = new BackupResult(true, name, kind, size, timer.Elapsed, deleted, null);
            await RecordAsync(string.Create(CultureInfo.InvariantCulture, $"ok {(kind == BackupKind.Full ? "full" : "diff")} {name} {size / (1024 * 1024)} MB in {(int)timer.Elapsed.TotalSeconds} s; {deleted} old backups deleted"), good: name, cancellationToken).ConfigureAwait(false);
            LogDone(_logger, name, size / (1024 * 1024), (int)timer.Elapsed.TotalSeconds, deleted);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(_logger, ex.Message);
            SafeDelete(stagingFile);
            await RecordAsync("error " + ex.Message, good: null, cancellationToken).ConfigureAwait(false);
            return new BackupResult(false, name, null, 0, timer.Elapsed, 0, ex.Message);
        }
    }

    private BackupKind Decide(IReadOnlyList<BackupManifest> existing, DateTime now)
    {
        var lastFull = existing.Where(m => m.Kind == BackupKind.Full).OrderByDescending(m => m.StartedAt).FirstOrDefault();
        return lastFull is null || now - lastFull.StartedAt >= _options.MaxFullAge || (now.DayOfWeek == _options.FullOn && lastFull.StartedAt.Date < now.Date)
            ? BackupKind.Full
            : BackupKind.Differential;
    }

    private IDisposable? Connect()
    {
        if (_connector is null || _credential?.Invoke() is not { } credential || !_options.TargetPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        return _connector.Connect(_options.TargetPath, credential);
    }

    private async Task<int> ApplyRetentionAsync(DateTime now, CancellationToken cancellationToken)
    {
        var manifests = await _target.ListAsync(cancellationToken).ConfigureAwait(false);
        var entries = manifests.Select(m => new BackupEntry(m.Name, m.Kind, m.StartedAt, m.BaseFull)).ToList();
        var keep = GfsRetention.Keep(entries, now, _options.Retention);
        var deleted = 0;
        foreach (var entry in entries.Where(e => !keep.Contains(e.Name)))
        {
            _target.Delete(entry.Name);
            deleted++;
        }

        return deleted;
    }

    private async Task RecordAsync(string text, string? good, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            await _state.SetAsync(BackupStateKeys.Last, text.Length > 400 ? text[..400] : text, now, cancellationToken).ConfigureAwait(false);
            if (good is not null)
            {
                await _state.SetAsync(BackupStateKeys.LastGood, good, now, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(_logger, "could not record the backup result: " + ex.Message);
        }
    }

    private static void SafeDelete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover staging file is removed by the next run's overwrite; nothing more to do here.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "backup {Name} is on the target: {SizeMb} MB in {Seconds} s; retention deleted {Deleted}")]
    private static partial void LogDone(ILogger logger, string name, long sizeMb, int seconds, int deleted);

    [LoggerMessage(Level = LogLevel.Warning, Message = "backup failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
