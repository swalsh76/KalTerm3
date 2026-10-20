using KalTerm.Contracts;
using KalTerm.Data.Governor;
using KalTerm.Data.Reading;
using KalTerm.Data.State;

namespace KalTerm.Api;

/// <summary>Builds the health report from a database snapshot. Anything that needs attention goes in <c>problems</c>, most important first; an empty list is healthy.</summary>
public static class StatusBuilder
{
    /// <summary>How far back the report counts gaps.</summary>
    public static readonly TimeSpan GapWindow = TimeSpan.FromHours(24);

    /// <summary>The ingest process refreshes its heartbeat every 15 seconds; four missed beats is a problem.</summary>
    public static readonly TimeSpan HeartbeatStale = TimeSpan.FromSeconds(60);

    /// <summary>Ticker updates and trades arrive many times a second across all markets; two quiet minutes while ingest runs is a problem.</summary>
    public static readonly TimeSpan StreamStale = TimeSpan.FromMinutes(2);

    /// <summary>The ingest process rewrites every published book at least every 15 seconds, even a quiet one; a minute without is a problem.</summary>
    public static readonly TimeSpan BooksStale = TimeSpan.FromSeconds(60);

    /// <summary>A day's backup is expected once a day; a day and a half without a good one is a problem.</summary>
    public static readonly TimeSpan BackupStale = TimeSpan.FromHours(36);

    /// <summary>The storage governor checks every 15 minutes; an hour without a check while ingest runs is a problem.</summary>
    public static readonly TimeSpan GovernorStale = TimeSpan.FromHours(1);

    /// <summary>The metrics analyzer runs every 5 minutes; 15 minutes without a finished run while ingest runs is a problem.</summary>
    public static readonly TimeSpan AnalyticsStale = TimeSpan.FromMinutes(15);

    /// <summary>The data disk is flagged below this much free space.</summary>
    public const long DiskFreeWarningBytes = 50L * 1024 * 1024 * 1024;

    /// <summary>What a gap means for the data.</summary>
    public const string GapNote = "Ticker updates and order books in a gap are lost and cannot be recovered; trades are recovered from REST (see each gap's backfill).";

    /// <summary>Builds the report.</summary>
    /// <param name="snapshot">What the database says.</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns>The report.</returns>
    public static StatusDto Build(StatusSnapshot snapshot, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var problems = new List<string>();

        var ingest = IngestState(snapshot.Run, now, problems);
        var ticker = Stream(snapshot.LastTickerAt, now);
        var trade = Stream(snapshot.LastTradeAt, now);
        var running = ingest.State == IngestStateKeys.Running;
        CheckStream("ticker", snapshot.LastTickerAt, ticker, running, problems);
        CheckStream("trade", snapshot.LastTradeAt, trade, running, problems);

        var books = Books(snapshot.Books, now, running, problems);

        var used = snapshot.DataUsedBytes ?? snapshot.DataFileBytes;
        var usedPercent = StorageBudget.Percent(used, snapshot.MaxFileBytes);
        switch (StorageBudget.Level(used, snapshot.MaxFileBytes))
        {
            case BudgetLevel.Shed:
                problems.Add($"the database is using {usedPercent}% of its size cap: the storage governor is dropping the oldest raw days");
                break;
            case BudgetLevel.Warn:
                problems.Add($"the database is using {usedPercent}% of its size cap");
                break;
        }

        CheckGovernor(snapshot.GovernorLast, now, running, problems);
        var backup = Backup(snapshot.BackupRecord, now, problems);

        var analytics = Analytics(snapshot.Analytics, now, running, problems);

        if (snapshot.Backfill is { Failed: > 0 } backfill)
        {
            problems.Add($"{backfill.Failed} gap(s) could not have their trades recovered (see kterm gaps)");
        }

        if (snapshot.DiskFreeBytes is { } free && free < DiskFreeWarningBytes)
        {
            problems.Add($"only {free / (1024 * 1024 * 1024)} GB free on the data disk");
        }

        return new StatusDto(
            ApiFormat.Moment(now),
            problems,
            ingest,
            new StreamsDto(ticker, trade),
            new StorageDto(snapshot.DataFileBytes, snapshot.MaxFileBytes, snapshot.DiskFreeBytes, used, usedPercent, snapshot.OldestDataDay, snapshot.GovernorLast?.Value, snapshot.GovernorLast is { } last ? AgeSeconds(last.UpdatedAt, now) : null),
            new Dictionary<string, long>(snapshot.MarketsByStatus, StringComparer.Ordinal),
            new GapSummaryDto((int)GapWindow.TotalHours, snapshot.GapsByKind.Values.Sum(), new Dictionary<string, long>(snapshot.GapsByKind, StringComparer.Ordinal), GapNote, snapshot.Backfill?.Pending ?? 0, snapshot.Backfill?.Failed ?? 0),
            books,
            backup,
            analytics);
    }

