using KalTerm.Data.Markets;

namespace KalTerm.Data.Tests;

/// <summary>Its own class, hence its own database: unlisting touches every tradable market that was not seen.</summary>
public class MarketUnlistTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MarketRecord Record(string ticker, string status) => new(ticker, "EV", "binary", status);

    [Fact]
    public async Task MarksOnlyTradableMarketsThatWereNotSeenSinceThePassStarted()
    {
        db.Require();
        var store = new MarketStore(new DataSource(db.Options));
        await store.UpsertAsync([Record("A", "active"), Record("B", "active"), Record("C", "settled"), Record("D", "open")], T0, TestContext.Current.CancellationToken);
        var passStart = T0.AddMinutes(5);
        await store.UpsertAsync([Record("A", "active")], passStart, TestContext.Current.CancellationToken);

        var unlisted = await store.MarkUnlistedAsync(passStart, TestContext.Current.CancellationToken);

        Assert.Equal(["B", "D"], unlisted.Order());
        Assert.Equal("active", await db.ScalarAsync<string>("SELECT status FROM dbo.market WHERE ticker = 'A'"));
        Assert.Equal(MarketStore.UnlistedStatus, await db.ScalarAsync<string>("SELECT status FROM dbo.market WHERE ticker = 'B'"));
        Assert.Equal("settled", await db.ScalarAsync<string>("SELECT status FROM dbo.market WHERE ticker = 'C'"));
    }

    [Fact]
    public async Task AnUnlistedMarketThatIsListedAgainBecomesTradableAgain()
    {
        db.Require();
        var store = new MarketStore(new DataSource(db.Options));
        await store.UpsertAsync([Record("R", "active")], T0, TestContext.Current.CancellationToken);
        await store.MarkUnlistedAsync(T0.AddMinutes(5), TestContext.Current.CancellationToken);

        await store.UpsertAsync([Record("R", "active")], T0.AddMinutes(10), TestContext.Current.CancellationToken);

        Assert.Equal("active", await db.ScalarAsync<string>("SELECT status FROM dbo.market WHERE ticker = 'R'"));
        Assert.Empty(await store.MarkUnlistedAsync(T0.AddMinutes(10), TestContext.Current.CancellationToken));
    }
}
