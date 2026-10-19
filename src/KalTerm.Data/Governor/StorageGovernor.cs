using System.Globalization;
using Dapper;
using KalTerm.Data.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Data.Governor;

/// <summary>Where the database stands against its size cap.</summary>
public enum BudgetLevel
{
    /// <summary>Plenty of room.</summary>
    Ok,

    /// <summary>At or over <see cref="StorageBudget.WarnPercent"/> of the cap: the status report warns.</summary>
    Warn,

    /// <summary>At or over <see cref="StorageBudget.ShedPercent"/> of the cap: the governor drops the oldest raw day.</summary>
    Shed,
}

/// <summary>The thresholds of the size budget. They are measured against <b>used</b> space, because dropping a day frees pages inside the file but never shrinks the file.</summary>
public static class StorageBudget
{
    /// <summary>The status report warns from this share of the cap (percent).</summary>
    public const int WarnPercent = 80;

    /// <summary>The governor starts dropping raw days from this share of the cap (percent).</summary>
    public const int ShedPercent = 90;

    /// <summary>The newest days that are never dropped, whatever the retention setting or the size: today and the days before it.</summary>
    public const int MinKeepDays = 3;

    /// <summary>The share of the cap that is used, in whole percent (rounded down); <see langword="null"/> when there is no cap.</summary>
    /// <param name="usedBytes">Space used by data.</param>
    /// <param name="maxBytes">The cap, if any.</param>
    /// <returns>The percentage.</returns>
    public static int? Percent(long usedBytes, long? maxBytes) =>
        maxBytes is { } max && max > 0 ? (int)Math.Min(int.MaxValue, usedBytes * 100 / max) : null;

    /// <summary>The level for a use.</summary>
    /// <param name="usedBytes">Space used by data.</param>
    /// <param name="maxBytes">The cap, if any.</param>
    /// <returns>The level; <see cref="BudgetLevel.Ok"/> when there is no cap.</returns>
    public static BudgetLevel Level(long usedBytes, long? maxBytes) => Percent(usedBytes, maxBytes) switch
    {
        >= ShedPercent => BudgetLevel.Shed,
        >= WarnPercent => BudgetLevel.Warn,
        _ => BudgetLevel.Ok,
    };
}

/// <summary>Retention settings of the governor.</summary>
public sealed record GovernorOptions
{
    /// <summary>How many days of raw ticker and trade data are kept before the daily retention drops them. Default 30.</summary>
    public int RawRetentionDays { get; init; } = 30;

    /// <summary>The time of day (UTC) from which the daily retention may run. Default 00:30.</summary>
    public TimeOnly RetentionAt { get; init; } = new(0, 30);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if a value cannot work.</summary>
    public void Validate() => ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RawRetentionDays, StorageBudget.MinKeepDays);
}

/// <summary>The keys the governor writes to <c>dbo.ingest_state</c>.</summary>
public static class GovernorStateKeys
{
    /// <summary>The UTC day (<c>yyyy-MM-dd</c>) on which the daily retention last completed.</summary>
    public const string RetentionDay = "governor.retention_day";

    /// <summary>What the last check did: <c>ok</c> or <c>error</c>, the percent of the cap in use, the days dropped; its time is the check's time.</summary>
    public const string Last = "governor.last";
}

/// <summary>Space in the data file(s) of the database.</summary>
/// <param name="UsedBytes">Pages holding data.</param>
/// <param name="FileBytes">The allocated size of the file(s).</param>
/// <param name="MaxBytes">The hard cap; <see langword="null"/> when unlimited.</param>
public sealed record StorageUsage(long UsedBytes, long FileBytes, long? MaxBytes);

/// <summary>What one check of the governor did.</summary>
/// <param name="At">When it ran (UTC).</param>
/// <param name="Level">Where the database stood against its cap.</param>
/// <param name="UsedPercent">The share of the cap in use, or <see langword="null"/> when there is no cap or it could not be read.</param>
/// <param name="Dropped">The days whose raw data was dropped.</param>
/// <param name="Problem">Why something could not be done, or <see langword="null"/>.</param>
public sealed record GovernorReport(DateTime At, BudgetLevel Level, int? UsedPercent, IReadOnlyList<DateOnly> Dropped, string? Problem);

