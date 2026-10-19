using System.Globalization;

namespace KalTerm.Backup;

/// <summary>Builds <see cref="BackupOptions"/> from the plain settings the ingest host and the admin tool share (<c>Ingest:Backup*</c> / <c>KALTERM_Ingest__Backup*</c>).</summary>
public static class BackupSettings
{
    /// <summary>Where the share credential is kept unless told otherwise.</summary>
    public const string DefaultCredentialPath = @"C:\KalTerm\secrets\backup-share.cred";

    /// <summary>The default time of day (UTC) from which the daily backup runs.</summary>
    public const string DefaultRunAt = "08:00";

    /// <summary>Reads a time of day such as <c>08:00</c>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="time">The time.</param>
    /// <returns>Whether it was valid.</returns>
    public static bool TryParseRunAt(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>The options for the given settings.</summary>
    /// <param name="target">The backup target folder; empty means backups are off.</param>
    /// <param name="staging">The staging folder; <see langword="null"/> for SQL Server's own default.</param>
    /// <param name="daily">Days of differentials kept.</param>
    /// <param name="weekly">Weeks of full backups kept.</param>
    /// <param name="monthly">Months of full backups kept.</param>
    /// <param name="runAt">The time of day (UTC), for example <c>08:00</c>.</param>
    /// <returns>The options, or <see langword="null"/> when no target is set.</returns>
    /// <exception cref="InvalidOperationException">A setting cannot work.</exception>
    public static BackupOptions? Create(string? target, string? staging, int daily, int weekly, int monthly, string? runAt)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        if (!TryParseRunAt(runAt ?? DefaultRunAt, out var at))
        {
            throw new InvalidOperationException("the backup time must look like 08:00 (UTC)");
        }

        var options = new BackupOptions
        {
            TargetPath = target.Trim(),
            StagingPath = string.IsNullOrWhiteSpace(staging) ? null : staging.Trim(),
            RunAt = at,
            Retention = new GfsOptions { Daily = daily, Weekly = weekly, Monthly = monthly },
        };
        try
        {
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("the backup settings cannot work: " + ex.Message, ex);
        }

        return options;
    }
}
