namespace KalTerm.Data.Tests;

public class PartitionMaintenanceTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task AddsEveryDayFromTodayThroughTheDaysAheadAndOnlyOnce()
    {
        db.Require();
        var maintenance = new PartitionMaintenance(new DataSource(db.Options));
        var today = new DateOnly(2027, 1, 1);

        var first = await maintenance.EnsureAheadAsync(today, 3, TestContext.Current.CancellationToken);
        var again = await maintenance.EnsureAheadAsync(today, 3, TestContext.Current.CancellationToken);
        var nextDay = await maintenance.EnsureAheadAsync(today.AddDays(1), 3, TestContext.Current.CancellationToken);

        Assert.Equal([today, today.AddDays(1), today.AddDays(2), today.AddDays(3)], first);
        Assert.Empty(again);
        Assert.Equal([today.AddDays(4)], nextDay);
    }

    [Fact]
    public async Task AtLeastOneDayAheadIsRequired()
    {
        var maintenance = new PartitionMaintenance(new DataSource(new DatabaseConnectionOptions { Server = "no-such-server-for-this-test" }));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => maintenance.EnsureAheadAsync(new DateOnly(2027, 1, 1), 0, TestContext.Current.CancellationToken));
    }
}
