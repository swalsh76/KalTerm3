using KalTerm.Trading.Account;

namespace KalTerm.Trading.Tests.Account;

public sealed class TradingStateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kalterm-state-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_folder, "s.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void AMissingFileIsAFreshState()
    {
        var state = TradingState.Load(Path_);

        Assert.Equal(("", false, 0L), (state.Day, state.Halted, state.TransferredE6));
    }

    [Fact]
    public void EverythingSurvivesARestart()
    {
        var state = new TradingState(Path_);
        state.Roll("2026-10-13");
        state.SetBaseline(-1_500_000);
        state.BeginTransfer("t-1", 0, 3, 2_000_000);
        state.Halt("daily loss");
        state.NoteUnknownOrder("coid-1");

        var again = TradingState.Load(Path_);

        Assert.Equal(("2026-10-13", -1_500_000L, 2_000_000L, true, "daily loss"), (again.Day, again.RealizedBaselineE6, again.TransferredE6, again.Halted, again.HaltReason));
        Assert.Equal(["coid-1"], again.UnknownOrders);
        Assert.Equal([new PendingTransfer("t-1", 0, 3, 2_000_000)], again.PendingTransfers);
    }

    [Fact]
    public void ASavedStateLeavesNoTemporaryFiles()
    {
        var state = new TradingState(Path_);
        state.Halt("x");
        state.ClearHalt();

        Assert.Equal(["s.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"day": "x"}""")]
    [InlineData("""{"day":"d","realized_baseline_e6":"x","transferred_e6":0,"halted":false}""")]
    [InlineData("""{"day":"d","realized_baseline_e6":null,"transferred_e6":0,"halted":false,"pending_transfers":[{"id":"a"}]}""")]
    public void ADamagedFileIsAnErrorNeverAReset(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path_, content);

        var thrown = Assert.Throws<TradingStateException>(() => TradingState.Load(Path_));

        Assert.Contains("refused", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllText(Path_));
    }

    [Fact]
    public void AFileThatCannotBeReadIsAnErrorToo()
    {
        Directory.CreateDirectory(Path_);   // a folder where the file should be

        Assert.Throws<TradingStateException>(() => TradingState.Load(Path_));
    }

    [Fact]
    public void ANewDayResetsTheCountersButNotTheHalt()
    {
        var state = new TradingState(Path_);
        state.Roll("2026-10-13");
        state.SetBaseline(5);
        state.BeginTransfer("t", 0, 1, 100);
        state.Halt("x");
        state.NoteUnknownOrder("c");

        Assert.False(state.Roll("2026-10-13"));
        Assert.Equal(100, state.TransferredE6);
        Assert.True(state.Roll("2026-10-14"));

        Assert.Equal((null, 0L), (state.RealizedBaselineE6, state.TransferredE6));
        Assert.True(state.Halted);
        Assert.Equal(["c"], state.UnknownOrders);
        Assert.Single(state.PendingTransfers);
    }

    [Fact]
    public void TheBaselineIsRecordedOncePerDay()
    {
        var state = new TradingState(Path_);
        Assert.Equal(0, state.RealizedToday(7_000_000));

        state.SetBaseline(1_000_000);
        state.SetBaseline(9_999_999);

        Assert.Equal(-600_000, state.RealizedToday(400_000));
    }

    [Fact]
    public void TransfersAreCountedBeforeTheyAreConfirmedAndTakenBackIfRefused()
    {
        var state = new TradingState(Path_);
        state.BeginTransfer("a", 0, 1, 300);
        state.BeginTransfer("b", 0, 1, 500);
        Assert.Equal(800, TradingState.Load(Path_).TransferredE6);

        state.FinishTransfer("a");
        state.AbortTransfer("b");
        state.AbortTransfer("unknown");

        Assert.Equal(300, state.TransferredE6);
        Assert.Empty(state.PendingTransfers);
    }

    [Fact]
    public void UnknownOrdersAreRememberedUntilResolved()
    {
        var state = new TradingState(Path_);
        state.NoteUnknownOrder("c1");
        state.NoteUnknownOrder("c1");
        Assert.Equal(["c1"], state.UnknownOrders);

        state.ResolveUnknownOrder("c1");
        state.ResolveUnknownOrder("c1");

        Assert.Empty(TradingState.Load(Path_).UnknownOrders);
    }

    [Fact]
    public void AHaltCanBeCleared()
    {
        var state = new TradingState(Path_);
        state.Halt("kill switch");
        state.ClearHalt();

        Assert.False(TradingState.Load(Path_).Halted);
    }

    [Fact]
    public void TheDefaultPathIsNextToTheRiskFile()
    {
        Assert.Equal(Path.GetDirectoryName(KalTerm.Trading.Risk.RiskPolicyLoader.DefaultPath()), Path.GetDirectoryName(TradingState.DefaultPath()));
        Assert.EndsWith("trading_state.json", TradingState.DefaultPath(), StringComparison.Ordinal);
    }

    [Fact]
    public void DaysAreUtc()
    {
        Assert.Equal("2026-10-14", TradingState.UtcDay(new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.FromHours(-5))));
        Assert.Equal("2026-10-13", TradingState.UtcDay(new DateTimeOffset(2026, 10, 13, 23, 59, 0, TimeSpan.Zero)));
    }
}
