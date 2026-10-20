using KalTerm.Ingest.Analytics;

namespace KalTerm.Ingest.Tests;

public class AnalyzerStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 15, 250, DateTimeKind.Utc);

    private AnalyzerStore Store() => new(new KalTerm.Data.DataSource(db.Options));

    [Fact]
    public async Task TheLastStartIsRememberedToTheMillisecondAndUnknownUntilSet()
    {
        db.Require();
        var store = Store();

        Assert.Null(await store.GetLastStartAsync("st_last", TestContext.Current.CancellationToken));
        await store.SetLastStartAsync("st_last", Now, TestContext.Current.CancellationToken);

        Assert.Equal(Now, await store.GetLastStartAsync("st_last", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnlyOneHolderOfAnalyzersLockAtATimeAndItCanBeTakenAgainAfterRelease()
    {
        db.Require();
        var store = Store();

        var first = await store.TryLockAsync("st_lock", TestContext.Current.CancellationToken);
        var second = await store.TryLockAsync("st_lock", TestContext.Current.CancellationToken);
        var other = await store.TryLockAsync("st_other", TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.NotNull(other);
        await first.DisposeAsync();
        var again = await store.TryLockAsync("st_lock", TestContext.Current.CancellationToken);
        Assert.NotNull(again);
        await again.DisposeAsync();
        await other.DisposeAsync();
    }

    [Fact]
    public async Task ARunIsOpenedAndClosedInTheHistory()
    {
        db.Require();
        var store = Store();

        var id = await store.BeginRunAsync("st_run", Now, TestContext.Current.CancellationToken);
        Assert.Equal("running", await db.ScalarAsync<string>($"SELECT status FROM dbo.analyzer_run WHERE run_id = {id}"));
        await store.FinishRunAsync(id, Now.AddSeconds(3), "ok", 42, true, "42 written", TestContext.Current.CancellationToken);

        Assert.Equal(("ok", 42, true, "42 written"), (
            await db.ScalarAsync<string>($"SELECT status FROM dbo.analyzer_run WHERE run_id = {id}"),
            await db.ScalarAsync<int>($"SELECT row_count FROM dbo.analyzer_run WHERE run_id = {id}"),
            await db.ScalarAsync<bool>($"SELECT changed FROM dbo.analyzer_run WHERE run_id = {id}"),
            await db.ScalarAsync<string>($"SELECT message FROM dbo.analyzer_run WHERE run_id = {id}")));
        Assert.Equal(Now.AddSeconds(3), await db.ScalarAsync<DateTime>($"SELECT finished_at FROM dbo.analyzer_run WHERE run_id = {id}"));
    }

    [Fact]
    public async Task TheChangeCounterCountsUpFromOneAndPruningKeepsRecentRuns()
    {
        db.Require();
        var store = Store();

        await store.BumpVersionAsync("st_ver", Now, TestContext.Current.CancellationToken);
        await store.BumpVersionAsync("st_ver", Now, TestContext.Current.CancellationToken);
        var old = await store.BeginRunAsync("st_prune", Now.AddDays(-10), TestContext.Current.CancellationToken);
        var recent = await store.BeginRunAsync("st_prune", Now, TestContext.Current.CancellationToken);
        var pruned = await store.PruneRunsAsync(Now.AddDays(-7), TestContext.Current.CancellationToken);

        Assert.Equal("2", await db.ScalarAsync<string>("SELECT value FROM dbo.ingest_state WHERE name = 'analyzer.st_ver.version'"));
        Assert.True(pruned >= 1);
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.analyzer_run WHERE run_id = {old}"));
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.analyzer_run WHERE run_id = {recent}"));
    }
}
