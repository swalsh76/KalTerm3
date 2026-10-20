using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Ingest.Analytics;

/// <summary>What an analyzer is given when it runs.</summary>
/// <param name="Now">The current UTC time.</param>
public sealed record AnalysisContext(DateTime Now);

/// <summary>What an analyzer reports.</summary>
/// <param name="Rows">How many result rows it looked at (or wrote).</param>
/// <param name="Changed">Whether its results differ from the run before; this is what bumps the analyzer's change counter.</param>
/// <param name="Message">A few words for the run history, or <see langword="null"/>.</param>
public sealed record AnalyzerOutcome(int Rows, bool Changed, string? Message = null);

/// <summary>
/// A piece of analysis the runner schedules. It takes the clock and returns its outcome; it writes its own result table. It does not know how it is scheduled, locked or timed, so heavier analyzers can
/// be added later without changing the runner.
/// </summary>
public interface IAnalyzer
{
    /// <summary>The analyzer's name: letters, digits and underscores, at most 40 characters (it is part of a state key).</summary>
    string Name { get; }

    /// <summary>How often it runs.</summary>
    TimeSpan Interval { get; }

    /// <summary>Does the analysis.</summary>
    /// <param name="context">The clock.</param>
    /// <param name="cancellationToken">Cancelled on a timeout or a stop; the analyzer should stop soon after.</param>
    /// <returns>The outcome.</returns>
    Task<AnalyzerOutcome> RunAsync(AnalysisContext context, CancellationToken cancellationToken = default);
}

/// <summary>Runs the analyzers that are due.</summary>
public interface IAnalyzerRunner
{
    /// <summary>Runs every analyzer that is due and not already running. Never throws except when cancelled.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many analyzers ran.</returns>
    Task<int> RunOnceAsync(CancellationToken cancellationToken = default);
}

/// <summary>The held lock of one analyzer; disposing it releases the lock.</summary>
public interface IAnalyzerLease : IAsyncDisposable;

/// <summary>What the runner remembers: when each analyzer last started, who holds its lock, the run history and the change counter.</summary>
public interface IAnalyzerStore
{
    /// <summary>When the analyzer last started (UTC); <see langword="null"/> when it never has.</summary>
    /// <param name="name">The analyzer.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The moment.</returns>
    Task<DateTime?> GetLastStartAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Records that the analyzer started.</summary>
    /// <param name="name">The analyzer.</param>
    /// <param name="at">When (UTC).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    Task SetLastStartAsync(string name, DateTime at, CancellationToken cancellationToken = default);

    /// <summary>Takes the analyzer's lock without waiting.</summary>
    /// <param name="name">The analyzer.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The lease, or <see langword="null"/> when someone else holds the lock.</returns>
    Task<IAnalyzerLease?> TryLockAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Opens a run in the history.</summary>
    /// <param name="name">The analyzer.</param>
    /// <param name="startedAt">When it started (UTC).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The run's id.</returns>
    Task<long> BeginRunAsync(string name, DateTime startedAt, CancellationToken cancellationToken = default);

    /// <summary>Closes a run in the history.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="finishedAt">When it ended (UTC).</param>
    /// <param name="status">ok, failed or timeout.</param>
    /// <param name="rows">How many rows, or <see langword="null"/>.</param>
    /// <param name="changed">Whether the results changed, or <see langword="null"/>.</param>
    /// <param name="message">A few words, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    Task FinishRunAsync(long runId, DateTime finishedAt, string status, int? rows, bool? changed, string? message, CancellationToken cancellationToken = default);

    /// <summary>Raises the analyzer's change counter by one (alerts and push watch it).</summary>
    /// <param name="name">The analyzer.</param>
    /// <param name="at">When (UTC).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    Task BumpVersionAsync(string name, DateTime at, CancellationToken cancellationToken = default);

    /// <summary>Deletes history older than a moment.</summary>
    /// <param name="olderThan">The cut-off (UTC).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many runs were deleted.</returns>
    Task<int> PruneRunsAsync(DateTime olderThan, CancellationToken cancellationToken = default);
}

/// <summary>Tuning for <see cref="AnalyzerRunner"/>.</summary>
public sealed record AnalyzerRunnerOptions
{
    /// <summary>The longest one run may take before it is cancelled. Default 5 minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the run history is kept. Default 7 days.</summary>
    public TimeSpan RunRetention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Throws if a value cannot work.</summary>
    public void Validate()
    {
        if (Timeout <= TimeSpan.Zero || RunRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AnalyzerRunnerOptions), "the timeout and the retention must be positive");
        }
    }
}