/// <summary>What the governor needs from the database.</summary>
public interface IGovernorStore
{
    /// <summary>Used and allocated space and the cap.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The space.</returns>
    Task<StorageUsage> GetUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>The UTC day of the oldest row in the ticker and trade tables; <see langword="null"/> when both are empty.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The day.</returns>
    Task<DateOnly?> GetOldestDataDayAsync(CancellationToken cancellationToken = default);

    /// <summary>The days that have a partition boundary of their own.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The days, ascending.</returns>
    Task<IReadOnlyList<DateOnly>> GetBoundariesAsync(CancellationToken cancellationToken = default);

    /// <summary>Empties one day in every time-series table.</summary>
    /// <param name="day">The UTC day.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the day is empty.</returns>
    Task TruncateDayAsync(DateOnly day, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IGovernorStore"/> over the KalTerm database.</summary>
/// <param name="source">Opens connections.</param>
public sealed class GovernorStore(DataSource source) : IGovernorStore
{
    /// <inheritdoc/>
    public async Task<StorageUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleAsync<(long Used, long File, long? Max)>(new CommandDefinition(
            """
            SELECT CAST(SUM(CAST(FILEPROPERTY(name, 'SpaceUsed') AS bigint)) * 8192 AS bigint) AS Used,
                   CAST(SUM(CAST(size AS bigint)) * 8192 AS bigint) AS [File],
                   CASE WHEN MIN(max_size) = -1 THEN NULL ELSE CAST(SUM(CAST(max_size AS bigint)) * 8192 AS bigint) END AS Max
            FROM sys.database_files WHERE type = 0
            """,
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new StorageUsage(row.Used, row.File, row.Max);
    }

    /// <inheritdoc/>
    public async Task<DateOnly?> GetOldestDataDayAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var oldest = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT MIN(d) FROM (SELECT MIN(ts) AS d FROM dbo.ticker UNION ALL SELECT MIN(ts) FROM dbo.trade) AS x",
            commandTimeout: 120,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return oldest is { } found ? DateOnly.FromDateTime(found) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DateOnly>> GetBoundariesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await PartitionManager.GetBoundariesAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task TruncateDayAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await PartitionManager.TruncateDayAsync(connection, day, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Runs a governor check.</summary>
public interface IStorageGovernor
{
    /// <summary>Applies the retention if it is due, then the size budget. Never throws except when cancelled: a failure is in the report and in the log.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>What it did.</returns>
    Task<GovernorReport> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps the database inside its size cap. <b>Retention</b>: once a day (from <see cref="GovernorOptions.RetentionAt"/>) every day older than the retention window is
/// dropped whole, oldest first. <b>Budget</b>: from <see cref="StorageBudget.ShedPercent"/> of the cap the oldest raw day is dropped, one day per check (freed pages
/// are given back asynchronously, so one step is measured before the next). Neither ever touches the newest <see cref="StorageBudget.MinKeepDays"/> days; when the
/// budget needs more than that, the report says so and nothing else is dropped. A day is dropped by partition (<c>TRUNCATE</c>), never with <c>DELETE</c>.
/// </summary>
public sealed partial class StorageGovernor : IStorageGovernor
{
    private readonly IGovernorStore _store;
    private readonly IIngestState _state;
    private readonly GovernorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Creates a governor.</summary>
    /// <param name="store">The database side.</param>
    /// <param name="state">Where the governor remembers its last run.</param>
    /// <param name="options">Retention settings; <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives the log.</param>
    public StorageGovernor(IGovernorStore store, IIngestState state, GovernorOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(state);
        _options = options ?? new GovernorOptions();
        _options.Validate();
        _store = store;
        _state = state;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public async Task<GovernorReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var dropped = new List<DateOnly>();
        string? problem = null;
        var level = BudgetLevel.Ok;
        int? percent = null;
        try
        {
            problem = await RetainAsync(now, today, dropped, cancellationToken).ConfigureAwait(false);
            var usage = await _store.GetUsageAsync(cancellationToken).ConfigureAwait(false);
            percent = StorageBudget.Percent(usage.UsedBytes, usage.MaxBytes);
            level = StorageBudget.Level(usage.UsedBytes, usage.MaxBytes);
            if (level == BudgetLevel.Shed && dropped.Count == 0)
            {
                problem = await ShedAsync(today, percent, dropped, cancellationToken).ConfigureAwait(false) ?? problem;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            problem = ex.Message;
            LogFailed(_logger, ex.Message);
        }

        var report = new GovernorReport(now, level, percent, dropped, problem);
        await RememberAsync(report, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private async Task<string?> RetainAsync(DateTime now, DateOnly today, List<DateOnly> dropped, CancellationToken cancellationToken)
    {
        if (TimeOnly.FromDateTime(now) < _options.RetentionAt)
        {
            return null;
        }

        var done = await _state.GetAsync(GovernorStateKeys.RetentionDay, cancellationToken).ConfigureAwait(false);
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (done?.Value == todayText)
        {
            return null;
        }

        var cutoff = today.AddDays(-_options.RawRetentionDays);
        string? problem = null;
        if (await _store.GetOldestDataDayAsync(cancellationToken).ConfigureAwait(false) is { } oldest && oldest < cutoff)
        {
            var boundaries = await _store.GetBoundariesAsync(cancellationToken).ConfigureAwait(false);
            if (boundaries.Count > 0 && oldest < boundaries[0])
            {
                problem = $"data from {oldest:yyyy-MM-dd} is older than the first partition boundary and cannot be dropped by day";
            }

            foreach (var day in boundaries.Where(d => d >= oldest && d < cutoff))
            {
                await _store.TruncateDayAsync(day, cancellationToken).ConfigureAwait(false);
                dropped.Add(day);
                LogDropped(_logger, day, "retention");
            }
        }

        await _state.SetAsync(GovernorStateKeys.RetentionDay, todayText, now, cancellationToken).ConfigureAwait(false);
        return problem;
    }

    private async Task<string?> ShedAsync(DateOnly today, int? percent, List<DateOnly> dropped, CancellationToken cancellationToken)
    {
        var floor = today.AddDays(-StorageBudget.MinKeepDays);
        if (await _store.GetOldestDataDayAsync(cancellationToken).ConfigureAwait(false) is { } oldest && oldest < floor)
        {
            var boundaries = await _store.GetBoundariesAsync(cancellationToken).ConfigureAwait(false);
            if (boundaries.Contains(oldest))
            {
                await _store.TruncateDayAsync(oldest, cancellationToken).ConfigureAwait(false);
                dropped.Add(oldest);
                LogDropped(_logger, oldest, "budget");
                return null;
            }

            return $"the database is at {percent}% of its cap but the oldest data ({oldest:yyyy-MM-dd}) has no partition boundary of its own";
        }

        return $"the database is at {percent}% of its cap and the newest {StorageBudget.MinKeepDays} days are kept: nothing more can be dropped";
    }

    private async Task RememberAsync(GovernorReport report, CancellationToken cancellationToken)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{(report.Problem is null ? "ok" : "error")} used={report.UsedPercent?.ToString(CultureInfo.InvariantCulture) ?? "-"}% dropped={string.Join(',', report.Dropped.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}{(report.Problem is null ? string.Empty : " " + report.Problem)}");
        try
        {
            await _state.SetAsync(GovernorStateKeys.Last, text.Length > 400 ? text[..400] : text, report.At, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(_logger, "could not record the check: " + ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "dropped the raw data of {Day} ({Reason})")]
    private static partial void LogDropped(ILogger logger, DateOnly day, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "storage governor check failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
