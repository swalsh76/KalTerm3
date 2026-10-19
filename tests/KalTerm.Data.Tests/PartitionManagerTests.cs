namespace KalTerm.Data.Tests;

public class PartitionManagerTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateOnly Day1 = new(2026, 3, 1);


    [Fact]
    public async Task AddsMissingDaysAndReportsThem()
    {
        db.Require();
        await using var connection = await db.OpenAsync();

        var added = await PartitionManager.EnsureBoundariesAsync(connection, Day1, Day1.AddDays(2), TestContext.Current.CancellationToken);
        var boundaries = await PartitionManager.GetBoundariesAsync(connection, TestContext.Current.CancellationToken);

        Assert.Equal([Day1, Day1.AddDays(1), Day1.AddDays(2)], added);
        Assert.Equal(added, boundaries.Where(d => d.Year == 2026 && d.Month == 3 && d.Day <= 3).ToList());
    }

    [Fact]
    public async Task EnsuringAgainAddsNothing()
    {
        db.Require();
        await using var connection = await db.OpenAsync();
        var from = new DateOnly(2026, 4, 1);
        await PartitionManager.EnsureBoundariesAsync(connection, from, from.AddDays(1), TestContext.Current.CancellationToken);

        var again = await PartitionManager.EnsureBoundariesAsync(connection, from, from.AddDays(1), TestContext.Current.CancellationToken);

        Assert.Empty(again);
    }

    [Fact]
    public async Task AReversedRangeIsRefused()
    {
        db.Require();
        await using var connection = await db.OpenAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PartitionManager.EnsureBoundariesAsync(connection, Day1.AddDays(1), Day1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TruncatingADayEmptiesOnlyThatDayInBothTables()
    {
        db.Require();
        await using var connection = await db.OpenAsync();
        var d1 = new DateOnly(2026, 5, 1);
        await PartitionManager.EnsureBoundariesAsync(connection, d1, d1.AddDays(2), TestContext.Current.CancellationToken);
        await db.ExecuteAsync("""
            INSERT dbo.ticker (ts, market_id, price_e6) VALUES
              ('2026-05-01T10:00:00', 7, 1), ('2026-05-01T23:59:59.999', 7, 2), ('2026-05-02T00:00:00', 7, 3), ('2026-05-03T12:00:00', 7, 4);
            INSERT dbo.trade (ts, market_id, trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block) VALUES
              ('2026-05-01T10:00:00', 7, 't1', 1, 1, 1, 1, 0), ('2026-05-02T10:00:00', 7, 't2', 1, 1, 1, 1, 0);
            """);

        await PartitionManager.TruncateDayAsync(connection, d1, TestContext.Current.CancellationToken);

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.ticker WHERE market_id = 7"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.ticker WHERE market_id = 7 AND ts < '2026-05-02'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.trade WHERE market_id = 7"));
    }

    [Fact]
    public async Task ADayWithoutItsOwnBoundaryIsRefused()
    {
        db.Require();
        await using var connection = await db.OpenAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PartitionManager.TruncateDayAsync(connection, new DateOnly(1999, 1, 1), TestContext.Current.CancellationToken));

        Assert.Contains("No partition boundary", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullConnectionsAreRefused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => PartitionManager.GetBoundariesAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => PartitionManager.EnsureBoundariesAsync(null!, Day1, Day1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => PartitionManager.TruncateDayAsync(null!, Day1, TestContext.Current.CancellationToken));
    }
}
