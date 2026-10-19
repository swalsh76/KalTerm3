using KalTerm.Data.Writing;
using Microsoft.Data.SqlClient;
using static KalTerm.Data.Tests.Writing.WriterTestKit;

namespace KalTerm.Data.Tests.Writing;

public sealed class WriterDatabaseTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private static readonly DateOnly TestDay = new(2026, 6, 1);
    private static readonly DateOnly PartitionDay = new(2026, 8, 1);

    /// <summary>Boundaries before any row, as the host does at start-up: a populated last partition can no longer be split.</summary>
    public async ValueTask InitializeAsync()
    {
        db.Require();
        await using var connection = await db.OpenAsync();
        await PartitionManager.EnsureBoundariesAsync(connection, TestDay.AddDays(-1), TestDay.AddDays(2), TestContext.Current.CancellationToken);
        await PartitionManager.EnsureBoundariesAsync(connection, PartitionDay, PartitionDay.AddDays(2), TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static BulkWriterOptions Quick(int flushRows = 3) => new()
    {
        FlushRows = flushRows,
        FlushInterval = TimeSpan.FromMilliseconds(50),
        InitialBackoff = TimeSpan.FromMilliseconds(20),
        MaxBackoff = TimeSpan.FromMilliseconds(50),
        ShutdownTimeout = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public async Task TickerRowsRoundTripIncludingNullsAndExtremes()
    {
        db.Require();
        var market = 1001;
        await using var writer = Writers.Ticker(new DataSource(db.Options), Quick(2));
        writer.Start();

        writer.TryWrite(new TickerRow(Epoch.AddMilliseconds(123), market, long.MaxValue, long.MinValue, 0, 1, -1, long.MaxValue, long.MinValue, 7));
        writer.TryWrite(new TickerRow(Epoch.AddMilliseconds(124), market));
        await WaitUntilAsync(() => writer.Stats.Written == 2);

        await using var connection = await db.OpenAsync();
        await using var command = new SqlCommand("SELECT ts, price_e6, yes_bid_e6, yes_ask_e6, yes_bid_size_e2, yes_ask_size_e2, volume_e2, open_interest_e2, last_trade_size_e2 FROM dbo.ticker WHERE market_id = 1001 ORDER BY ts", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Epoch.AddMilliseconds(123), reader.GetDateTime(0));
        Assert.Equal(long.MaxValue, reader.GetInt64(1));
        Assert.Equal(long.MinValue, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
        Assert.Equal(1L, reader.GetInt64(4));
        Assert.Equal(-1L, reader.GetInt64(5));
        Assert.Equal(long.MaxValue, reader.GetInt64(6));
        Assert.Equal(long.MinValue, reader.GetInt64(7));
        Assert.Equal(7L, reader.GetInt64(8));
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        for (var column = 1; column <= 8; column++)
        {
            Assert.True(reader.IsDBNull(column));
        }
    }

    [Fact]
    public async Task TradeRowsRoundTrip()
    {
        db.Require();
        await using var writer = Writers.Trade(new DataSource(db.Options), Quick(1));
        writer.Start();

        writer.TryWrite(new TradeRow(Epoch, 1002, "trade-abc-1", 650_000, 350_000, 1_250, TakerSide.No, true));
        await WaitUntilAsync(() => writer.Stats.Written == 1);

        await using var connection = await db.OpenAsync();
        await using var command = new SqlCommand("SELECT trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block FROM dbo.trade WHERE market_id = 1002", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("trade-abc-1", reader.GetString(0));
        Assert.Equal(650_000L, reader.GetInt64(1));
        Assert.Equal(350_000L, reader.GetInt64(2));
        Assert.Equal(1_250L, reader.GetInt64(3));
        Assert.Equal((byte)2, reader.GetByte(4));
        Assert.True(reader.GetBoolean(5));
    }

    [Fact]
    public async Task RowsOnTwoDaysLandInTwoPartitions()
    {
        db.Require();
        var day = PartitionDay;
        await using var writer = Writers.Ticker(new DataSource(db.Options), Quick(4));
        writer.Start();
        var first = day.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc);
        writer.TryWrite(new TickerRow(first, 1003, 1));
        writer.TryWrite(new TickerRow(first.AddSeconds(2), 1003, 2));
        writer.TryWrite(new TickerRow(first.AddSeconds(3), 1003, 3));
        writer.TryWrite(new TickerRow(first.AddHours(1), 1003, 4));
        await WaitUntilAsync(() => writer.Stats.Written == 4);

        var partitions = await db.ScalarAsync<int>("SELECT COUNT(DISTINCT $PARTITION.pf_day(ts)) FROM dbo.ticker WHERE market_id = 1003");
        Assert.Equal(2, partitions);
    }

    [Fact]
    public async Task ABatchWithOneBadRowLeavesNothingBehindAndIsDroppedOnce()
    {
        db.Require();
        await using var writer = Writers.Trade(new DataSource(db.Options), Quick(3));
        writer.Start();

        writer.TryWrite(new TradeRow(Epoch, 1004, "ok-1", 1, 1, 1, TakerSide.Yes, false));
        writer.TryWrite(new TradeRow(Epoch.AddSeconds(1), 1004, "ok-2", 1, 1, 1, TakerSide.Yes, false));
        writer.TryWrite(new TradeRow(Epoch.AddSeconds(2), 1004, new string('x', 65), 1, 1, 1, TakerSide.Yes, false));
        await WaitUntilAsync(() => writer.Stats.PoisonBatches == 1);

        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.trade WHERE market_id = 1004"));
        Assert.Equal(3, writer.Stats.Dropped);
        Assert.Equal(0, writer.Stats.Retries);
        var range = writer.TakeDroppedRange();
        Assert.Equal(new DroppedRange(Epoch, Epoch.AddSeconds(2), 3), range);
    }

    [Fact]
    public async Task ARealConnectionFailureIsRetriedNotDropped()
    {
        await Requirement.RequireLocalDbAsync();
        var missing = new DatabaseConnectionOptions { Server = LocalDbServer.Server, Database = LocalDbServer.NewDatabaseName() };
        var writer = Writers.Ticker(new DataSource(missing), Quick(1));
        writer.Start();

        writer.TryWrite(new TickerRow(Epoch, 1));
        await WaitUntilAsync(() => writer.Stats.Retries >= 2);
        Assert.True(writer.Stats.Retries >= 2, "not retried: " + writer.Stats.LastError);

        Assert.Equal(0, writer.Stats.PoisonBatches);
        Assert.Equal(0, writer.Stats.Written);
        await writer.DisposeAsync();
        Assert.Equal(1, writer.Stats.Dropped);
    }

    [Fact]
    public async Task AnEmptyBatchWritesNothing()
    {
        await Requirement.RequireLocalDbAsync();
        using var sink = new SqlBulkSink<TickerRow>(new DataSource(db.Options), TickerRowMapper.Instance);

        await sink.WriteAsync([], TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ASinkRefusesNullsAndAMapperWithMismatchedColumns()
    {
        var source = new DataSource(db.Options);
        Assert.Throws<ArgumentNullException>(() => new SqlBulkSink<TickerRow>(null!, TickerRowMapper.Instance));
        Assert.Throws<ArgumentNullException>(() => new SqlBulkSink<TickerRow>(source, null!));
        Assert.Throws<ArgumentException>(() => new SqlBulkSink<TickerRow>(source, new BrokenMapper()));
    }

    [Fact]
    public async Task ASinkRefusesANullBatch()
    {
        using var sink = new SqlBulkSink<TickerRow>(new DataSource(db.Options), TickerRowMapper.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sink.WriteAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MappersRefuseAColumnOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TickerRowMapper.Instance.GetValue(new TickerRow(Epoch, 1), 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => TradeRowMapper.Instance.GetValue(new TradeRow(Epoch, 1, "t", 1, 1, 1, TakerSide.Unknown, false), 8));
    }

    [Fact]
    public void MappersDescribeTheirTablesAndTimestamps()
    {
        var trade = new TradeRow(Epoch, 1, "t", 1, 1, 1, TakerSide.Unknown, false);

        Assert.Equal("dbo.ticker", TickerRowMapper.Instance.Table);
        Assert.Equal("dbo.trade", TradeRowMapper.Instance.Table);
        Assert.Equal(Epoch, TradeRowMapper.Instance.Timestamp(trade));
        Assert.Equal(TickerRowMapper.Instance.Columns.Count, TickerRowMapper.Instance.ColumnTypes.Count);
        Assert.Equal(TradeRowMapper.Instance.Columns.Count, TradeRowMapper.Instance.ColumnTypes.Count);
    }

    private sealed class BrokenMapper : IRowMapper<TickerRow>
    {
        public string Table => "dbo.ticker";

        public IReadOnlyList<string> Columns { get; } = ["ts", "market_id"];

        public IReadOnlyList<Type> ColumnTypes { get; } = [typeof(DateTime)];

        public object? GetValue(TickerRow row, int column) => null;

        public DateTime Timestamp(TickerRow row) => row.Ts;
    }
}
