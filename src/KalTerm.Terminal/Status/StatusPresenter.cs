using System.Globalization;
using KalTerm.Contracts;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Status;

/// <summary>How worried a line should look.</summary>
internal enum Severity
{
    Normal,
    Warning,
    Error,
}

/// <summary>One line of the status page: a label, what it says, and how it should look.</summary>
internal sealed record StatusLine(string Label, string Text, Severity Severity = Severity.Normal);

/// <summary>The status page as text: problems first, then a line per area. The same content as <c>kterm status</c>.</summary>
internal sealed record StatusPage(string Heading, IReadOnlyList<string> Problems, IReadOnlyList<StatusLine> Lines, string GapNote);

/// <summary>Turns the server's health report into what the status page shows. Pure: no screen, no clock.</summary>
internal static class StatusPresenter
{
    public static StatusPage Present(StatusDto status, Formatting format)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(format);
        var lines = new List<StatusLine>
        {
            Ingest(status.Ingest, format),
            Streams(status.Streams),
            Storage(status.Storage),
            Retention(status.Storage),
            new("Markets", MarketCounts(status.Markets)),
            Books(status.Books),
            Backup(status.Backup),
            Analytics(status.Analytics),
            Gaps(status.Gaps),
        };
        return new StatusPage($"Server status {format.MomentWithZone(status.Time)}", status.Problems, lines, status.Gaps.Note);
    }

    private static StatusLine Ingest(IngestStateDto ingest, Formatting format) => new(
        "Ingest",
        $"{ingest.State}  last heartbeat {Formatting.Age(ingest.HeartbeatAgeSeconds)} ({format.MomentWithZone(ingest.LastHeartbeat)})",
        ingest.State switch { "running" => Severity.Normal, "stopped" => Severity.Error, _ => Severity.Warning });

    private static StatusLine Streams(StreamsDto streams) => new(
        "Streams",
        $"ticker {Stream(streams.Ticker)}  |  trade {Stream(streams.Trade)}",
        streams.Ticker.LastTime is null || streams.Trade.LastTime is null ? Severity.Error : Severity.Normal);

    private static string Stream(StreamDto stream) => stream.LastTime is null ? "none in 24 h" : Formatting.Age(stream.AgeSeconds);

    private static StatusLine Storage(StorageDto storage)
    {
        var cap = storage.MaxBytes is { } max ? $"{Formatting.Size(max)} ({Formatting.Percent(storage.UsedBytes, max)})" : "no cap";
        return new("Storage", $"database used {Formatting.Size(storage.UsedBytes)} of {cap}, file {Formatting.Size(storage.DataFileBytes)}  |  disk free {Formatting.Size(storage.DiskFreeBytes)}");
    }

    private static StatusLine Retention(StorageDto storage)
    {
        var governor = storage.GovernorLast is null ? "has not run" : $"{storage.GovernorLast} {Formatting.Age(storage.GovernorAgeSeconds)}";
        return new("Retention", $"oldest raw data {storage.OldestDataDay ?? Formatting.None}  |  governor {governor}", storage.GovernorLast?.StartsWith("error", StringComparison.Ordinal) == true ? Severity.Error : Severity.Normal);
    }

    private static string MarketCounts(IReadOnlyDictionary<string, long> byStatus) =>
        byStatus.Count == 0 ? "none" : string.Join("  |  ", byStatus.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value.ToString("N0", CultureInfo.InvariantCulture)}"));

    private static StatusLine Books(BooksStatusDto books)
    {
        if (books.Watched == 0)
        {
            return new("Books", "none watched");
        }

        var stale = books.Stale == 0 ? "none stale" : $"{books.Stale} stale";
        var approximate = books.Approximate == 0 ? string.Empty : $", {books.Approximate} approximate";
        return new("Books", $"{books.Watched} watched, {stale}{approximate}, published {Formatting.Age(books.PublishedAgeSeconds)}", books.Stale > 0 ? Severity.Error : books.Approximate > 0 ? Severity.Warning : Severity.Normal);
    }

    private static StatusLine Backup(BackupStatusDto? backup)
    {
        if (backup is null || !backup.Configured)
        {
            return new("Backup", "not configured");
        }

        var severity = Severity.Normal;
        var good = backup.LastGood is null ? "no finished backup yet" : $"last good {backup.LastGood}, {Formatting.Age(backup.LastGoodAgeSeconds)}";
        if (backup.LastGood is null)
        {
            severity = Severity.Error;
        }

        var failed = string.Empty;
        if (backup.Last is { } last && last.StartsWith("error", StringComparison.Ordinal) && (backup.LastGoodAt is null || string.CompareOrdinal(backup.LastAt, backup.LastGoodAt) >= 0))
        {
            failed = $"  |  last attempt failed: {last}";
            severity = Severity.Error;
        }

        var verify = string.Empty;
        if (backup.LastVerify is { } proof)
        {
            if (proof.StartsWith("error", StringComparison.Ordinal))
            {
                verify = $"  |  proof failed: {proof}";
                severity = Severity.Error;
            }
            else
            {
                verify = "  |  last proof by restore ok";
            }
        }

        return new("Backup", good + failed + verify, severity);
    }

    private static StatusLine Analytics(AnalyticsStatusDto? analytics)
    {
        if (analytics?.Last is null)
        {
            return new("Analytics", analytics is null ? "not reported" : "no run yet");
        }

        var failed = analytics.Last is "failed" or "timeout";
        var last = failed ? $"last run {analytics.Last}: {analytics.LastMessage}" : $"last run {analytics.Last}";
        var ok = analytics.LastOkAt is null
            ? "  |  no successful run yet"
            : $"  |  screener figures {Formatting.Age(analytics.LastOkAgeSeconds)}, {analytics.LastOkRows?.ToString("N0", CultureInfo.InvariantCulture) ?? Formatting.None} markets in {analytics.LastOkSeconds?.ToString(CultureInfo.InvariantCulture) ?? Formatting.None} s";
        return new("Analytics", last + ok, failed ? Severity.Error : Severity.Normal);
    }

    private static StatusLine Gaps(GapSummaryDto gaps)
    {
        var kinds = gaps.ByKind.Count == 0 ? string.Empty : "  (" + string.Join(", ", gaps.ByKind.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} {p.Value}")) + ")";
        var backfill = (gaps.BackfillPending, gaps.BackfillFailed) switch
        {
            (0, 0) => string.Empty,
            var (pending, failed) => $"  |  trade backfill: {pending} waiting{(failed > 0 ? $", {failed} failed" : string.Empty)}",
        };
        return new("Gaps", $"{gaps.Count} in the last {gaps.WindowHours} h{kinds}{backfill}", gaps.BackfillFailed > 0 ? Severity.Error : Severity.Normal);
    }
}
