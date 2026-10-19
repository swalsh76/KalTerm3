namespace KalTerm.Backup;

/// <summary>How many backups of each generation are kept (grandfather-father-son).</summary>
public sealed record GfsOptions
{
    /// <summary>Sons: differential backups newer than this many days are kept (if their full backup is kept). Default 7.</summary>
    public int Daily { get; init; } = 7;

    /// <summary>Fathers: the newest full backup of each of this many ISO weeks that have one. Default 4.</summary>
    public int Weekly { get; init; } = 4;

    /// <summary>Grandfathers: the newest full backup of each of this many calendar months that have one. Default 12.</summary>
    public int Monthly { get; init; } = 12;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if a value cannot work.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Daily, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Weekly, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(Monthly, 1);
    }
}

/// <summary>A backup as retention sees it.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Kind">Full or differential.</param>
/// <param name="At">When it started (UTC).</param>
/// <param name="BaseFull">For a differential, its full backup's name.</param>
public sealed record BackupEntry(string Name, BackupKind Kind, DateTime At, string? BaseFull);

/// <summary>Decides which backups to keep. Pure, so it can be tested without a disk.</summary>
public static class GfsRetention
{
    /// <summary>
    /// The names to keep. The newest full backup is always kept. A differential is kept only while it is newer than <see cref="GfsOptions.Daily"/> days <b>and</b> the full backup it
    /// builds on is kept, since it cannot be restored without it. Everything not returned may be deleted.
    /// </summary>
    /// <param name="entries">Every backup on the target.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="options">The generations to keep.</param>
    /// <returns>The names to keep.</returns>
    public static IReadOnlySet<string> Keep(IReadOnlyCollection<BackupEntry> entries, DateTime now, GfsOptions options)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var fulls = entries.Where(e => e.Kind == BackupKind.Full).OrderByDescending(e => e.At).ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (fulls.Count > 0)
        {
            keep.Add(fulls[0].Name);
        }

        foreach (var week in fulls.GroupBy(f => (System.Globalization.ISOWeek.GetYear(f.At), System.Globalization.ISOWeek.GetWeekOfYear(f.At))).Take(options.Weekly))
        {
            keep.Add(week.First().Name);
        }

        foreach (var month in fulls.GroupBy(f => (f.At.Year, f.At.Month)).Take(options.Monthly))
        {
            keep.Add(month.First().Name);
        }

        var horizon = now.AddDays(-options.Daily);
        foreach (var diff in entries.Where(e => e.Kind == BackupKind.Differential && e.At > horizon && e.BaseFull is not null && keep.Contains(e.BaseFull)))
        {
            keep.Add(diff.Name);
        }

        return keep;
    }
}
