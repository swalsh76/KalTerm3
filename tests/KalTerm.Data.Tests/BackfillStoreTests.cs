using KalTerm.Data.Gaps;
using KalTerm.Data.Reading;

namespace KalTerm.Data.Tests;

public class BackfillStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);

    private BackfillStore Store() => new(new DataSource(db.Options));

    private async Task GapsAsync(params GapRecord[] gaps)
    {
        await db.ExecuteAsync("DELETE FROM dbo.ingest_gap");
        await new GapStore(new DataSource(db.Options)).AppendAsync(gaps, TestContext.Current.CancellationToken);
    }

    private async Task<(string Status, int Attempts)> StateAsync(string kind)
    {
        await using var connection = await db.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand($"SELECT backfill_status, backfill_attempts FROM dbo.ingest_gap WHERE kind = '{kind}'", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.GetString(0), reader.GetByte(1));
    }

    [Fact]
    public async Task GapsRecordedBeforeTheMigrationAreSimplyPending()
    {
        db.Require();
        await db.ExecuteAsync("DELETE FROM dbo.ingest_gap");
        await db.ExecuteAsync("INSERT dbo.ingest_gap (kind, started_at, ended_at) VALUES ('old_style', '2026-10-01 10:00:00', '2026-10-01 10:05:00')");

        Assert.Equal(("pending", 0), await StateAsync("old_style"));
    }

    [Fact]
    public async Task TheOldestClosedSettledPendingGapIsClaimedFirstAndTheAttemptIsCounted()
    {
        db.Require();
        await GapsAsync(
            new GapRecord("g_second", Now.AddHours(-2), Now.AddHours(-2).AddMinutes(5)),
            new GapRecord("g_first", Now.AddHours(-3), Now.AddHours(-3).AddMinutes(5)),
            new GapRecord("g_open", Now.AddHours(-4), null),
            new GapRecord("g_unsettled", Now.AddMinutes(-1), Now.AddSeconds(-10)),
            new GapRecord("g_market", Now.AddHours(-5), Now.AddHours(-4), MarketId: 5));

        var first = await Store().ClaimNextAsync(Now, Settle, Stale, TestContext.Current.CancellationToken);
        var second = await Store().ClaimNextAsync(Now, Settle, Stale, TestContext.Current.CancellationToken);
        var none = await Store().ClaimNextAsync(Now, Settle, Stale, TestContext.Current.CancellationToken);

        Assert.Equal(("g_first", 1), (first!.Kind, first.Attempts));
        Assert.Equal(DateTimeKind.Utc, first.StartedAt.Kind);
        Assert.Equal(Now.AddHours(-3).AddMinutes(5), first.EndedAt);
        Assert.Equal("g_second", second!.Kind);
        Assert.Null(none);
        Assert.Equal(("running", 1), await StateAsync("g_first"));
        Assert.Equal(("pending", 0), await StateAsync("g_unsettled"));
    }

    [Fact]
    public async Task AFailedAttemptWaitsOneMinutePerAttemptAndTheLastOneEndsInFailed()
    {
        db.Require();
        await GapsAsync(new GapRecord("g_retry", Now.AddHours(-1), Now.AddHours(-1).AddMinutes(1)));
        var store = Store();

        var attempt1 = await store.ClaimNextAsync(Now, Settle, Stale, TestContext.Current.CancellationToken);
        await store.FailAsync(attempt1!.Id, 3, "rest was down", Now, TestContext.Current.CancellationToken);
        var tooSoon = await store.ClaimNextAsync(Now.AddSeconds(30), Settle, Stale, TestContext.Current.CancellationToken);
        var attempt2 = await store.ClaimNextAsync(Now.AddSeconds(61), Settle, Stale, TestContext.Current.CancellationToken);
        await store.FailAsync(attempt2!.Id, 3, "rest was down again", Now.AddMinutes(1), TestContext.Current.CancellationToken);
        var attempt3 = await store.ClaimNextAsync(Now.AddMinutes(4), Settle, Stale, TestContext.Current.CancellationToken);
        await store.FailAsync(attempt3!.Id, 3, "rest was down a third time", Now.AddMinutes(4), TestContext.Current.CancellationToken);
        var after = await store.ClaimNextAsync(Now.AddHours(1), Settle, Stale, TestContext.Current.CancellationToken);

        Assert.Null(tooSoon);
        Assert.Equal((2, 3), (attempt2.Attempts, attempt3.Attempts));
        Assert.Null(after);
        Assert.Equal(("failed", 3), await StateAsync("g_retry"));
        Assert.Equal("rest was down a third time", await db.ScalarAsync<string>("SELECT backfill_note FROM dbo.ingest_gap WHERE kind = 'g_retry'"));
    }

    [Fact]
    public async Task AGapLeftRunningByADeadProcessIsTakenOverAfterTheStaleTime()
    {
        db.Require();
        await GapsAsync(new GapRecord("g_dead", Now.AddHours(-1), Now.AddHours(-1).AddMinutes(1)));
        var store = Store();
        await store.ClaimNextAsync(Now, Settle, Stale, TestContext.Current.CancellationToken);

        var early = await store.ClaimNextAsync(Now.AddMinutes(9), Settle, Stale, TestContext.Current.CancellationToken);
        var late = await store.ClaimNextAsync(Now.AddMinutes(11), Settle, Stale, TestContext.Current.CancellationToken);

        Assert.Null(early);
        Assert.Equal(2, late!.Attempts);
    }

    [Fact]
    public async Task CompletingAGapRecordsWhatWasRecoveredAndCutsALongNote()
    {
        db.Require();
        await GapsAsync(new GapRecord("g_done", Now.AddHours(-1), Now.AddHours(-1).AddMinutes(1)));
        var claimed = await Store().ClaimNextAsync(Now, Settle, Stale, TestContext.Current.CancellationToken);

        await Store().CompleteAsync(claimed!.Id, BackfillStatus.Done, 120, 7, new string('n', 500), Now.AddSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(("done", 1), await StateAsync("g_done"));
        Assert.Equal(120, await db.ScalarAsync<int>("SELECT backfill_found FROM dbo.ingest_gap WHERE kind = 'g_done'"));
        Assert.Equal(7, await db.ScalarAsync<int>("SELECT backfill_added FROM dbo.ingest_gap WHERE kind = 'g_done'"));
        Assert.Equal(200, await db.ScalarAsync<int>("SELECT LEN(backfill_note) FROM dbo.ingest_gap WHERE kind = 'g_done'"));
        Assert.Null(await Store().ClaimNextAsync(Now.AddHours(1), Settle, Stale, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoredTradesAreListedByMarketAndIdWithinTheRangeOnly()
    {
        db.Require();
        var day = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        await ReaderSeed.DaysAsync(db, day, 3);
        var a = await ReaderSeed.MarketAsync(db, "BF-A");
        var b = await ReaderSeed.MarketAsync(db, "BF-B");
        await ReaderSeed.TradesAsync(db, a, (day.AddHours(1), "x-1", 400_000, 1, false), (day.AddHours(5), "x-2", 400_000, 1, false));
        await ReaderSeed.TradesAsync(db, b, (day.AddHours(1).AddSeconds(2), "x-1", 400_000, 2, false));

        var keys = await Store().StoredTradesAsync(day.AddHours(1).AddSeconds(-1), day.AddHours(2), TestContext.Current.CancellationToken);

        Assert.Equal(new HashSet<StoredTradeKey> { new(a, "x-1"), new(b, "x-1") }, keys);
    }

    [Fact]
    public async Task BadArgumentsAreRefused()
    {
        var store = new BackfillStore(new DataSource(new DatabaseConnectionOptions { Server = "no-such-server-for-this-test" }));

        await Assert.ThrowsAsync<ArgumentException>(() => store.CompleteAsync(1, " ", 0, 0, "n", Now, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CompleteAsync(1, "done", 0, 0, null!, Now, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.FailAsync(1, 3, null!, Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheGapReaderAndTheStatusReportTheBackfillOfEachGap()
    {
        db.Require();
        await GapsAsync(
            new GapRecord("g_a_done", Now.AddHours(-3), Now.AddHours(-3).AddMinutes(1)),
            new GapRecord("g_b_wait", Now.AddHours(-2), Now.AddHours(-2).AddMinutes(1)),
            new GapRecord("g_c_bad", Now.AddHours(-1), Now.AddHours(-1).AddMinutes(1)),
            new GapRecord("g_d_open", Now.AddMinutes(-5), null));
        var store = Store();
        var done = await store.ClaimNextAsync(Now, Settle, Stale, TestContext.Current.CancellationToken);
        await store.CompleteAsync(done!.Id, BackfillStatus.Done, 50, 5, "window recovered", Now, TestContext.Current.CancellationToken);
        await db.ExecuteAsync("UPDATE dbo.ingest_gap SET backfill_status = 'failed', backfill_attempts = 3, backfill_note = 'gave up' WHERE kind = 'g_c_bad'");

        var gaps = await new GapReader(new DataSource(db.Options)).ReadAsync(Now.AddDays(-1), Now.AddDays(1), 10, TestContext.Current.CancellationToken);
        var status = await new StatusReader(new DataSource(db.Options)).ReadAsync(Now, TimeSpan.FromDays(7), TestContext.Current.CancellationToken);

        var byKind = gaps.ToDictionary(g => g.Kind);
        Assert.Equal(("done", 50, 5, "window recovered"), (byKind["g_a_done"].BackfillStatus, byKind["g_a_done"].BackfillFound, byKind["g_a_done"].BackfillAdded, byKind["g_a_done"].BackfillNote));
        Assert.Equal(("pending", null, null), (byKind["g_b_wait"].BackfillStatus, byKind["g_b_wait"].BackfillFound, byKind["g_b_wait"].BackfillAdded));
        Assert.Equal("gave up", byKind["g_c_bad"].BackfillNote);
        Assert.Equal(new BackfillCounts(1, 1), status.Backfill);   // the open gap is not waiting for a backfill yet
    }
}
