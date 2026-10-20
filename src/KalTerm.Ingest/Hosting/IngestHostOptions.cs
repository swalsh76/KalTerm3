using System.Text.RegularExpressions;
using KalTerm.Data;

namespace KalTerm.Ingest.Hosting;

/// <summary>What the ingest process reads from its configuration (the <c>Ingest</c> section of <c>appsettings.json</c> or <c>KALTERM_Ingest__*</c> variables).</summary>
public sealed partial class IngestHostOptions
{
    /// <summary>The name of the configuration section.</summary>
    public const string SectionName = "Ingest";

    /// <summary>The SQL Server to use; shared memory on this machine by default. Windows authentication only.</summary>
    public string Server { get; set; } = DatabaseConnectionOptions.SharedMemoryServer;

    /// <summary>The database name.</summary>
    public string Database { get; set; } = DatabaseConnectionOptions.DefaultDatabase;

    /// <summary>
    /// When set, a missing database is created with this hard maximum size for its data file, in gigabytes, and the durability and size settings of an
    /// existing one are checked and corrected. When not set, a missing database is an error: the process never creates storage unasked.
    /// </summary>
    public int? ProvisionMaxSizeGb { get; set; }

    /// <summary>How many days past today get a partition boundary.</summary>
    public int PartitionDaysAhead { get; set; } = 7;

    /// <summary>How many days of raw ticker and trade data are kept (<c>Ingest:RetentionDays</c>); older days are dropped whole once a day. Default 30; at least 4.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// The candle intervals that are stored and for which markets (<c>Ingest:Candles</c>), for example <c>1d=all;1h=all;1m=watched</c>. Empty stores none. Intervals: 1m, 5m, 15m, 1h, 4h, 1d;
    /// scopes: all, watched (the watch list). Adding one builds it from the raw rows still held; removing one stops new rows and leaves the old ones to retention.
    /// </summary>
    public string Candles { get; set; } = Data.Candles.CandleRules.DefaultText;

    /// <summary>The stored candle rules these settings describe.</summary>
    /// <returns>The rules.</returns>
    /// <exception cref="InvalidOperationException">The text is not a valid list of rules.</exception>
    public IReadOnlyList<Data.Candles.CandleRule> ToCandleRules()
    {
        try
        {
            return Data.Candles.CandleRules.Parse(Candles);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Ingest:Candles is not valid: " + ex.Message, ex);
        }
    }

    /// <summary>Whether the trades of closed gaps are recovered from REST (<c>Ingest:BackfillEnabled</c>). Default true.</summary>
    public bool BackfillEnabled { get; set; } = true;

    /// <summary>
    /// Where finished backups are kept (<c>Ingest:BackupTarget</c>): a folder, or <c>\\host\share\folder</c>. Empty means no backups. The folder must carry the marker file written by
    /// <c>kalterm-admin backup init-target</c>.
    /// </summary>
    public string? BackupTarget { get; set; }

    /// <summary>Where SQL Server writes a backup before it is copied to the target (<c>Ingest:BackupStaging</c>); empty for SQL Server's own default backup folder.</summary>
    public string? BackupStaging { get; set; }

    /// <summary>Where the protected share credential is kept (<c>Ingest:BackupCredentialPath</c>).</summary>
    public string BackupCredentialPath { get; set; } = Backup.BackupSettings.DefaultCredentialPath;

    /// <summary>Days of differential backups kept. Default 7.</summary>
    public int BackupDaily { get; set; } = 7;

    /// <summary>Weeks of full backups kept. Default 4.</summary>
    public int BackupWeekly { get; set; } = 4;

    /// <summary>Months of full backups kept. Default 12.</summary>
    public int BackupMonthly { get; set; } = 12;

    /// <summary>The time of day (UTC) from which the daily backup runs (<c>Ingest:BackupAt</c>). Default 08:00.</summary>
    public string BackupAt { get; set; } = Backup.BackupSettings.DefaultRunAt;

    /// <summary>The backup settings these options describe, or <see langword="null"/> when no target is set.</summary>
    /// <returns>The options.</returns>
    public Backup.BackupOptions? ToBackupOptions() => Backup.BackupSettings.Create(BackupTarget, BackupStaging, BackupDaily, BackupWeekly, BackupMonthly, BackupAt);

    /// <summary>
    /// The markets whose order books are built and published (<c>Ingest:WatchList:0</c>, <c>Ingest:WatchList:1</c>, ...). Empty means no order book subscription at
    /// all. Changing it needs a restart in M3.
    /// </summary>
    public string[] WatchList { get; set; } = [];

    /// <summary>The most markets on the watch list.</summary>
    public const int MaxWatchList = 100;

    /// <summary>The database connection options these settings describe.</summary>
    /// <returns>The options.</returns>
    public DatabaseConnectionOptions ToConnectionOptions() => new() { Server = Server, Database = Database, ApplicationName = "KalTerm.Ingest" };

    /// <summary>Throws <see cref="InvalidOperationException"/> if the settings cannot work.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server) || string.IsNullOrWhiteSpace(Database))
        {
            throw new InvalidOperationException("Ingest:Server and Ingest:Database must not be empty");
        }

        if (ProvisionMaxSizeGb is <= 0)
        {
            throw new InvalidOperationException("Ingest:ProvisionMaxSizeGb must be positive when set");
        }

        if (RetentionDays <= Data.Governor.StorageBudget.MinKeepDays)
        {
            throw new InvalidOperationException($"Ingest:RetentionDays must be more than {Data.Governor.StorageBudget.MinKeepDays}");
        }

        _ = ToBackupOptions();
        _ = ToCandleRules();   // throws InvalidOperationException when the backup settings cannot work

        if (PartitionDaysAhead < 1)
        {
            throw new InvalidOperationException("Ingest:PartitionDaysAhead must be at least 1");
        }

        if (WatchList.Length > MaxWatchList)
        {
            throw new InvalidOperationException($"Ingest:WatchList has {WatchList.Length} markets; the most is {MaxWatchList}");
        }

        foreach (var ticker in WatchList)
        {
            if (ticker is null || !TickerShape().IsMatch(ticker))
            {
                throw new InvalidOperationException($"Ingest:WatchList holds something that is not a market ticker: '{ticker}'");
            }
        }

        if (WatchList.Distinct(StringComparer.Ordinal).Count() != WatchList.Length)
        {
            throw new InvalidOperationException("Ingest:WatchList names a market twice");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex TickerShape();
}