    private static AnalyticsStatusDto? Analytics(AnalyticsRecord? record, DateTime now, bool ingestRunning, List<string> problems)
    {
        if (record?.Last is null)
        {
            return record is null ? null : new AnalyticsStatusDto(null, null, null, null, null, null, null);
        }

        var last = record.Last;
        var ok = record.LastOk;
        var dto = new AnalyticsStatusDto(
            last.Status,
            last.Message,
            ApiFormat.Moment(last.StartedAt),
            ApiFormat.Moment(ok?.StartedAt),
            ok is null ? null : AgeSeconds(ok.StartedAt, now),
            ok?.FinishedAt is { } finished ? (long)(finished - ok.StartedAt).TotalSeconds : null,
            ok?.Rows);
        if (last.Status is "failed" or "timeout")
        {
            problems.Add($"the metrics analyzer's last run {last.Status}: {last.Message}");
        }
        else if (ingestRunning && (ok is null || now - ok.StartedAt > AnalyticsStale))
        {
            problems.Add(ok is null ? "the metrics analyzer has not finished a run" : $"the metrics analyzer has not finished a run for {AgeSeconds(ok.StartedAt, now) / 60} min");
        }

        return dto;
    }

    private static BackupStatusDto Backup(BackupRecord? record, DateTime now, List<string> problems)
    {
        var configured = record?.Configured is not null;
        var dto = new BackupStatusDto(
            configured,
            record?.LastGood?.Value,
            ApiFormat.Moment(record?.LastGood?.UpdatedAt),
            record?.LastGood is { } good ? AgeSeconds(good.UpdatedAt, now) : null,
            record?.Last?.Value,
            ApiFormat.Moment(record?.Last?.UpdatedAt),
            record?.LastVerify?.Value,
            ApiFormat.Moment(record?.LastVerify?.UpdatedAt));
        if (!configured)
        {
            return dto;
        }

        if (record!.LastGood is null)
        {
            if (now - record.Configured!.UpdatedAt > BackupStale)
            {
                problems.Add($"no backup has finished in the {AgeSeconds(record.Configured.UpdatedAt, now) / 3600} h since a backup target was configured");
            }
        }
        else if (now - record.LastGood.UpdatedAt > BackupStale)
        {
            problems.Add($"the last good backup is {AgeSeconds(record.LastGood.UpdatedAt, now) / 3600} h old");
        }

        if (record.Last is { } last && last.Value.StartsWith("error", StringComparison.Ordinal) && (record.LastGood is null || last.UpdatedAt >= record.LastGood.UpdatedAt))
        {
            problems.Add($"the last backup attempt failed: {last.Value}");
        }

        if (record.LastVerify is { } verify && verify.Value.StartsWith("error", StringComparison.Ordinal))
        {
            problems.Add($"the last proof of a backup by restore failed: {verify.Value}");
        }

        return dto;
    }

    private static void CheckGovernor(IngestStateEntry? last, DateTime now, bool ingestRunning, List<string> problems)
    {
        if (last is null)
        {
            return;   // an ingest build from before the governor, or its first check is still to come
        }

        if (last.Value.StartsWith("error", StringComparison.Ordinal))
        {
            problems.Add($"the storage governor reports: {last.Value}");
        }
        else if (ingestRunning && AgeSeconds(last.UpdatedAt, now) > GovernorStale.TotalSeconds)
        {
            problems.Add($"the storage governor has not checked for {AgeSeconds(last.UpdatedAt, now) / 60} min although ingest says it is running");
        }
    }

    private static BooksStatusDto Books(BookStatus? books, DateTime now, bool ingestRunning, List<string> problems)
    {
        if (books is null || books.Watched == 0)
        {
            return new BooksStatusDto(0, 0, 0, null);
        }

        long? age = books.NewestPublishedAt is { } newest ? AgeSeconds(newest, now) : null;
        if (books.Stale > 0)
        {
            problems.Add($"{books.Stale} of {books.Watched} watched order books are stale (messages were missed and recovery has not finished)");
        }

        if (ingestRunning && age > BooksStale.TotalSeconds)
        {
            problems.Add($"the order books were last published {age} s ago although ingest says it is running");
        }

        return new BooksStatusDto(books.Watched, books.Stale, books.Approximate, age);
    }

    private static IngestStateDto IngestState(IngestStateEntry? run, DateTime now, List<string> problems)
    {
        if (run is null)
        {
            problems.Add("the ingest process has never recorded a run");
            return new IngestStateDto("unknown", null, null);
        }

        var age = AgeSeconds(run.UpdatedAt, now);
        if (run.Value == IngestStateKeys.Running && age > HeartbeatStale.TotalSeconds)
        {
            problems.Add($"the ingest heartbeat is {age} s old: the ingest process may be down");
        }
        else if (run.Value == IngestStateKeys.Stopped)
        {
            problems.Add($"the ingest process is stopped (since {ApiFormat.Moment(run.UpdatedAt)})");
        }

        return new IngestStateDto(run.Value, ApiFormat.Moment(run.UpdatedAt), age);
    }

    private static StreamDto Stream(DateTime? last, DateTime now) =>
        last is { } value ? new StreamDto(ApiFormat.Moment(value), AgeSeconds(value, now)) : new StreamDto(null, null);

    private static void CheckStream(string name, DateTime? last, StreamDto stream, bool ingestRunning, List<string> problems)
    {
        if (last is null)
        {
            problems.Add($"no {name} data in the last 24 hours");
        }
        else if (ingestRunning && stream.AgeSeconds > StreamStale.TotalSeconds)
        {
            problems.Add($"no {name} data for {stream.AgeSeconds} s although ingest says it is running");
        }
    }

    private static long AgeSeconds(DateTime since, DateTime now) => Math.Max(0, (long)(now - since).TotalSeconds);
}
