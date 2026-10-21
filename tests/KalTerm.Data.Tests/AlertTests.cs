using KalTerm.Data;
using KalTerm.Data.Alerts;
using KalTerm.Data.Auth;
using KalTerm.Data.Users;

namespace KalTerm.Data.Tests;

public class AlertKindsTests
{
    [Fact]
    public void EveryKindHasAWireNameThatReadsBack()
    {
        foreach (var kind in Enum.GetValues<AlertKind>())
        {
            Assert.True(AlertKinds.TryParse(AlertKinds.Name(kind), out var back));
            Assert.Equal(kind, back);
        }

        Assert.False(AlertKinds.TryParse("price_sideways", out _));
        Assert.False(AlertKinds.TryParse(null, out _));
        Assert.False(AlertKinds.TryParse("PRICE_ABOVE", out _));
    }

    [Theory]
    [InlineData(AlertKind.PriceAbove, 1, true)]
    [InlineData(AlertKind.PriceAbove, 999_999, true)]
    [InlineData(AlertKind.PriceAbove, 0, false)]
    [InlineData(AlertKind.PriceBelow, 1_000_000, false)]
    [InlineData(AlertKind.SpreadAbove, 1_000_000, true)]
    [InlineData(AlertKind.SpreadAbove, 1_000_001, false)]
    [InlineData(AlertKind.VolumeSpike, 2, true)]
    [InlineData(AlertKind.VolumeSpike, 1, false)]
    [InlineData(AlertKind.VolumeSpike, 1_000, true)]
    [InlineData(AlertKind.VolumeSpike, 1_001, false)]
    [InlineData(AlertKind.OiChange, 100, true)]
    [InlineData(AlertKind.OiChange, 99, false)]
    public void ThresholdsMustMakeSenseForTheirKind(AlertKind kind, long threshold, bool valid)
    {
        Assert.Equal(valid, AlertKinds.IsValidThreshold(kind, threshold));
    }
}

