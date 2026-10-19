using KalTerm.Data.State;

namespace KalTerm.Data.Tests;

public class IngestStateStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, 250, DateTimeKind.Utc);

    private IngestStateStore Store() => new(new DataSource(db.Options));

    [Fact]
    public async Task AValueNeverWrittenReadsAsNothing()
    {
        db.Require();

        Assert.Null(await Store().GetAsync("never-" + Guid.NewGuid().ToString("N")[..8], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWrittenValueComesBackWithItsUtcTime()
    {
        db.Require();
        var store = Store();

        await store.SetAsync(IngestStateKeys.Run, IngestStateKeys.Running, T0, TestContext.Current.CancellationToken);
        var entry = await store.GetAsync(IngestStateKeys.Run, TestContext.Current.CancellationToken);

        Assert.Equal(new IngestStateEntry(IngestStateKeys.Running, T0), entry);
        Assert.Equal(DateTimeKind.Utc, entry!.UpdatedAt.Kind);
    }

    [Fact]
    public async Task WritingAgainReplacesTheValueInPlace()
    {
        db.Require();
        var store = Store();
        await store.SetAsync("replace-me", "first", T0, TestContext.Current.CancellationToken);

        await store.SetAsync("replace-me", "second", T0.AddMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(new IngestStateEntry("second", T0.AddMinutes(1)), await store.GetAsync("replace-me", TestContext.Current.CancellationToken));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT COUNT_BIG(*) FROM dbo.ingest_state WHERE name = 'replace-me'"));
    }

    [Fact]
    public async Task AValueTheColumnCannotHoldIsRefusedAndTheOldOneStays()
    {
        db.Require();
        var store = Store();
        await store.SetAsync("keep-me", "kept", T0, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("keep-me", new string('v', 401), T0.AddMinutes(1), TestContext.Current.CancellationToken));

        Assert.Equal("kept", (await store.GetAsync("keep-me", TestContext.Current.CancellationToken))!.Value);
    }

    [Fact]
    public async Task ABadNameOrMissingValueIsRefusedBeforeAnyConnection()
    {
        var store = new IngestStateStore(new DataSource(new DatabaseConnectionOptions { Server = "no-such-server-for-this-test" }));

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(" ", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(new string('n', 65), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("", "v", T0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetAsync("name", null!, T0, TestContext.Current.CancellationToken));
    }
}