/// <summary>
/// Runs analyzers when they are due: an analyzer starts when its interval has passed since its last start (a failure counts, so a broken analyzer is not hammered), under its own lock so two runs
/// never overlap (a second process, or a run that outlived its interval, is skipped), within a timeout. Every run lands in the history. A failure is logged and recorded, never thrown. When a run
/// reports a change, the analyzer's change counter is raised.
/// </summary>
public sealed partial class AnalyzerRunner : IAnalyzerRunner
{
    private readonly IReadOnlyList<IAnalyzer> _analyzers;
    private readonly IAnalyzerStore _store;
    private readonly AnalyzerRunnerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private DateTime _nextPrune = DateTime.MinValue;

    /// <summary>Creates the runner.</summary>
    /// <param name="analyzers">The analyzers, run in this order.</param>
    /// <param name="store">Remembers runs and holds locks.</param>
    /// <param name="options">Tuning; <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives the log.</param>
    public AnalyzerRunner(IReadOnlyList<IAnalyzer> analyzers, IAnalyzerStore store, AnalyzerRunnerOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(analyzers);
        ArgumentNullException.ThrowIfNull(store);
        _options = options ?? new AnalyzerRunnerOptions();
        _options.Validate();
        foreach (var analyzer in analyzers)
        {
            if (string.IsNullOrWhiteSpace(analyzer.Name) || analyzer.Name.Length > 40 || !analyzer.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || analyzer.Interval <= TimeSpan.Zero)
            {
                throw new ArgumentException("an analyzer needs a name of letters, digits and underscores (at most 40) and a positive interval", nameof(analyzers));
            }
        }

        if (analyzers.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() != analyzers.Count)
        {
            throw new ArgumentException("analyzer names must be unique", nameof(analyzers));
        }

        _analyzers = analyzers;
        _store = store;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var ran = 0;
        foreach (var analyzer in _analyzers)
        {
            try
            {
                if (await RunIfDueAsync(analyzer, cancellationToken).ConfigureAwait(false))
                {
                    ran++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogBookkeepingFailed(_logger, analyzer.Name, ex.Message);
            }
        }

        await PruneAsync(cancellationToken).ConfigureAwait(false);
        return ran;
    }

    private async Task<bool> RunIfDueAsync(IAnalyzer analyzer, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (await _store.GetLastStartAsync(analyzer.Name, cancellationToken).ConfigureAwait(false) is { } last && now < last + analyzer.Interval)
        {
            return false;
        }

        var lease = await _store.TryLockAsync(analyzer.Name, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            LogBusy(_logger, analyzer.Name);
            return false;
        }

        await using (lease.ConfigureAwait(false))
        {
            await _store.SetLastStartAsync(analyzer.Name, now, cancellationToken).ConfigureAwait(false);
            var runId = await _store.BeginRunAsync(analyzer.Name, now, cancellationToken).ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_options.Timeout);
            string status;
            AnalyzerOutcome? outcome = null;
            string? message = null;
            try
            {
                outcome = await analyzer.RunAsync(new AnalysisContext(now), limit.Token).ConfigureAwait(false);
                status = "ok";
                message = outcome.Message;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                status = "timeout";
                message = $"cancelled after {_options.Timeout.TotalSeconds:0} seconds";
            }
            catch (OperationCanceledException)
            {
                await _store.FinishRunAsync(runId, _time.GetUtcNow().UtcDateTime, "failed", null, null, "stopped", CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                status = "failed";
                message = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
            }

            var finished = _time.GetUtcNow().UtcDateTime;
            // The analysis is done: recording it takes a moment and must not be lost to a stop that arrived while it ran.
            await _store.FinishRunAsync(runId, finished, status, outcome?.Rows, outcome?.Changed, message, CancellationToken.None).ConfigureAwait(false);
            if (outcome is { Changed: true })
            {
                await _store.BumpVersionAsync(analyzer.Name, finished, CancellationToken.None).ConfigureAwait(false);
            }

            if (status == "ok")
            {
                LogRan(_logger, analyzer.Name, outcome!.Rows, outcome.Changed, (finished - now).TotalSeconds);
            }
            else
            {
                LogRunFailed(_logger, analyzer.Name, status, message ?? string.Empty);
            }
        }

        return true;
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (now < _nextPrune)
        {
            return;
        }

        _nextPrune = now.AddHours(1);
        try
        {
            await _store.PruneRunsAsync(now - _options.RunRetention, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogBookkeepingFailed(_logger, "history", ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "analyzer {Name}: {Rows} rows, changed={Changed}, {Seconds:0.0} s")]
    private static partial void LogRan(ILogger logger, string name, int rows, bool changed, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "analyzer {Name} {Status}: {Message}")]
    private static partial void LogRunFailed(ILogger logger, string name, string status, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "analyzer {Name} is already running elsewhere; skipped")]
    private static partial void LogBusy(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "analyzer {Name}: bookkeeping failed: {Reason}")]
    private static partial void LogBookkeepingFailed(ILogger logger, string name, string reason);
}