public class AlertRulesTests
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);

    private static AlertObservation Seen(long? last = 600_000, long? spread = 20_000, long recent = 0, long baseline = 0, long? oi = 10_000, long? oiBefore = 9_000, bool price = false, bool volume = false, bool oiPartial = false) =>
        new(last, spread, recent, baseline, oi, oiBefore, price, volume, oiPartial);

    [Theory]
    [InlineData(AlertKind.PriceAbove, 600_000, AlertCondition.Met)]
    [InlineData(AlertKind.PriceAbove, 600_001, AlertCondition.NotMet)]
    [InlineData(AlertKind.PriceBelow, 600_000, AlertCondition.Met)]
    [InlineData(AlertKind.PriceBelow, 599_999, AlertCondition.NotMet)]
    [InlineData(AlertKind.SpreadAbove, 20_000, AlertCondition.Met)]
    [InlineData(AlertKind.SpreadAbove, 20_001, AlertCondition.NotMet)]
    public void PriceAndSpreadConditionsIncludeTheThresholdItself(AlertKind kind, long threshold, AlertCondition expected)
    {
        Assert.Equal(expected, AlertRules.Judge(kind, threshold, Seen()).Condition);
    }

    [Fact]
    public void ThePriceAlertsReportTheObservedPriceAndSpread()
    {
        Assert.Equal(600_000L, AlertRules.Judge(AlertKind.PriceAbove, 500_000, Seen()).Value);
        Assert.Equal(20_000L, AlertRules.Judge(AlertKind.SpreadAbove, 10_000, Seen()).Value);
    }

    [Fact]
    public void MissingOrIncompleteDataDecidesNothing()
    {
        Assert.Equal(AlertCondition.Unknown, AlertRules.Judge(AlertKind.PriceAbove, 1, Seen(last: null)).Condition);
        Assert.Equal(AlertCondition.Unknown, AlertRules.Judge(AlertKind.PriceBelow, 999_999, Seen(price: true)).Condition);
        Assert.Equal(AlertCondition.Unknown, AlertRules.Judge(AlertKind.SpreadAbove, 1, Seen(spread: null)).Condition);
        Assert.Equal(AlertCondition.Unknown, AlertRules.Judge(AlertKind.SpreadAbove, 1, Seen(price: true)).Condition);
        Assert.Equal(AlertCondition.Unknown, AlertRules.Judge(AlertKind.VolumeSpike, 2, Seen(recent: 100, baseline: 0)).Condition);
        Assert.Equal(AlertCondition.Unknown, AlertRules.Judge(AlertKind.VolumeSpike, 2, Seen(recent: 100, baseline: 100, volume: true)).Condition);
        Assert.Equal(AlertCondition.Unknown, AlertRules.Judge(AlertKind.OiChange, 100, Seen(oi: null)).Condition);
        Assert.Equal(AlertCondition.Unknown, AlertRules.Judge(AlertKind.OiChange, 100, Seen(oiBefore: null)).Condition);
        Assert.Equal(AlertCondition.Unknown, AlertRules.Judge(AlertKind.OiChange, 100, Seen(oiPartial: true)).Condition);
    }

    [Theory]
    [InlineData(1_000, 9_600, 10, AlertCondition.Met)]     // 1,000 * 96 = 96,000 = exactly 10 x 9,600
    [InlineData(999, 9_600, 10, AlertCondition.NotMet)]
    [InlineData(100, 9_600, 10, AlertCondition.NotMet)]    // 100 * 96 = 9,600 is only 1 x
    [InlineData(0, 9_600, 2, AlertCondition.NotMet)]
    public void AVolumeSpikeIsTheRecentQuarterHourAgainstTheAverageQuarterHourOfTheDayBefore(long recent, long baseline, long factor, AlertCondition expected)
    {
        Assert.Equal(expected, AlertRules.Judge(AlertKind.VolumeSpike, factor, Seen(recent: recent, baseline: baseline)).Condition);
    }

    [Fact]
    public void TheSpikeValueIsTheWholeNumberOfTimesTheAverageAndHugeVolumesDoNotOverflow()
    {
        Assert.Equal(10L, AlertRules.Judge(AlertKind.VolumeSpike, 2, Seen(recent: 1_050, baseline: 9_600)).Value);
        var huge = AlertRules.Judge(AlertKind.VolumeSpike, 1_000, Seen(recent: long.MaxValue / 200, baseline: 1));
        Assert.Equal(AlertCondition.Met, huge.Condition);
    }

    [Theory]
    [InlineData(10_500, 10_000, 500, AlertCondition.Met)]
    [InlineData(9_500, 10_000, 500, AlertCondition.Met)]
    [InlineData(10_499, 10_000, 500, AlertCondition.NotMet)]
    [InlineData(10_000, 10_000, 500, AlertCondition.NotMet)]
    public void OpenInterestMovingEitherWayByTheThresholdFires(long now, long before, long threshold, AlertCondition expected)
    {
        var verdict = AlertRules.Judge(AlertKind.OiChange, threshold, Seen(oi: now, oiBefore: before));

        Assert.Equal((expected, now - before), (verdict.Condition, verdict.Value));
    }

    private static AlertVerdict Met => new(AlertCondition.Met, 1);

    private static AlertVerdict NotMet => new(AlertCondition.NotMet, 0);

    [Fact]
    public void TheFirstEvaluationOnlyRecordsWhatAlreadyHoldsAndNeverFires()
    {
        var already = AlertRules.Step(new AlertState(null, null), Met, Now, 60);
        var not = AlertRules.Step(new AlertState(null, null), NotMet, Now, 60);

        Assert.Equal((false, (bool?)false), (already.Fire, already.State.Armed));   // true at the start: wait for it to clear
        Assert.Equal((false, (bool?)true), (not.Fire, not.State.Armed));
    }

    [Fact]
    public void AReadyAlertFiresWhenTheConditionBecomesTrueAndThenWaitsForItToClear()
    {
        var fired = AlertRules.Step(new AlertState(true, null), Met, Now, 60);
        var still = AlertRules.Step(fired.State, Met, Now.AddSeconds(30), 60);
        var cleared = AlertRules.Step(still.State, NotMet, Now.AddSeconds(40), 60);
        var again = AlertRules.Step(cleared.State, Met, Now.AddSeconds(100), 60);

        Assert.Equal((true, (bool?)false, (DateTime?)Now), (fired.Fire, fired.State.Armed, fired.State.LastFiredAt));
        Assert.False(still.Fire);
        Assert.Equal((bool?)true, cleared.State.Armed);
        Assert.True(again.Fire);
        Assert.Equal((DateTime?)Now.AddSeconds(100), again.State.LastFiredAt);
    }

    [Fact]
    public void AnAlertReadyAgainWithinItsCooldownWaitsForTheCooldownToPass()
    {
        var state = new AlertState(true, Now);

        var early = AlertRules.Step(state, Met, Now.AddSeconds(59), 60);
        var due = AlertRules.Step(state, Met, Now.AddSeconds(60), 60);

        Assert.False(early.Fire);
        Assert.Equal((bool?)true, early.State.Armed);   // still ready: it fires the moment the cooldown is over, if the condition still holds
        Assert.True(due.Fire);
    }

    [Fact]
    public void AnUnknownVerdictChangesNothing()
    {
        var state = new AlertState(true, Now);

        var step = AlertRules.Step(state, new AlertVerdict(AlertCondition.Unknown, 0), Now.AddHours(1), 60);

        Assert.Equal((false, state), (step.Fire, step.State));
    }
}

