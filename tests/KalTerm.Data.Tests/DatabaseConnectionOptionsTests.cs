using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Tests;

public class DatabaseConnectionOptionsTests
{
    [Fact]
    public void DefaultsToSharedMemoryAndTheKalTermDatabase()
    {
        var options = new DatabaseConnectionOptions();
        var builder = new SqlConnectionStringBuilder(options.ConnectionString);

        Assert.Equal("lpc:.", builder.DataSource);
        Assert.Equal("KalTerm", builder.InitialCatalog);
    }

    [Fact]
    public void AlwaysUsesWindowsAuthenticationWithNoPassword()
    {
        var builder = new SqlConnectionStringBuilder(new DatabaseConnectionOptions().ConnectionString);

        Assert.True(builder.IntegratedSecurity);
        Assert.Equal(string.Empty, builder.Password);
        Assert.Equal(string.Empty, builder.UserID);
    }

    [Fact]
    public void MasterConnectionStringTargetsMasterOnTheSameServer()
    {
        var options = new DatabaseConnectionOptions { Server = @"(localdb)\Other", Database = "Mine" };
        var builder = new SqlConnectionStringBuilder(options.MasterConnectionString);

        Assert.Equal(@"(localdb)\Other", builder.DataSource);
        Assert.Equal("master", builder.InitialCatalog);
    }

    [Theory]
    [InlineData("", "KalTerm")]
    [InlineData(" ", "KalTerm")]
    [InlineData("lpc:.", "")]
    public void BlankServerOrDatabaseIsRefused(string server, string database)
    {
        var options = new DatabaseConnectionOptions { Server = server, Database = database };

        Assert.Throws<InvalidOperationException>(() => options.ConnectionString);
    }
}
