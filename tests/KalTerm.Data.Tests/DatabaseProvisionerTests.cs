namespace KalTerm.Data.Tests;

public sealed class DatabaseProvisionerTests : IAsyncLifetime
{
    private const long Megabyte = 1024 * 1024;

    private readonly DatabaseConnectionOptions _options = new()
    {
        Server = LocalDbServer.Server,
        Database = LocalDbServer.NewDatabaseName(),
    };


    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (await LocalDbServer.ProbeAsync() is null)
        {
            await LocalDbServer.DropAsync(_options.Database);
        }
    }

    [Fact]
    public async Task CreatesTheDatabaseWithTheDurabilityAndSizeSettings()
    {
        await Requirement.RequireLocalDbAsync();

        var result = await DatabaseProvisioner.EnsureAsync(_options, 256 * Megabyte, TestContext.Current.CancellationToken);
        var settings = await DatabaseProvisioner.ReadSettingsAsync(_options, TestContext.Current.CancellationToken);

        Assert.True(result.Created);
        Assert.NotNull(settings);
        Assert.Equal("DISABLED", settings.DelayedDurability);
        Assert.Equal("CHECKSUM", settings.PageVerify);
        Assert.Equal("SIMPLE", settings.Recovery);
        Assert.Equal(256 * Megabyte, settings.MaxSizeBytes);
    }

    [Fact]
    public async Task LeavesAutoCloseOff()
    {
        await Requirement.RequireLocalDbAsync();

        await DatabaseProvisioner.EnsureAsync(_options, 256 * Megabyte, TestContext.Current.CancellationToken);

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_options.MasterConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("SELECT is_auto_close_on FROM sys.databases WHERE name = @n", connection);
        command.Parameters.AddWithValue("@n", _options.Database);
        Assert.Equal(false, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunningAgainChangesNothing()
    {
        await Requirement.RequireLocalDbAsync();
        await DatabaseProvisioner.EnsureAsync(_options, 256 * Megabyte, TestContext.Current.CancellationToken);

        var again = await DatabaseProvisioner.EnsureAsync(_options, 256 * Megabyte, TestContext.Current.CancellationToken);

        Assert.False(again.Created);
        Assert.Empty(again.Changes);
    }

    [Fact]
    public async Task CorrectsDriftInEverySetting()
    {
        await Requirement.RequireLocalDbAsync();
        await DatabaseProvisioner.EnsureAsync(_options, 256 * Megabyte, TestContext.Current.CancellationToken);
        var name = _options.Database;
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(_options.MasterConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var settings = await DatabaseProvisioner.ReadSettingsAsync(_options, TestContext.Current.CancellationToken);
            var drift = $"""
                ALTER DATABASE [{name}] SET DELAYED_DURABILITY = FORCED;
                ALTER DATABASE [{name}] SET PAGE_VERIFY NONE;
                ALTER DATABASE [{name}] SET RECOVERY FULL;
                ALTER DATABASE [{name}] SET AUTO_CLOSE ON;
                ALTER DATABASE [{name}] MODIFY FILE (NAME = [{settings!.DataFileName}], MAXSIZE = UNLIMITED);
                """;
            await using var command = new Microsoft.Data.SqlClient.SqlCommand(drift, connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var fixedUp = await DatabaseProvisioner.EnsureAsync(_options, 256 * Megabyte, TestContext.Current.CancellationToken);
        var after = await DatabaseProvisioner.ReadSettingsAsync(_options, TestContext.Current.CancellationToken);

        Assert.Equal(5, fixedUp.Changes.Count);
        Assert.Contains(fixedUp.Changes, c => c.Contains("AUTO_CLOSE", StringComparison.Ordinal));
        Assert.Contains(fixedUp.Changes, c => c.Contains("unlimited", StringComparison.Ordinal));
        Assert.Equal(new DatabaseSettings("DISABLED", "CHECKSUM", "SIMPLE", after!.DataFileName, 256 * Megabyte), after);
    }

    [Fact]
    public async Task RoundsTheMaximumUpToAWholeMegabyte()
    {
        await Requirement.RequireLocalDbAsync();

        await DatabaseProvisioner.EnsureAsync(_options, (256 * Megabyte) + 1, TestContext.Current.CancellationToken);
        var settings = await DatabaseProvisioner.ReadSettingsAsync(_options, TestContext.Current.CancellationToken);

        Assert.Equal(257 * Megabyte, settings!.MaxSizeBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RefusesWithoutAPositiveMaximum(long maxSizeBytes)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DatabaseProvisioner.EnsureAsync(_options, maxSizeBytes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadSettingsOfAMissingDatabaseIsNull()
    {
        await Requirement.RequireLocalDbAsync();

        Assert.Null(await DatabaseProvisioner.ReadSettingsAsync(_options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NullOptionsAreRefused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => DatabaseProvisioner.EnsureAsync(null!, Megabyte, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DatabaseProvisioner.ReadSettingsAsync(null!, TestContext.Current.CancellationToken));
    }
}
