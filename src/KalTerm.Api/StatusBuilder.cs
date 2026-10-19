using KalTerm.Contracts;
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

    /// <summary>The data file is flagged at this share of its hard maximum.</summary>
    public const double CapWarningShare = 0.9;

    /// <summary>The data disk is flagged below this much free space.</summary>
    public const long DiskFreeWarningBytes = 50L * 1024 * 1024 * 1024;

    /// <summary>What a gap means for the data.</summary>
    public const string GapNote = "Ticker updates in a gap are lost and cannot be recovered; trade backfill arrives in a later milestone.";

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

        if (snapshot.MaxFileBytes is { } max && max > 0 && snapshot.DataFileBytes >= max * CapWarningShare)
        {
            problems.Add($"the database file is at {snapshot.DataFileBytes * 100 / max}% of its size cap");
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
            new StorageDto(snapshot.DataFileBytes, snapshot.MaxFileBytes, snapshot.DiskFreeBytes),
            new Dictionary<string, long>(snapshot.MarketsByStatus, StringComparer.Ordinal),
            new GapSummaryDto((int)GapWindow.TotalHours, snapshot.GapsByKind.Values.Sum(), new Dictionary<string, long>(snapshot.GapsByKind, StringComparer.Ordinal), GapNote),
            books);
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
