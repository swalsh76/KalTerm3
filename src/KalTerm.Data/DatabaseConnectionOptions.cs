using Microsoft.Data.SqlClient;

namespace KalTerm.Data;

/// <summary>Where the KalTerm database lives and how to reach it. The server never listens on TCP; the default is shared memory.</summary>
public sealed record DatabaseConnectionOptions
{
    /// <summary>The default server: the local default instance over shared memory.</summary>
    public const string SharedMemoryServer = "lpc:.";

    /// <summary>The default database name.</summary>
    public const string DefaultDatabase = "KalTerm";

    /// <summary>The <c>Data Source</c>: <c>lpc:.</c> (shared memory, the default), <c>lpc:.\NAME</c> for a named instance, or <c>(localdb)\NAME</c>.</summary>
    public string Server { get; init; } = SharedMemoryServer;

    /// <summary>The database name.</summary>
    public string Database { get; init; } = DefaultDatabase;

    /// <summary>The application name SQL Server shows for the connection.</summary>
    public string ApplicationName { get; init; } = "KalTerm";

    /// <summary>The connection string for <see cref="Database"/>. Always Windows authentication; no password is ever configured.</summary>
    public string ConnectionString => Build(Database);

    /// <summary>The connection string for <c>master</c>, used to create the database.</summary>
    public string MasterConnectionString => Build("master");

    private string Build(string database)
    {
        if (string.IsNullOrWhiteSpace(Server))
        {
            throw new InvalidOperationException("The database server is not configured.");
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException("The database name is not configured.");
        }

        return new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            ApplicationName = ApplicationName,
            Encrypt = SqlConnectionEncryptOption.Optional,
            Pooling = true,
            ConnectTimeout = 15,
        }.ConnectionString;
    }
}
