using KalTerm.Data.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Backup;

/// <summary>Runs the daily backup when it is due.</summary>
public interface IBackupJob
{
    /// <summary>Takes a backup (and, after a full one, proves it) if one is due. Never throws except when cancelled.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see langword="true"/> when a backup was attempted.</returns>
    Task<bool> RunIfDueAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides when the backup runs. A backup is due when there has never been a good one, when the last good one is more than 26 hours old (the host was down at the usual time),
/// or when it is past <see cref="BackupOptions.RunAt"/> and there has been none today. After a failure the next attempt waits <see cref="RetryAfter"/>. After every <b>full</b> backup the
/// backup is proven by a restore into a scratch database (the physical-only check; a complete <c>CHECKDB</c> is for the admin tool).
/// </summary>
public sealed partial class BackupSchedule : IBackupJob
{
    /// <summary>How long to wait after a failed attempt.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    /// <summary>A last good backup older than this is overdue whatever the time of day.</summary>
    public static readonly TimeSpan Overdue = TimeSpan.FromHours(26);

    private readonly IBackupService _backups;
    private readonly IRestoreService _restores;
    private readonly IIngestState _state;
    private readonly BackupOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Creates the schedule.</summary>
    /// <param name="backups">Takes backups.</param>
    /// <param name="restores">Proves them.</param>
    /// <param name="state">Where the last results are recorded.</param>
    /// <param name="options">Settings.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives the log.</param>
    public BackupSchedule(IBackupService backups, IRestoreService restores, IIngestState state, BackupOptions options, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(restores);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _backups = backups;
        _restores = restores;
        _state = state;
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task<bool> RunIfDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            var configured = await _state.GetAsync(BackupStateKeys.Configured, cancellationToken).ConfigureAwait(false);
            if (configured?.Value != _options.TargetPath)
            {
                await _state.SetAsync(BackupStateKeys.Configured, _options.TargetPath, now, cancellationToken).ConfigureAwait(false);
            }

            var good = await _state.GetAsync(BackupStateKeys.LastGood, cancellationToken).ConfigureAwait(false);
            var last = await _state.GetAsync(BackupStateKeys.Last, cancellationToken).ConfigureAwait(false);
            if (!IsDue(now, good, last))
            {
                return false;
            }

            var result = await _backups.RunAsync(null, cancellationToken).ConfigureAwait(false);
            if (result.Success && result.Kind == BackupKind.Full)
            {
                await _restores.VerifyAsync(physicalOnly: true, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(_logger, ex.Message);
            return false;
        }
    }

    private bool IsDue(DateTime now, IngestStateEntry? good, IngestStateEntry? last)
    {
        if (last is not null && last.Value.StartsWith("error", StringComparison.Ordinal) && now - last.UpdatedAt < RetryAfter)
        {
            return false;
        }

        if (good is null || now - good.UpdatedAt > Overdue)
        {
            return true;
        }

        return DateOnly.FromDateTime(good.UpdatedAt) < DateOnly.FromDateTime(now) && TimeOnly.FromDateTime(now) >= _options.RunAt;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "the backup schedule could not run: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
