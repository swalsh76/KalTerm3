using KalTerm.Data.Reading;

namespace KalTerm.Data.Tests;

/// <summary>What the market streams read from real tables: markets by ticker, many markets' ticker rows over a window, and several books at once.</summary>
public class MarketStreamReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private MarketStreamReader Reader() => new(new DataSource(db.Options));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Three markets with a few ticks each. The prefix keeps one test's rows apart from another's: the database is shared by the whole class.</summary>
    private async Task<(int A, int B, int C)> SeedAsync(string prefix)
    {
        await ReaderSeed.DaysAsync(db, T0.Date, 2);
        var a = await ReaderSeed.MarketAsync(db, prefix + "-A");
        var b = await ReaderSeed.MarketAsync(db, prefix + "-B");
        var c = await ReaderSeed.MarketAsync(db, prefix + "-C");
        await ReaderSeed.TicksAsync(db, a, (T0.AddSeconds(1), 510_000), (T0.AddSeconds(3), 520_000), (T0.AddSeconds(5), 530_000));
        await ReaderSeed.TicksAsync(db, b, (T0.AddSeconds(2), 410_000), (T0.AddSeconds(3), 420_000));
        await ReaderSeed.TicksAsync(db, c, (T0.AddSeconds(2), 310_000));
        return (a, b, c);
    }

    [Fact]
    public async Task OnlyExistingMarketsAreResolvedAndTheirIdsAreRight()
    {
        db.Require();
        var (a, b, _) = await SeedAsync("RES");

        var found = await Reader().ResolveAsync(["RES-A", "RES-B", "RES-NONE"], Ct);

        Assert.Equal(new Dictionary<string, int> { ["RES-A"] = a, ["RES-B"] = b }, found);
        Assert.Empty(await Reader().ResolveAsync([], Ct));
    }

    [Fact]
    public async Task TheRowsOfSeveralMarketsComeBackOldestFirstWithinTheWindowOnly()
    {
        db.Require();
        var (a, b, _) = await SeedAsync("WIN");
        var markets = new Dictionary<int, string> { [a] = "WIN-A", [b] = "WIN-B" };

        var rows = await Reader().ReadTicksAsync(markets, T0.AddSeconds(2), T0.AddSeconds(5), 100, Ct);

        // the window is [from, until): the row at 12:00:05 is not in it; the market that was not asked for (C) is not in it; rows with the same time come in a fixed order
        Assert.Equal([("WIN-B", T0.AddSeconds(2)), ("WIN-A", T0.AddSeconds(3)), ("WIN-B", T0.AddSeconds(3))], rows.Select(r => (r.Ticker, r.Tick.Ts)));
        Assert.All(rows, r => Assert.Equal(DateTimeKind.Utc, r.Tick.Ts.Kind));
        Assert.Equal((520_000L, 519_990L), (rows[1].Tick.PriceE6, rows[1].Tick.YesBidE6));
    }

    [Fact]
    public async Task TheRowLimitIsHonouredAndAnEmptyWindowOrAnEmptyListReadsNothing()
    {
        db.Require();
        var (a, b, _) = await SeedAsync("LIM");
        var markets = new Dictionary<int, string> { [a] = "LIM-A", [b] = "LIM-B" };

        var limited = await Reader().ReadTicksAsync(markets, T0, T0.AddSeconds(60), 2, Ct);

        Assert.Equal(2, limited.Count);
        Assert.Empty(await Reader().ReadTicksAsync(markets, T0.AddSeconds(5), T0.AddSeconds(5), 10, Ct));
        Assert.Empty(await Reader().ReadTicksAsync(new Dictionary<int, string>(), T0, T0.AddSeconds(60), 10, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Reader().ReadTicksAsync(markets, T0, T0.AddSeconds(60), 0, Ct));
    }

    [Fact]
    public async Task TwoReadsOfTheSameWindowGiveTheSameOrder()
    {
        db.Require();
        var (a, _, _) = await SeedAsync("ORD");
        await ReaderSeed.TicksAsync(db, a, (T0.AddSeconds(1), 511_000), (T0.AddSeconds(1), 512_000));
        var markets = new Dictionary<int, string> { [a] = "ORD-A" };

        var first = await Reader().ReadTicksAsync(markets, T0, T0.AddSeconds(2), 100, Ct);
        var second = await Reader().ReadTicksAsync(markets, T0, T0.AddSeconds(2), 100, Ct);

        Assert.Equal(first.Select(r => r.Tick.PriceE6), second.Select(r => r.Tick.PriceE6));
        Assert.Equal(first.Select(r => r.Tick.PriceE6).Order(), first.Select(r => r.Tick.PriceE6));   // ties come in price order
    }

    [Fact]
    public async Task SeveralBooksAreReadAtOnceInTickerOrderAndOnlyThosePublished()
    {
        db.Require();
        var a = await ReaderSeed.MarketAsync(db, "STRB-A");
        var b = await ReaderSeed.MarketAsync(db, "STRB-B");
        await ReaderSeed.MarketAsync(db, "STRB-C");
        await new Books.BookStateStore(new DataSource(db.Options)).SaveAsync(
            [
                new Books.BookState(b, T0, T0.AddSeconds(2), false, false, [new(560_000, 100)], []),
                new Books.BookState(a, T0, T0.AddSeconds(2), true, false, [], [new(430_000, 5)]),
            ],
            Ct);

        var books = await new BookReader(new DataSource(db.Options)).GetManyAsync(["STRB-B", "STRB-A", "STRB-C", "STRB-NONE"], Ct);

        Assert.Equal(["STRB-A", "STRB-B"], books.Select(x => x.Ticker));
        Assert.True(books[0].Stale);
        Assert.Equal([new Books.BookLevel(560_000, 100)], books[1].YesLevels);
        Assert.Empty(await new BookReader(new DataSource(db.Options)).GetManyAsync([], Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => new BookReader(new DataSource(db.Options)).GetManyAsync(null!, Ct));
    }

    [Fact]
    public async Task ARealStreamReaderValidatesWhatItIsGiven()
    {
        db.Require();

        await Assert.ThrowsAsync<ArgumentNullException>(() => Reader().ResolveAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Reader().ReadTicksAsync(null!, T0, T0.AddSeconds(1), 1, Ct));
    }
}