public class UserDataStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);

    private UserDataStore Store() => new(new DataSource(db.Options));

    private async Task<int> UserAsync(string name) => await new UserStore(new DataSource(db.Options)).AddUserAsync(name, "read", Now, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AWatchlistIsMadeFilledListedShownAndDeleted()
    {
        db.Require();
        var u = await UserAsync("ud-lists");
        await ReaderSeed.MarketAsync(db, "UD-A");
        await ReaderSeed.MarketAsync(db, "UD-B");
        var store = Store();

        Assert.Equal(StoreOutcome.Ok, await store.CreateWatchlistAsync(u, "favorites", Now, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Exists, await store.CreateWatchlistAsync(u, "favorites", Now, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Ok, await store.AddMarketAsync(u, "favorites", "UD-B", Now, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Ok, await store.AddMarketAsync(u, "favorites", "UD-A", Now, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Ok, await store.AddMarketAsync(u, "favorites", "UD-A", Now, TestContext.Current.CancellationToken));   // again: fine

        var list = Assert.Single(await store.ListWatchlistsAsync(u, TestContext.Current.CancellationToken));
        var detail = (await store.GetWatchlistAsync(u, "favorites", TestContext.Current.CancellationToken))!;
        Assert.Equal(("favorites", 2, DateTimeKind.Utc), (list.Name, list.Markets, list.CreatedAt.Kind));
        Assert.Equal(["UD-A", "UD-B"], detail.Tickers);
        Assert.Equal(2, (await store.WatchlistMarketIdsAsync(u, "favorites", TestContext.Current.CancellationToken))!.Count);

        Assert.Equal(StoreOutcome.Ok, await store.RemoveMarketAsync(u, "favorites", "UD-A", TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.NotFound, await store.RemoveMarketAsync(u, "favorites", "UD-A", TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Ok, await store.DeleteWatchlistAsync(u, "favorites", TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.NotFound, await store.DeleteWatchlistAsync(u, "favorites", TestContext.Current.CancellationToken));
        Assert.Null(await store.GetWatchlistAsync(u, "favorites", TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.watchlist_market wm JOIN dbo.market m ON m.market_id = wm.market_id WHERE m.ticker IN ('UD-A', 'UD-B')"));   // the markets went with the list
    }

    [Fact]
    public async Task AddingToAMissingListOrAMissingMarketSaysWhichIsMissing()
    {
        db.Require();
        var u = await UserAsync("ud-missing");
        var store = Store();
        await store.CreateWatchlistAsync(u, "mine", Now, TestContext.Current.CancellationToken);

        Assert.Equal(StoreOutcome.NotFound, await store.AddMarketAsync(u, "nope", "UD-A", Now, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.MarketNotFound, await store.AddMarketAsync(u, "mine", "UD-NO-SUCH", Now, TestContext.Current.CancellationToken));
        Assert.Null(await store.WatchlistMarketIdsAsync(u, "nope", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UsersNeverSeeOrChangeEachOthersData()
    {
        db.Require();
        var ann = await UserAsync("ud-ann");
        var bob = await UserAsync("ud-bob");
        await ReaderSeed.MarketAsync(db, "UD-ISO");
        var store = Store();
        await store.CreateWatchlistAsync(ann, "shared-name", Now, TestContext.Current.CancellationToken);
        await store.AddMarketAsync(ann, "shared-name", "UD-ISO", Now, TestContext.Current.CancellationToken);
        var (_, annAlert) = await store.AddAlertAsync(ann, "UD-ISO", AlertKind.PriceAbove, 600_000, 60, Now, TestContext.Current.CancellationToken);
        await store.SetSettingAsync(ann, "time_zone", "Asia/Tokyo", Now, TestContext.Current.CancellationToken);

        Assert.Empty(await store.ListWatchlistsAsync(bob, TestContext.Current.CancellationToken));
        Assert.Null(await store.GetWatchlistAsync(bob, "shared-name", TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.NotFound, await store.DeleteWatchlistAsync(bob, "shared-name", TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.NotFound, await store.AddMarketAsync(bob, "shared-name", "UD-ISO", Now, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.NotFound, await store.RemoveMarketAsync(bob, "shared-name", "UD-ISO", TestContext.Current.CancellationToken));
        Assert.Empty(await store.ListAlertsAsync(bob, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.NotFound, await store.DeleteAlertAsync(bob, annAlert!.Value, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.NotFound, await store.SetAlertPausedAsync(bob, annAlert.Value, true, TestContext.Current.CancellationToken));
        Assert.Null(await store.GetSettingAsync(bob, "time_zone", TestContext.Current.CancellationToken));
        Assert.Single(await store.ListAlertsAsync(ann, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Ok, await store.CreateWatchlistAsync(bob, "shared-name", Now, TestContext.Current.CancellationToken));   // the same name is fine for another user
    }

    [Fact]
    public async Task ALayoutIsSavedReplacedListedReadAndDeleted()
    {
        db.Require();
        var u = await UserAsync("ud-layouts");
        var store = Store();

        Assert.Equal(StoreOutcome.Ok, await store.PutLayoutAsync(u, "desk", """{"version":1,"page":"Markets"}""", Now, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Ok, await store.PutLayoutAsync(u, "laptop", """{"version":1}""", Now, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Ok, await store.PutLayoutAsync(u, "desk", """{"version":1,"page":"Alerts"}""", Now.AddMinutes(5), TestContext.Current.CancellationToken));   // replaced

        var list = await store.ListLayoutsAsync(u, TestContext.Current.CancellationToken);
        var desk = (await store.GetLayoutAsync(u, "desk", TestContext.Current.CancellationToken))!;
        Assert.Equal(["desk", "laptop"], list.Select(l => l.Name));
        Assert.Equal(DateTimeKind.Utc, list[0].UpdatedAt.Kind);
        Assert.Equal((Now.AddMinutes(5), """{"version":1,"page":"Alerts"}"""), (desk.UpdatedAt, desk.Body));
        Assert.Null(await store.GetLayoutAsync(u, "nothing", TestContext.Current.CancellationToken));

        Assert.Equal(StoreOutcome.Ok, await store.DeleteLayoutAsync(u, "desk", TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.NotFound, await store.DeleteLayoutAsync(u, "desk", TestContext.Current.CancellationToken));
        Assert.Equal(["laptop"], (await store.ListLayoutsAsync(u, TestContext.Current.CancellationToken)).Select(l => l.Name));
    }

    [Fact]
    public async Task ALayoutBodyThatIsNotJsonNeverReachesTheTable()
    {
        db.Require();
        var u = await UserAsync("ud-layout-json");

        await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => Store().PutLayoutAsync(u, "bad", "not json at all", Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UsersNeverSeeOrChangeEachOthersLayouts()
    {
        db.Require();
        var ann = await UserAsync("ud-layout-ann");
        var bob = await UserAsync("ud-layout-bob");
        var store = Store();
        await store.PutLayoutAsync(ann, "shared-name", """{"a":1}""", Now, TestContext.Current.CancellationToken);

        Assert.Empty(await store.ListLayoutsAsync(bob, TestContext.Current.CancellationToken));
        Assert.Null(await store.GetLayoutAsync(bob, "shared-name", TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.NotFound, await store.DeleteLayoutAsync(bob, "shared-name", TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Ok, await store.PutLayoutAsync(bob, "shared-name", """{"b":2}""", Now, TestContext.Current.CancellationToken));   // the same name is fine for another user
        Assert.Equal("""{"a":1}""", (await store.GetLayoutAsync(ann, "shared-name", TestContext.Current.CancellationToken))!.Body);
    }

    [Fact]
    public async Task AUserCannotHaveMoreThanTwentyLayoutsButMayReplaceOneWhenFull()
    {
        db.Require();
        var u = await UserAsync("ud-many-layouts");
        var store = Store();
        for (var i = 0; i < UserDataStore.MaxLayouts; i++)
        {
            Assert.Equal(StoreOutcome.Ok, await store.PutLayoutAsync(u, "l" + i, "{}", Now, TestContext.Current.CancellationToken));
        }

        Assert.Equal(StoreOutcome.LimitReached, await store.PutLayoutAsync(u, "one-more", "{}", Now, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Ok, await store.PutLayoutAsync(u, "l3", """{"changed":true}""", Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AUserCannotHaveMoreThanTwentyWatchlists()
    {
        db.Require();
        var u = await UserAsync("ud-many-lists");
        var store = Store();
        for (var i = 0; i < UserDataStore.MaxWatchlists; i++)
        {
            Assert.Equal(StoreOutcome.Ok, await store.CreateWatchlistAsync(u, "l" + i, Now, TestContext.Current.CancellationToken));
        }

        Assert.Equal(StoreOutcome.LimitReached, await store.CreateWatchlistAsync(u, "one-more", Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWatchlistHoldsAtMostFiveHundredMarkets()
    {
        db.Require();
        var u = await UserAsync("ud-big-list");
        var records = Enumerable.Range(0, UserDataStore.MaxMarketsPerWatchlist + 1).Select(i => new KalTerm.Data.Markets.MarketRecord("UD-BIG-" + i, "EV-BIG", "binary", "active", "Yes", "No")).ToList();
        await new KalTerm.Data.Markets.MarketStore(new DataSource(db.Options)).UpsertAsync(records, Now, TestContext.Current.CancellationToken);
        var store = Store();
        await store.CreateWatchlistAsync(u, "big", Now, TestContext.Current.CancellationToken);
        for (var i = 0; i < UserDataStore.MaxMarketsPerWatchlist; i++)
        {
            Assert.Equal(StoreOutcome.Ok, await store.AddMarketAsync(u, "big", "UD-BIG-" + i, Now, TestContext.Current.CancellationToken));
        }

        Assert.Equal(StoreOutcome.LimitReached, await store.AddMarketAsync(u, "big", "UD-BIG-" + UserDataStore.MaxMarketsPerWatchlist, Now, TestContext.Current.CancellationToken));
        Assert.Equal(StoreOutcome.Ok, await store.AddMarketAsync(u, "big", "UD-BIG-0", Now, TestContext.Current.CancellationToken));   // one already there is still fine
    }

    [Fact]
    public async Task AnAlertIsMadeListedPausedResumedAndDeletedWithItsFires()
    {
        db.Require();
        var u = await UserAsync("ud-alerts");
        var market = await ReaderSeed.MarketAsync(db, "UD-AL");
        var store = Store();

        var (outcome, id) = await store.AddAlertAsync(u, "UD-AL", AlertKind.OiChange, 50_000, 120, Now, TestContext.Current.CancellationToken);
        var alert = Assert.Single(await store.ListAlertsAsync(u, TestContext.Current.CancellationToken));
        Assert.Equal((StoreOutcome.Ok, id), (outcome, alert.AlertId));
        Assert.Equal(("UD-AL", AlertKind.OiChange, 50_000L, 120, false), (alert.Ticker, alert.Kind, alert.Threshold, alert.CooldownSeconds, alert.Paused));
        Assert.Equal((Now, DateTimeKind.Utc), (alert.CreatedAt, alert.CreatedAt.Kind));

        await db.ExecuteAsync($"UPDATE dbo.alert SET armed = 0 WHERE alert_id = {id}");
        Assert.Equal(StoreOutcome.Ok, await store.SetAlertPausedAsync(u, id!.Value, true, TestContext.Current.CancellationToken));
        Assert.True((await store.ListAlertsAsync(u, TestContext.Current.CancellationToken))[0].Paused);
        Assert.Equal(StoreOutcome.Ok, await store.SetAlertPausedAsync(u, id.Value, false, TestContext.Current.CancellationToken));
        Assert.Equal(DBNull.Value, await db.ScalarAsync<object>($"SELECT ISNULL(CAST(armed AS int), NULL) FROM dbo.alert WHERE alert_id = {id}"));   // resuming makes it judge afresh

        await db.ExecuteAsync($"INSERT dbo.alert_fire (alert_id, user_id, market_id, kind, threshold, value, fired_at) VALUES ({id}, {u}, {market}, 'oi_change', 50000, 60000, '2026-03-11T12:31:00')");
        Assert.Equal(StoreOutcome.Ok, await store.DeleteAlertAsync(u, id.Value, TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.alert_fire WHERE alert_id = {id}"));
        Assert.Equal(StoreOutcome.NotFound, await store.DeleteAlertAsync(u, id.Value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAlertOnAnUnknownMarketIsRefusedAndTheLimitIsTwoHundred()
    {
        db.Require();
        var u = await UserAsync("ud-alert-limit");
        await ReaderSeed.MarketAsync(db, "UD-LIM");
        var store = Store();

        Assert.Equal(StoreOutcome.MarketNotFound, (await store.AddAlertAsync(u, "UD-NOPE", AlertKind.PriceAbove, 1, 60, Now, TestContext.Current.CancellationToken)).Outcome);
        for (var i = 0; i < UserDataStore.MaxAlerts; i++)
        {
            Assert.Equal(StoreOutcome.Ok, (await store.AddAlertAsync(u, "UD-LIM", AlertKind.PriceAbove, 1 + i, 60, Now, TestContext.Current.CancellationToken)).Outcome);
        }

        Assert.Equal(StoreOutcome.LimitReached, (await store.AddAlertAsync(u, "UD-LIM", AlertKind.PriceAbove, 999, 60, Now, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task FiresComeOldestFirstTheNewestOnesWithoutACursorAndOnlyTheCallersOwn()
    {
        db.Require();
        var u = await UserAsync("ud-fires");
        var other = await UserAsync("ud-fires-other");
        var market = await ReaderSeed.MarketAsync(db, "UD-FIRE");
        var store = Store();
        var (_, id) = await store.AddAlertAsync(u, "UD-FIRE", AlertKind.PriceAbove, 500_000, 60, Now, TestContext.Current.CancellationToken);
        for (var i = 0; i < 5; i++)
        {
            await db.ExecuteAsync($"INSERT dbo.alert_fire (alert_id, user_id, market_id, kind, threshold, value, fired_at) VALUES ({id}, {u}, {market}, 'price_above', 500000, {510_000 + i}, '2026-03-11T12:3{i}:00')");
        }

        await db.ExecuteAsync($"INSERT dbo.alert_fire (alert_id, user_id, market_id, kind, threshold, value, fired_at) VALUES ({id}, {other}, {market}, 'price_above', 500000, 1, '2026-03-11T12:39:00')");

        var newest = await store.ReadFiresAsync(u, null, 3, TestContext.Current.CancellationToken);
        var after = await store.ReadFiresAsync(u, newest[0].FireId, 10, TestContext.Current.CancellationToken);

        Assert.Equal(3, newest.Count);
        Assert.Equal(new long[] { 510_002, 510_003, 510_004 }, newest.Select(f => f.Value));
        Assert.Equal(2, after.Count);
        Assert.All(newest.Concat(after), f => Assert.Equal(("UD-FIRE", AlertKind.PriceAbove, 500_000L, DateTimeKind.Utc), (f.Ticker, f.Kind, f.Threshold, f.FiredAt.Kind)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadFiresAsync(u, null, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadFiresAsync(u, null, 1_001, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASettingIsSetReplacedAndCleared()
    {
        db.Require();
        var u = await UserAsync("ud-settings");
        var store = Store();

        Assert.Null(await store.GetSettingAsync(u, "time_zone", TestContext.Current.CancellationToken));
        await store.SetSettingAsync(u, "time_zone", "Asia/Tokyo", Now, TestContext.Current.CancellationToken);
        await store.SetSettingAsync(u, "time_zone", "Europe/London", Now.AddMinutes(1), TestContext.Current.CancellationToken);
        Assert.Equal("Europe/London", await store.GetSettingAsync(u, "time_zone", TestContext.Current.CancellationToken));
        await store.SetSettingAsync(u, "time_zone", null, Now, TestContext.Current.CancellationToken);

        Assert.Null(await store.GetSettingAsync(u, "time_zone", TestContext.Current.CancellationToken));
    }
}

public class AlertEvaluationStoreTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);

    private AlertDataReader Reader() => new(new DataSource(db.Options));

    private AlertEngineStore Engine() => new(new DataSource(db.Options));

    private Task TickAsync(int market, DateTime at, long? price, long? bid, long? ask, long? interest) =>
        db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, yes_ask_e6, open_interest_e2, volume_e2) VALUES ('{ReaderSeed.Time(at)}', {market}, {Sql(price)}, {Sql(bid)}, {Sql(ask)}, {Sql(interest)}, 1)"));

    private static string Sql(long? value) => value is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";

    private Task TradeAsync(int market, DateTime at, string id, long count) =>
        db.ExecuteAsync(FormattableString.Invariant($"INSERT dbo.trade (ts, market_id, trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block) VALUES ('{ReaderSeed.Time(at)}', {market}, '{id}', 500000, 500000, {count}, 1, 0)"));

    [Fact]
    public async Task TheLatestPriceBookAndOpenInterestAndTheOpenInterestAnHourAgoAreFound()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "AE-TICK");
        await TickAsync(m, Now.AddHours(-3), 400_000, 390_000, 410_000, 1_000);
        await TickAsync(m, Now.AddMinutes(-70), 410_000, 400_000, 420_000, 2_000);   // the last one at least an hour ago
        await TickAsync(m, Now.AddMinutes(-30), 450_000, 440_000, 470_000, 2_500);
        await TickAsync(m, Now.AddMinutes(-2), null, 445_000, 465_000, 3_000);        // no price in this one: the price comes from the one before
        await TickAsync(m, Now.AddMinutes(1), 999_000, 1, 2, 3);                      // after now: never seen

        var seen = (await Reader().ReadAsync([m], Now, TestContext.Current.CancellationToken))[m];

        Assert.Equal((450_000L, 20_000L, 3_000L, 2_000L), (seen.LastE6, seen.SpreadE6, seen.OpenInterestE2, seen.OpenInterestHourAgoE2));
        Assert.False(seen.PricePartial || seen.VolumePartial || seen.OiPartial);
    }

    [Fact]
    public async Task AQuietMarketIsFoundByTheWiderWindowsAndOneQuieterThanADayHasNothing()
    {
        db.Require();
        var quiet = await ReaderSeed.MarketAsync(db, "AE-QUIET");
        var gone = await ReaderSeed.MarketAsync(db, "AE-GONE");
        var busy = await ReaderSeed.MarketAsync(db, "AE-BUSY");
        await TickAsync(quiet, Now.AddHours(-20), 300_000, 290_000, 310_000, 7_000);
        await TickAsync(quiet, Now.AddHours(-30), 100_000, 90_000, 110_000, 6_000);
        await TickAsync(gone, Now.AddHours(-26), 300_000, 290_000, 310_000, 7_000);
        await TickAsync(busy, Now.AddSeconds(-10), 500_000, 490_000, 510_000, 9_000);
        await TickAsync(busy, Now.AddHours(-3), 450_000, 440_000, 460_000, 8_000);

        var seen = await Reader().ReadAsync([quiet, gone, busy], Now, TestContext.Current.CancellationToken);

        Assert.Equal((300_000L, 20_000L, 7_000L, 7_000L), (seen[quiet].LastE6, seen[quiet].SpreadE6, seen[quiet].OpenInterestE2, seen[quiet].OpenInterestHourAgoE2));
        Assert.Equal((500_000L, 20_000L, 9_000L, 8_000L), (seen[busy].LastE6, seen[busy].SpreadE6, seen[busy].OpenInterestE2, seen[busy].OpenInterestHourAgoE2));
        Assert.Equal(new AlertObservation(null, null, 0, 0, null, null, false, false, false), seen[gone]);
    }

    [Fact]
    public async Task TheRecentQuarterHourAndTheDayBeforeAreSummedSeparately()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "AE-VOL");
        await TradeAsync(m, Now.AddMinutes(-5), "r1", 100);
        await TradeAsync(m, Now.AddMinutes(-14), "r2", 50);
        await TradeAsync(m, Now.AddMinutes(-16), "b1", 700);
        await TradeAsync(m, Now.AddHours(-23), "b2", 300);
        await TradeAsync(m, Now.AddHours(-25), "old", 9_999);    // before the baseline starts
        await TradeAsync(m, Now.AddMinutes(1), "future", 9_999);

        var seen = (await Reader().ReadAsync([m], Now, TestContext.Current.CancellationToken))[m];

        Assert.Equal((150L, 1_000L), (seen.RecentVolumeE2, seen.BaselineVolumeE2));
    }

    [Fact]
    public async Task AMarketWithNothingGetsAnEmptyObservation()
    {
        db.Require();
        var m = await ReaderSeed.MarketAsync(db, "AE-NONE");

        var seen = (await Reader().ReadAsync([m], Now, TestContext.Current.CancellationToken))[m];

        Assert.Equal(new AlertObservation(null, null, 0, 0, null, null, false, false, false), seen);
        Assert.Empty(await Reader().ReadAsync([], Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AGapMakesOnlyTheWindowsItOverlapsPartialAndAMarketsOwnGapCountsOnlyForIt()
    {
        db.Require();
        var gapNow = Now.AddDays(30);   // its own moment: a gap that covers the whole feed must not reach into the other tests' windows
        var gapped = await ReaderSeed.MarketAsync(db, "AE-GAPPED");
        var clear = await ReaderSeed.MarketAsync(db, "AE-CLEAR");
        await db.ExecuteAsync($"INSERT dbo.ingest_gap (kind, market_id, started_at, ended_at) VALUES ('ws_lost', {gapped}, '{ReaderSeed.Time(gapNow.AddMinutes(-40))}', '{ReaderSeed.Time(gapNow.AddMinutes(-30))}')");
        await db.ExecuteAsync($"INSERT dbo.ingest_gap (kind, market_id, started_at, ended_at) VALUES ('ingest_down', NULL, '{ReaderSeed.Time(gapNow.AddMinutes(-55))}', '{ReaderSeed.Time(gapNow.AddMinutes(-50))}')");

        var seen = await Reader().ReadAsync([gapped, clear], gapNow, TestContext.Current.CancellationToken);

        Assert.Equal((false, false, true), (seen[gapped].PricePartial, seen[gapped].VolumePartial, seen[gapped].OiPartial));   // over before the quarter hour and the last minute, inside the hour
        Assert.Equal((false, false, true), (seen[clear].PricePartial, seen[clear].VolumePartial, seen[clear].OiPartial));     // the whole-feed gap is inside the hour too
        await db.ExecuteAsync($"INSERT dbo.ingest_gap (kind, market_id, started_at) VALUES ('ws_lost', {gapped}, '{ReaderSeed.Time(gapNow.AddSeconds(-20))}')");
        var open = await Reader().ReadAsync([gapped, clear], gapNow, TestContext.Current.CancellationToken);
        Assert.Equal((true, true, true), (open[gapped].PricePartial, open[gapped].VolumePartial, open[gapped].OiPartial));
        Assert.Equal((false, false, true), (open[clear].PricePartial, open[clear].VolumePartial, open[clear].OiPartial));
    }

    [Fact]
    public async Task ManyMarketsAreReadInOnePassEvenPastTheInsertChunk()
    {
        db.Require();
        var ids = new List<int>();
        var records = Enumerable.Range(0, 1_200).Select(i => new KalTerm.Data.Markets.MarketRecord("AE-MANY-" + i, "EV-MANY", "binary", "active", "Yes", "No")).ToList();
        var result = await new KalTerm.Data.Markets.MarketStore(new DataSource(db.Options)).UpsertAsync(records, Now, TestContext.Current.CancellationToken);
        ids.AddRange(records.Select(r => result.Ids[r.Ticker]));

        var seen = await Reader().ReadAsync(ids, Now, TestContext.Current.CancellationToken);

        Assert.Equal(1_200, seen.Count);
    }

    [Fact]
    public async Task OnlyAlertsThatAreNotPausedAreActiveAndTheirMemoryComesBack()
    {
        db.Require();
        var u = await new UserStore(new DataSource(db.Options)).AddUserAsync("ae-engine", "read", Now, TestContext.Current.CancellationToken);
        await ReaderSeed.MarketAsync(db, "AE-ENG");
        var store = new UserDataStore(new DataSource(db.Options));
        var (_, live) = await store.AddAlertAsync(u, "AE-ENG", AlertKind.SpreadAbove, 30_000, 45, Now, TestContext.Current.CancellationToken);
        var (_, paused) = await store.AddAlertAsync(u, "AE-ENG", AlertKind.PriceAbove, 700_000, 60, Now, TestContext.Current.CancellationToken);
        await store.SetAlertPausedAsync(u, paused!.Value, true, TestContext.Current.CancellationToken);
        await db.ExecuteAsync($"UPDATE dbo.alert SET armed = 1, last_fired_at = '{ReaderSeed.Time(Now.AddMinutes(-3))}' WHERE alert_id = {live}");

        var active = (await Engine().ActiveAsync(TestContext.Current.CancellationToken)).Single(a => a.UserId == u);

        Assert.Equal((live, AlertKind.SpreadAbove, 30_000L, 45), (active.AlertId, active.Kind, active.Threshold, active.CooldownSeconds));
        Assert.Equal((true, (DateTime?)Now.AddMinutes(-3)), (active.State.Armed, active.State.LastFiredAt));
    }

    [Fact]
    public async Task ApplyingOutcomesSavesEachAlertsMemoryAndRecordsTheFiresTogether()
    {
        db.Require();
        var u = await new UserStore(new DataSource(db.Options)).AddUserAsync("ae-apply", "read", Now, TestContext.Current.CancellationToken);
        var market = await ReaderSeed.MarketAsync(db, "AE-APPLY");
        var store = new UserDataStore(new DataSource(db.Options));
        await store.AddAlertAsync(u, "AE-APPLY", AlertKind.PriceAbove, 500_000, 60, Now, TestContext.Current.CancellationToken);
        await store.AddAlertAsync(u, "AE-APPLY", AlertKind.PriceBelow, 100_000, 60, Now, TestContext.Current.CancellationToken);
        var alerts = (await Engine().ActiveAsync(TestContext.Current.CancellationToken)).Where(a => a.UserId == u).ToList();

        var fires = await Engine().ApplyAsync(
            [new AlertUpdate(alerts[0], new AlertState(false, Now), 510_000), new AlertUpdate(alerts[1], new AlertState(true, null), null)],
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fires);
        Assert.Equal(1, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.alert_fire WHERE alert_id = {alerts[0].AlertId} AND value = 510000 AND market_id = {market} AND kind = 'price_above'"));
        Assert.Equal(0, await db.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.alert_fire WHERE alert_id = {alerts[1].AlertId}"));
        Assert.Equal((false, true), (await db.ScalarAsync<bool>($"SELECT armed FROM dbo.alert WHERE alert_id = {alerts[0].AlertId}"), await db.ScalarAsync<bool>($"SELECT armed FROM dbo.alert WHERE alert_id = {alerts[1].AlertId}")));
        Assert.Equal(Now, await db.ScalarAsync<DateTime>($"SELECT last_evaluated_at FROM dbo.alert WHERE alert_id = {alerts[1].AlertId}"));
        Assert.Equal(0, await Engine().ApplyAsync([], Now, TestContext.Current.CancellationToken));
    }
}

public class AlertVersionReaderTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task TheCounterIsZeroUntilTheRunnerRaisesItAndThenWhatItSays()
    {
        db.Require();
        var reader = new AlertVersionReader(new DataSource(db.Options));
        Assert.Equal(0, await reader.ReadAsync(TestContext.Current.CancellationToken));

        await db.ExecuteAsync("INSERT dbo.ingest_state (name, value, updated_at) VALUES ('analyzer.alerts.version', '12', SYSUTCDATETIME())");
        Assert.Equal(12, await reader.ReadAsync(TestContext.Current.CancellationToken));

        await db.ExecuteAsync("UPDATE dbo.ingest_state SET value = 'not a number' WHERE name = 'analyzer.alerts.version'");
        Assert.Equal(0, await reader.ReadAsync(TestContext.Current.CancellationToken));
    }
}
