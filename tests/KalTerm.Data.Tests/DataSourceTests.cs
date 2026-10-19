using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Tests;

public class DataSourceTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task OpensAConnectionToTheConfiguredDatabase()
    {
        db.Require();

        await using var connection = await new DataSource(db.Options).OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Equal(db.Options.Database, connection.Database);
    }

    [Fact]
    public async Task ExposesItsOptions()
    {
        await Task.CompletedTask;

        Assert.Same(db.Options, new DataSource(db.Options).Options);
    }

    [Fact]
    public async Task AFailedOpenThrowsSqlException()
    {
        await Requirement.RequireLocalDbAsync();
        var missing = new DatabaseConnectionOptions { Server = LocalDbServer.Server, Database = LocalDbServer.NewDatabaseName() };

        await Assert.ThrowsAsync<SqlException>(() => new DataSource(missing).OpenAsync(TestContext.Current.CancellationToken));
    }
}
