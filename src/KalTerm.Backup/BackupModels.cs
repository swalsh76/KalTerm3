using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KalTerm.Backup;

/// <summary>The two kinds of backup: a full copy of the database, or the changes since the last full one.</summary>
public enum BackupKind
{
    /// <summary>Everything.</summary>
    Full,

    /// <summary>The changes since the last full backup (the database uses the simple recovery model, so there are no log backups).</summary>
    Differential,
}

/// <summary>Row counts per UTC day, for the two time-series tables. A backup's manifest holds them from just before and just after the backup so a restored copy can be checked against it.</summary>
/// <param name="Ticker">Ticker rows by day (<c>yyyy-MM-dd</c>).</param>
/// <param name="Trade">Trade rows by day (<c>yyyy-MM-dd</c>).</param>
public sealed record DayCounts(IReadOnlyDictionary<string, long> Ticker, IReadOnlyDictionary<string, long> Trade);

/// <summary>Describes one backup file. The manifest is written last, so a backup without one is not a backup.</summary>
/// <param name="Version">The manifest format; 1.</param>
/// <param name="Name">The backup file's name.</param>
/// <param name="Kind">Full or differential.</param>
/// <param name="BaseFull">For a differential, the name of the full backup it builds on.</param>
/// <param name="StartedAt">When the backup began (UTC).</param>
/// <param name="FinishedAt">When it was safely in place (UTC).</param>
/// <param name="Database">The database that was backed up.</param>
/// <param name="SizeBytes">The file's size.</param>
/// <param name="Sha256">The file's SHA-256, upper-case hex.</param>
/// <param name="SqlVersion">The SQL Server version that made it.</param>
/// <param name="Before">Row counts just before the backup started.</param>
/// <param name="After">Row counts just after it finished.</param>
public sealed record BackupManifest(
    int Version,
    string Name,
    BackupKind Kind,
    string? BaseFull,
    DateTime StartedAt,
    DateTime FinishedAt,
    string Database,
    long SizeBytes,
    string Sha256,
    string SqlVersion,
    DayCounts Before,
    DayCounts After)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>The manifest as JSON text.</summary>
    /// <returns>The text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a manifest.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">The text is not a manifest.</exception>
    public static BackupManifest FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(json, Options) ?? throw new InvalidDataException("the manifest is empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("the manifest is not valid: " + ex.Message, ex);
        }
    }
}

/// <summary>How backup files are named: <c>kalterm-full-20261019-080000.bak</c> and <c>kalterm-diff-20261019-080000.bak</c>, each with a <c>.json</c> manifest. Only names in this form are ever deleted.</summary>
public static class BackupNaming
{
    private const string Stamp = "yyyyMMdd-HHmmss";

    /// <summary>The suffix of a backup file that is not finished.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>The file name for a backup.</summary>
    /// <param name="kind">Full or differential.</param>
    /// <param name="at">When it started (UTC).</param>
    /// <returns>The name.</returns>
    public static string FileName(BackupKind kind, DateTime at) =>
        string.Create(CultureInfo.InvariantCulture, $"kalterm-{(kind == BackupKind.Full ? "full" : "diff")}-{at.ToString(Stamp, CultureInfo.InvariantCulture)}.bak");

    /// <summary>The name of the manifest of a backup file.</summary>
    /// <param name="fileName">The backup file's name.</param>
    /// <returns>The manifest's name.</returns>
    public static string ManifestName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName + ".json";
    }

    /// <summary>Reads the kind and time from a backup file name.</summary>
    /// <param name="fileName">The file name, without a folder.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="at">The time (UTC).</param>
    /// <returns>Whether the name is one of ours.</returns>
    public static bool TryParse(string fileName, out BackupKind kind, out DateTime at)
    {
        kind = BackupKind.Full;
        at = default;
        if (fileName is null || !fileName.EndsWith(".bak", StringComparison.Ordinal))
        {
            return false;
        }

        BackupKind parsedKind;
        if (fileName.StartsWith("kalterm-full-", StringComparison.Ordinal))
        {
            parsedKind = BackupKind.Full;
        }
        else if (fileName.StartsWith("kalterm-diff-", StringComparison.Ordinal))
        {
            parsedKind = BackupKind.Differential;
        }
        else
        {
            return false;
        }

        var stamp = fileName["kalterm-full-".Length..^".bak".Length];
        if (!DateTime.TryParseExact(stamp, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at))
        {
            return false;
        }

        kind = parsedKind;
        return true;
    }
}

/// <summary>What the checks of a restored copy found.</summary>
/// <param name="Matches">Whether the copy agrees with the manifest.</param>
/// <param name="Differences">What does not agree, one line each.</param>
public sealed record CountComparison(bool Matches, IReadOnlyList<string> Differences);

/// <summary>Compares the row counts of a restored copy with a manifest.</summary>
public static class ManifestCheck
{
    /// <summary>
    /// The tables only grow while a backup runs (ingest and trade backfill append; retention drops whole old days), so for every day the restored count must lie between the
    /// count before the backup and the count after it, whichever is smaller and larger. A day that did not change must match exactly.
    /// </summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="restored">Counts taken from the restored copy.</param>
    /// <returns>The comparison.</returns>
    public static CountComparison Compare(BackupManifest manifest, DayCounts restored)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(restored);
        var differences = new List<string>();
        Compare("ticker", manifest.Before.Ticker, manifest.After.Ticker, restored.Ticker, differences);
        Compare("trade", manifest.Before.Trade, manifest.After.Trade, restored.Trade, differences);
        return new CountComparison(differences.Count == 0, differences);
    }

    private static void Compare(string table, IReadOnlyDictionary<string, long> before, IReadOnlyDictionary<string, long> after, IReadOnlyDictionary<string, long> restored, List<string> differences)
    {
        foreach (var day in before.Keys.Concat(after.Keys).Concat(restored.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var b = before.GetValueOrDefault(day);
            var a = after.GetValueOrDefault(day);
            var r = restored.GetValueOrDefault(day);
            if (r < Math.Min(a, b) || r > Math.Max(a, b))
            {
                differences.Add(string.Create(CultureInfo.InvariantCulture, $"{table} {day}: restored {r}, but the backup saw {b} before and {a} after"));
            }
        }
    }
}
