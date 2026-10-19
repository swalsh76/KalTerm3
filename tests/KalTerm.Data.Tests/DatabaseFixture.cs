using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Tests;

/// <summary>
/// One provisioned and migrated LocalDB database per test class. Skips the tests when LocalDB is missing on a development
/// machine; fails them on CI, where it must exist.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public const long MaxSizeBytes = 256L * 1024 * 1024;

    private string? _unavailable;

    public DatabaseConnectionOptions Options { get; } = new()
    {
        Server = LocalDbServer.Server,
        Database = LocalDbServer.NewDatabaseName(),
    };

    public async ValueTask InitializeAsync()
    {
        _unavailable = await LocalDbServer.ProbeAsync();
        if (_unavailable is not null)
        {
            return;
        }

        await DatabaseProvisioner.EnsureAsync(Options, MaxSizeBytes);
        new SchemaMigrator().Migrate(Options.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_unavailable is null)
        {
            await LocalDbServer.DropAsync(Options.Database);
        }
    }

    /// <summary>Call first in every test that needs the database.</summary>
    public void Require() => Requirement.Check(_unavailable);

    public async Task<SqlConnection> OpenAsync() => await new DataSource(Options).OpenAsync();

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Turns "LocalDB is missing" into a skip locally and a failure on CI.</summary>
public static class Requirement
{
    public static async Task RequireLocalDbAsync() => Check(await LocalDbServer.ProbeAsync());

    public static void Check(string? unavailable)
    {
        if (unavailable is null)
        {
            return;
        }

        if (LocalDbServer.IsContinuousIntegration)
        {
            Assert.Fail(unavailable);
        }

        Assert.Skip(unavailable);
    }
}
