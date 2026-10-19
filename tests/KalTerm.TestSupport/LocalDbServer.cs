using Microsoft.Data.SqlClient;

namespace KalTerm.TestSupport;

/// <summary>
/// The SQL Server LocalDB instance integration tests run against: on the CI runner and on a development machine that has LocalDB.
/// Each test class makes its own uniquely named database and drops it afterwards.
/// </summary>
public static class LocalDbServer
{
    /// <summary>The environment variable that points the tests at another SQL Server (for example <c>lpc:.</c> on the target server).</summary>
    public const string ServerVariable = "KALTERM_TEST_SQL_SERVER";

    /// <summary>The automatic default LocalDB instance (created on first connection).</summary>
    public const string DefaultServer = @"(localdb)\MSSQLLocalDB";

    private static readonly Lazy<Task<string?>> Probe = new(ProbeCoreAsync);

    /// <summary>True on the CI runner, where a missing LocalDB must fail the tests instead of skipping them.</summary>
    public static bool IsContinuousIntegration =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The server the tests use: <see cref="ServerVariable"/> when set, otherwise <see cref="DefaultServer"/>.</summary>
    public static string Server { get; } = Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } configured ? configured : DefaultServer;

    /// <summary>A connection string for <c>master</c> on the test server.</summary>
    public static string MasterConnectionString { get; } = new SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = "master",
        IntegratedSecurity = true,
        Encrypt = SqlConnectionEncryptOption.Optional,
        ConnectTimeout = 15,
    }.ConnectionString;

    /// <summary>Checks once per process whether LocalDB can be reached.</summary>
    /// <returns><see langword="null"/> when it can, otherwise the reason it cannot.</returns>
    public static Task<string?> ProbeAsync() => Probe.Value;

    /// <summary>A database name that no other test run uses.</summary>
    /// <returns>A new unique name.</returns>
    public static string NewDatabaseName() => "KalTermTest_" + Guid.NewGuid().ToString("N");

    /// <summary>Drops a test database if it exists, disconnecting anyone still using it.</summary>
    /// <param name="database">The database name.</param>
    /// <returns>A task that completes when the database is gone.</returns>
    public static async Task DropAsync(string database)
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var name = "[" + database.Replace("]", "]]", StringComparison.Ordinal) + "]";
        var sql = $"IF DB_ID(@n) IS NOT NULL BEGIN ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {name}; END";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        command.Parameters.AddWithValue("@n", database);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<string?> ProbeCoreAsync()
    {
        try
        {
            await using var connection = new SqlConnection(MasterConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            return null;
        }
        catch (SqlException ex)
        {
            return "LocalDB is not available: " + ex.Message;
        }
    }
}
