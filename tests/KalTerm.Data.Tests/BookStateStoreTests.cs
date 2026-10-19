using KalTerm.Data.Books;

namespace KalTerm.Data.Tests;

public class BookLevelTextTests
{
    [Fact]
    public void LevelsRoundTripAsText()
    {
        BookLevel[] levels = [new(560_000, 1_850), new(550_000, 100), new(1, 1)];

        var text = BookLevelText.Encode(levels);

        Assert.Equal("560000:1850;550000:100;1:1", text);
        Assert.Equal(levels, BookLevelText.Decode(text));
    }

    [Fact]
    public void NoLevelsIsEmptyTextBothWays()
    {
        Assert.Equal(string.Empty, BookLevelText.Encode([]));
        Assert.Empty(BookLevelText.Decode(string.Empty));
    }

    [Fact]
    public void OnlyTheBestHundredLevelsAreKept()
    {
        var levels = Enumerable.Range(1, 150).Select(i => new BookLevel(1_000_000 - (i * 1_000), i)).ToList();

        var decoded = BookLevelText.Decode(BookLevelText.Encode(levels));

        Assert.Equal(BookLevelText.MaxLevels, decoded.Count);
        Assert.Equal(levels[0], decoded[0]);
        Assert.Equal(levels[99], decoded[^1]);
        Assert.True(BookLevelText.Encode(levels).Length < 8000);
    }

    [Theory]
    [InlineData("560000")]
    [InlineData("560000:")]
    [InlineData(":5")]
    [InlineData("a:b")]
    [InlineData("1:2:3")]
    [InlineData("1:2;")]
    [InlineData("1:2;;3:4")]
    public void TextThatIsNotAListOfLevelsIsRefused(string text)
    {
        Assert.Throws<FormatException>(() => BookLevelText.Decode(text));
    }

    [Fact]
    public void NullsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => BookLevelText.Encode(null!));
        Assert.Throws<ArgumentNullException>(() => BookLevelText.Decode(null!));
    }
}

public class BookStateStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, 250, DateTimeKind.Utc);

    private BookStateStore Store() => new(new DataSource(db.Options));

    [Fact]
    public async Task ABookIsStoredAndOverwrittenInPlace()
    {
        db.Require();
        BookLevel[] yes = [new(560_000, 1_850), new(550_000, 100)];
        BookLevel[] no = [new(430_000, 5)];

        await Store().SaveAsync([new BookState(42, T0, T0.AddSeconds(1), false, false, yes, no)], TestContext.Current.CancellationToken);
        await Store().SaveAsync([new BookState(42, T0.AddSeconds(5), T0.AddSeconds(6), true, true, [], [])], TestContext.Current.CancellationToken);

        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.orderbook_state WHERE market_id = 42"));
        await using var connection = await db.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("SELECT updated_at, published_at, is_stale, is_approximate, yes_levels, no_levels FROM dbo.orderbook_state WHERE market_id = 42", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal((T0.AddSeconds(5), T0.AddSeconds(6), true, true, string.Empty, string.Empty), (reader.GetDateTime(0), reader.GetDateTime(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetString(4), reader.GetString(5)));
    }

    [Fact]
    public async Task SeveralBooksAreStoredTogetherWithTheirLevelsIntact()
    {
        db.Require();
        BookLevel[] yes = [new(560_000, 1_850), new(550_000, 100)];

        await Store().SaveAsync(
            [new BookState(51, T0, T0, false, false, yes, []), new BookState(52, T0, T0, false, true, [], yes)],
            TestContext.Current.CancellationToken);

        Assert.Equal("560000:1850;550000:100", await db.ScalarAsync<string>("SELECT yes_levels FROM dbo.orderbook_state WHERE market_id = 51"));
        Assert.Equal("560000:1850;550000:100", await db.ScalarAsync<string>("SELECT no_levels FROM dbo.orderbook_state WHERE market_id = 52"));
    }

    [Fact]
    public async Task NothingToSaveTouchesNothingAndNullIsRefused()
    {
        var store = new BookStateStore(new DataSource(new DatabaseConnectionOptions { Server = "no-such-server-for-this-test" }));

        await store.SaveAsync([], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!, TestContext.Current.CancellationToken));
    }
}
