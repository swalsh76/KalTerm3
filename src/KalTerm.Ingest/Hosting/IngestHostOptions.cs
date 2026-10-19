using KalTerm.Data;

namespace KalTerm.Ingest.Hosting;

/// <summary>What the ingest process reads from its configuration (the <c>Ingest</c> section of <c>appsettings.json</c> or <c>KALTERM_Ingest__*</c> variables).</summary>
public sealed class IngestHostOptions
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

        if (PartitionDaysAhead < 1)
        {
            throw new InvalidOperationException("Ingest:PartitionDaysAhead must be at least 1");
        }
    }
}
