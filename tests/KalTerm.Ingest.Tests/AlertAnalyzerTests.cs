using KalTerm.Data.Alerts;
using KalTerm.Ingest.Analytics;

namespace KalTerm.Ingest.Tests;

public class AlertAnalyzerTests
{
    private static readonly DateTime Now = new(2026, 3, 11, 12, 30, 0, DateTimeKind.Utc);

    private sealed class FakeEngine(params ActiveAlert[] alerts) : IAlertEngineStore
    {
        public List<IReadOnlyList<AlertUpdate>> Applied { get; } = [];

        public Task<IReadOnlyList<ActiveAlert>> ActiveAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ActiveAlert>>(alerts);

        public Task<int> ApplyAsync(IReadOnlyList<AlertUpdate> updates, DateTime now, CancellationToken cancellationToken = default)
        {
            Applied.Add(updates);
            return Task.FromResult(updates.Count(u => u.FireValue is not null));
        }
    }

    private sealed class FakeReader(Dictionary<int, AlertObservation> seen) : IAlertDataReader
    {
        public List<int[]> Asked { get; } = [];

        public Task<IReadOnlyDictionary<int, AlertObservation>> ReadAsync(IReadOnlyCollection<int> marketIds, DateTime now, CancellationToken cancellationToken = default)
        {
            Asked.Add([.. marketIds.Order()]);
            return Task.FromResult<IReadOnlyDictionary<int, AlertObservation>>(seen);
        }
    }

    private static ActiveAlert Alert(int id, int market, AlertKind kind, long threshold, bool? armed, DateTime? lastFired = null) =>
        new(id, 1, market, kind, threshold, 60, new AlertState(armed, lastFired));

    private static AlertObservation Price(long last, bool partial = false) => new(last, 10_000, 0, 0, null, null, partial, false, false);

    [Fact]
    public async Task WithoutAlertsNothingIsReadOrWritten()
    {
        var engine = new FakeEngine();
        var reader = new FakeReader([]);

        var outcome = await new AlertAnalyzer(engine, reader).RunAsync(new AnalysisContext(Now), TestContext.Current.CancellationToken);

        Assert.Equal(new AnalyzerOutcome(0, false), outcome);
        Assert.Empty(reader.Asked);
        Assert.Empty(engine.Applied);
    }

    [Fact]
    public async Task AReadyAlertWhoseConditionHoldsFiresWithTheObservedValue()
    {
        var engine = new FakeEngine(Alert(1, 10, AlertKind.PriceAbove, 600_000, armed: true));
        var reader = new FakeReader(new() { [10] = Price(610_000) });

        var outcome = await new AlertAnalyzer(engine, reader).RunAsync(new AnalysisContext(Now), TestContext.Current.CancellationToken);

        var update = Assert.Single(Assert.Single(engine.Applied));
        Assert.Equal((610_000L, (bool?)false, (DateTime?)Now), (update.FireValue, update.State.Armed, update.State.LastFiredAt));
        Assert.Equal(new AnalyzerOutcome(1, true, "1 judged, 0 skipped, 1 fired"), outcome);
    }

    [Fact]
    public async Task TheFirstJudgmentOnlyRecordsWhatHoldsAndTheOutcomeIsNotAChange()
    {
        var engine = new FakeEngine(Alert(1, 10, AlertKind.PriceAbove, 600_000, armed: null));
        var reader = new FakeReader(new() { [10] = Price(610_000) });

        var outcome = await new AlertAnalyzer(engine, reader).RunAsync(new AnalysisContext(Now), TestContext.Current.CancellationToken);

        var update = Assert.Single(Assert.Single(engine.Applied));
        Assert.Equal((null, (bool?)false), (update.FireValue, update.State.Armed));
        Assert.False(outcome.Changed);
    }

    [Fact]
    public async Task AnAlertWhoseDataIsIncompleteOrMissingIsSkippedNotJudged()
    {
        var engine = new FakeEngine(
            Alert(1, 10, AlertKind.PriceAbove, 1, armed: true),    // a gap overlaps
            Alert(2, 11, AlertKind.PriceAbove, 1, armed: true),    // nothing known of the market
            Alert(3, 12, AlertKind.PriceAbove, 1, armed: true));   // fine
        var reader = new FakeReader(new() { [10] = Price(500_000, partial: true), [12] = Price(500_000) });

        var outcome = await new AlertAnalyzer(engine, reader).RunAsync(new AnalysisContext(Now), TestContext.Current.CancellationToken);

        Assert.Equal([3], Assert.Single(engine.Applied).Select(u => u.Alert.AlertId));
        Assert.Equal("1 judged, 2 skipped, 1 fired", outcome.Message);
    }

    [Fact]
    public async Task EachMarketIsAskedForOnceEvenWithSeveralAlertsOnIt()
    {
        var engine = new FakeEngine(Alert(1, 10, AlertKind.PriceAbove, 1, true), Alert(2, 10, AlertKind.PriceBelow, 999_999, true), Alert(3, 20, AlertKind.SpreadAbove, 1, true));
        var reader = new FakeReader(new() { [10] = Price(500_000), [20] = Price(500_000) });

        await new AlertAnalyzer(engine, reader).RunAsync(new AnalysisContext(Now), TestContext.Current.CancellationToken);

        Assert.Equal([10, 20], Assert.Single(reader.Asked));
        Assert.Equal(3, Assert.Single(engine.Applied).Count);
    }

    [Fact]
    public async Task AnAlertInItsCooldownDoesNotFireAgain()
    {
        var engine = new FakeEngine(Alert(1, 10, AlertKind.PriceAbove, 1, armed: true, lastFired: Now.AddSeconds(-20)));
        var reader = new FakeReader(new() { [10] = Price(500_000) });

        var outcome = await new AlertAnalyzer(engine, reader).RunAsync(new AnalysisContext(Now), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(Assert.Single(engine.Applied)).FireValue);
        Assert.False(outcome.Changed);
    }

    [Fact]
    public void ItIsCalledAlertsAndRunsEveryTenSecondsUnlessToldOtherwise()
    {
        var analyzer = new AlertAnalyzer(new FakeEngine(), new FakeReader([]));

        Assert.Equal(("alerts", TimeSpan.FromSeconds(10)), (analyzer.Name, analyzer.Interval));
        Assert.Equal(TimeSpan.FromSeconds(3), new AlertAnalyzer(new FakeEngine(), new FakeReader([]), TimeSpan.FromSeconds(3)).Interval);
    }
}
