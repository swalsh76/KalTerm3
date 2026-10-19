using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace KalTerm.Data.Tests;

public class SchemaTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task CreatesTheSixTablesAndTheJournal()
    {
        db.Require();

        var tables = new List<string>();
        await using var connection = await db.OpenAsync();
        await using var command = new SqlCommand("SELECT name FROM sys.tables ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(["ingest_gap", "ingest_state", "market", "orderbook_state", "SchemaVersions", "ticker", "trade"], tables);
    }

    [Fact]
    public async Task AppliedScriptIsRecordedAndARerunAppliesNothing()
    {
        db.Require();

        var again = new SchemaMigrator().Migrate(db.Options.ConnectionString);
        var journal = await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SchemaVersions");

        Assert.Empty(again);
        Assert.Equal(2, journal);
    }

    [Theory]
    [InlineData("ticker")]
    [InlineData("trade")]
    public async Task TimeSeriesTablesAreAnOrderedColumnstoreOnTheDayScheme(string table)
    {
        db.Require();

        var scheme = await db.ScalarAsync<string>($"SELECT ds.name FROM sys.indexes i JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id WHERE i.object_id = OBJECT_ID('dbo.{table}') AND i.type = 5");
        var orderedColumns = await db.ScalarAsync<string>($"SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.column_store_order_ordinal) FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = OBJECT_ID('dbo.{table}') AND ic.column_store_order_ordinal > 0");
        var escalation = await db.ScalarAsync<string>($"SELECT lock_escalation_desc FROM sys.tables WHERE name = '{table}'");

        Assert.Equal("ps_day", scheme);
        Assert.Equal("market_id,ts", orderedColumns);
        Assert.Equal("AUTO", escalation);
    }

    [Fact]
    public async Task TimeSeriesTablesHaveNoForeignKeysAndNoUniqueConstraints()
    {
        db.Require();

        var constraints = await db.ScalarAsync<int>("SELECT (SELECT COUNT(*) FROM sys.foreign_keys) + (SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id IN (OBJECT_ID('dbo.ticker'), OBJECT_ID('dbo.trade')))");

        Assert.Equal(0, constraints);
    }

    [Fact]
    public async Task MoneyAndCountColumnsAreBigintAndNamedForTheirScale()
    {
        db.Require();

        var wrong = await db.ScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id IN (OBJECT_ID('dbo.market'), OBJECT_ID('dbo.ticker'), OBJECT_ID('dbo.trade'))
              AND (c.name LIKE '%[_]e6' OR c.name LIKE '%[_]e2') AND t.name <> 'bigint'
            """);
        var scaled = await db.ScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.columns c
            WHERE c.object_id IN (OBJECT_ID('dbo.market'), OBJECT_ID('dbo.ticker'), OBJECT_ID('dbo.trade'))
              AND (c.name LIKE '%[_]e6' OR c.name LIKE '%[_]e2')
            """);

        Assert.Equal(0, wrong);
        Assert.Equal(12, scaled);
    }

    [Fact]
    public async Task FixedPointExtremesRoundTripExactly()
    {
        db.Require();

        await db.ExecuteAsync($"""
            INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, yes_ask_e6, yes_bid_size_e2, yes_ask_size_e2, volume_e2, open_interest_e2, last_trade_size_e2)
            VALUES ('2026-01-01T00:00:00.123', 1, {long.MaxValue}, {long.MinValue}, 0, 1, -1, {long.MaxValue}, {long.MinValue}, NULL);
            INSERT dbo.trade (ts, market_id, trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block)
            VALUES ('2026-01-01T00:00:00.456', 1, 'a-b', {long.MaxValue}, 1, {long.MaxValue}, 2, 1);
            """);

        await using var connection = await db.OpenAsync();
        await using var tickerCommand = new SqlCommand("SELECT price_e6, yes_bid_e6, volume_e2, open_interest_e2, last_trade_size_e2 FROM dbo.ticker WHERE market_id = 1", connection);
        await using (var reader = await tickerCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(long.MaxValue, reader.GetInt64(0));
            Assert.Equal(long.MinValue, reader.GetInt64(1));
            Assert.Equal(long.MaxValue, reader.GetInt64(2));
            Assert.Equal(long.MinValue, reader.GetInt64(3));
            Assert.True(reader.IsDBNull(4));
        }

        await using var tradeCommand = new SqlCommand("SELECT yes_price_e6, count_e2 FROM dbo.trade WHERE market_id = 1", connection);
        await using var tradeReader = await tradeCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await tradeReader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(long.MaxValue, tradeReader.GetInt64(0));
        Assert.Equal(long.MaxValue, tradeReader.GetInt64(1));
    }

    [Fact]
    public async Task MarketTickerIsUnique()
    {
        db.Require();
        const string insert = "INSERT dbo.market (ticker, event_ticker, market_type, status, first_seen_at, last_seen_at) VALUES ('UNIQ-1', 'E', 'binary', 'open', SYSUTCDATETIME(), SYSUTCDATETIME())";
        await db.ExecuteAsync(insert);

        var ex = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync(insert));

        Assert.Equal(2627, ex.Number);
    }

    [Fact]
    public async Task AFailingMigrationThrowsAndNamesTheCause()
    {
        await Requirement.RequireLocalDbAsync();
        var missing = new DatabaseConnectionOptions { Server = LocalDbServer.Server, Database = LocalDbServer.NewDatabaseName() };

        var ex = Assert.Throws<SchemaMigrationException>(() => new SchemaMigrator().Migrate(missing.ConnectionString));

        Assert.StartsWith("The schema migration failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigratorRefusesABlankConnectionString()
    {
        Assert.Throws<ArgumentException>(() => new SchemaMigrator().Migrate(" "));
    }

    [Fact]
    public async Task MigratorLogsWhatItDoes()
    {
        await Requirement.RequireLocalDbAsync();
        var name = LocalDbServer.NewDatabaseName();
        var options = new DatabaseConnectionOptions { Server = LocalDbServer.Server, Database = name };
        var logger = new ListLogger();
        try
        {
            await DatabaseProvisioner.EnsureAsync(options, DatabaseFixture.MaxSizeBytes, TestContext.Current.CancellationToken);

            new SchemaMigrator(logger).Migrate(options.ConnectionString);

            Assert.Contains(logger.Messages, m => m.Contains("0001_schema_v1", StringComparison.Ordinal));
        }
        finally
        {
            await LocalDbServer.DropAsync(name);
        }
    }

    [Fact]
    public async Task MigratorLogsAFailureAtErrorLevel()
    {
        await Requirement.RequireLocalDbAsync();
        var missing = new DatabaseConnectionOptions { Server = LocalDbServer.Server, Database = LocalDbServer.NewDatabaseName() };
        var logger = new ListLogger();

        Assert.Throws<SchemaMigrationException>(() => new SchemaMigrator(logger).Migrate(missing.ConnectionString));

        Assert.Contains(logger.Levels, l => l == LogLevel.Error);
    }
}
