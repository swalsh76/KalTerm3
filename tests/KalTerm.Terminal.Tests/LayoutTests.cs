using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KalTerm.Client;
using KalTerm.Terminal.Alerts;
using KalTerm.Terminal.Layouts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Shell;
using KalTerm.Terminal.Status;
using KalTerm.Terminal.Views;

namespace KalTerm.Terminal.Tests;

internal static class LayoutSamples
{
    public static LayoutState State(string page = "Markets", string status = "closed", string? list = null, bool sound = true, double width = 1000) => new(
        LayoutState.Current,
        new WindowLayout(10, 20, width, 640, false),
        page,
        new MarketsLayout(status, "5", list, "spread", false, [new ColumnLayout("Last", 90, 3), new ColumnLayout("Ticker", 200, 0)]),
        new ChartLayout("15m", "1 day", 20),
        sound);

    public static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement;
}

public sealed class LayoutStateTests
{
    [Fact]
    public void ALayoutSurvivesTheServerAndLeavesOutWhatIsNotSet()
    {
        var state = LayoutSamples.State();

        var again = LayoutState.FromJson(LayoutSamples.Raw(state.ToJson().GetRawText()));

        Assert.Equal(state.Window, again!.Window);
        Assert.Equal(state.Markets!.Columns, again.Markets!.Columns);
        Assert.Equal(("Markets", "closed", "spread", false, "15m", 20, true), (again.Page, again.Markets.Status, again.Markets.Sort, again.Markets.Descending, again.Chart!.Interval, again.Chart.BookDepth, again.Sound));
        var bare = new LayoutState(1, null, null, null, null, null).ToJson().GetRawText();
        Assert.Equal("""{"version":1}""", bare);
    }

    [Theory]
    [InlineData("""{"version":"one"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""42""")]
    public void ALayoutThatIsNotOneThisTerminalCanReadIsNull(string json) => Assert.Null(LayoutState.FromJson(LayoutSamples.Raw(json)));

    [Fact]
    public void FieldsANewerTerminalAddedAreIgnored()
    {
        var read = LayoutState.FromJson(LayoutSamples.Raw("""{"version":1,"page":"Alerts","something_new":{"a":1}}"""));

        Assert.Equal("Alerts", read!.Page);
    }
}

public sealed class WindowPlacementTests
{
    [Fact]
    public void APlaceOnTheScreensIsKept()
    {
        var saved = new WindowLayout(100, 50, 1100, 720, false);

        Assert.Equal(saved, WindowPlacement.Fit(saved, 0, 0, 1920, 1080));
    }

    [Theory]
    [InlineData(3000, 100, 1820, 100)]   // on a monitor that is gone: moved back so a grabbable part is visible
    [InlineData(-5000, 100, -1000, 100)]
    [InlineData(100, 4000, 100, 1040)]
    [InlineData(100, -300, 100, 0)]
    public void APlaceOffTheScreensIsMovedBackOnto(double left, double top, double expectedLeft, double expectedTop)
    {
        var fit = WindowPlacement.Fit(new WindowLayout(left, top, 1100, 720, false), 0, 0, 1920, 1080);

        Assert.Equal((expectedLeft, expectedTop), (fit.Left, fit.Top));
    }

    [Fact]
    public void ASizeIsKeptBetweenTheWindowsMinimumAndTheScreens()
    {
        var huge = WindowPlacement.Fit(new WindowLayout(0, 0, 9000, 9000, true), 0, 0, 1920, 1080);
        var tiny = WindowPlacement.Fit(new WindowLayout(0, 0, 10, 10, false), 0, 0, 1920, 1080);
        var broken = WindowPlacement.Fit(new WindowLayout(double.NaN, double.NaN, double.NaN, double.PositiveInfinity, false), 0, 0, 1920, 1080);

        Assert.Equal((1920d, 1080d, true), (huge.Width, huge.Height, huge.Maximized));
        Assert.Equal((WindowPlacement.MinWidth, WindowPlacement.MinHeight), (tiny.Width, tiny.Height));
        Assert.Equal((0d, 0d, WindowPlacement.MinWidth, WindowPlacement.MinHeight), (broken.Left, broken.Top, broken.Width, broken.Height));
        Assert.Throws<ArgumentNullException>(() => WindowPlacement.Fit(null!, 0, 0, 1, 1));
    }
}

public sealed class ViewPreferencesTests
{
    [Fact]
    public void ChoicesAreRememberedAnnouncedOnceAndRestoredWithoutAnnouncement()
    {
        var prefs = new ViewPreferences();
        var changes = 0;
        prefs.Changed += () => changes++;

        prefs.SetChart("1h", "1 week");
        prefs.SetChart("1h", "1 week");
        prefs.SetBookDepth(20);
        prefs.SetBookDepth(20);
        Assert.Equal(2, changes);
        Assert.Equal(new ChartLayout("1h", "1 week", 20), prefs.Capture());

        prefs.Apply(new ChartLayout("5m", null, null));
        prefs.Apply(null);

        Assert.Equal((2, "5m", null, null), (changes, prefs.ChartInterval, prefs.ChartRange, prefs.BookDepth));
    }
}

public sealed class LayoutServiceTests : IDisposable
{
    private readonly FakeApi _api = new();
    private readonly LayoutService _service;

    public LayoutServiceTests() => _service = new LayoutService(_api);

    public void Dispose() => _api.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NamesAreListedAndAFailureIsAnswerInWords()
    {
        _api.LayoutRows["b"] = LayoutSamples.Raw("{}");
        _api.LayoutRows["a"] = LayoutSamples.Raw("{}");
        var (listed, none) = await _service.ListAsync(Ct);
        Assert.Equal(["a", "b"], listed);
        Assert.Null(none);

        _api.LayoutFails = new ApiUnavailableException("no route");
        var (names, problem) = await _service.ListAsync(Ct);

        Assert.Empty(names);
        Assert.Equal("Could not read your layouts: no route", problem);
    }

    [Fact]
    public async Task ALayoutIsReadSavedAndDeleted()
    {
        var state = LayoutSamples.State();

        Assert.Null(await _service.SaveAsync("desk", state, Ct));
        var read = await _service.ReadAsync("desk", Ct);
        Assert.Null(await _service.DeleteAsync("desk", Ct));

        Assert.Equal(state.Window, read.State!.Window);
        Assert.Equal((false, null), (read.Missing, read.Problem));
        Assert.Empty(_api.LayoutRows);
    }

    [Fact]
    public async Task AMissingLayoutIsMissingNotAProblem()
    {
        var read = await _service.ReadAsync("nothing", Ct);

        Assert.Equal((null, true, null), (read.State, read.Missing, read.Problem));
    }

    [Fact]
    public async Task ALayoutThatCannotBeUsedIsAProblemAndNotMissing()
    {
        _api.LayoutRows["broken"] = LayoutSamples.Raw("""{"version":"x"}""");
        _api.LayoutRows["future"] = LayoutSamples.Raw("""{"version":2}""");

        var broken = await _service.ReadAsync("broken", Ct);
        var future = await _service.ReadAsync("future", Ct);

        Assert.Contains("could not be read", broken.Problem, StringComparison.Ordinal);
        Assert.Contains("newer KalTerm", future.Problem, StringComparison.Ordinal);
        Assert.All([broken, future], r => Assert.Equal((null, false), (r.State, r.Missing)));
    }

    [Fact]
    public async Task TheServersRefusalsAreAnsweredInPlainWords()
    {
        var state = LayoutSamples.State();

        _api.LayoutFails = new ApiException(HttpStatusCode.Conflict, "limit_reached");
        Assert.Equal("You already have 20 layouts. Delete one first.", await _service.SaveAsync("x", state, Ct));
        _api.LayoutFails = new ApiException(HttpStatusCode.BadRequest, "invalid_parameter");
        Assert.Equal("The server did not accept the layout x.", await _service.SaveAsync("x", state, Ct));
        _api.LayoutFails = new ApiUnavailableException("down");
        Assert.Equal("Could not save the layout x: down", await _service.SaveAsync("x", state, Ct));
        _api.LayoutFails = new ApiUnavailableException("down");
        Assert.Equal("Could not read the layout x: down", (await _service.ReadAsync("x", Ct)).Problem);
        _api.LayoutFails = new ApiUnavailableException("down");
        Assert.Equal("Could not delete the layout x: down", await _service.DeleteAsync("x", Ct));
        _api.LayoutFails = new ApiException(HttpStatusCode.NotFound, "not_found");
        Assert.Null(await _service.DeleteAsync("x", Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.SaveAsync("x", null!, Ct));
    }
}

internal sealed class FakeTarget : ILayoutTarget
{
    public LayoutState Now { get; set; } = LayoutSamples.State();

    public List<LayoutState> Applied { get; } = [];

    public Action? OnApply { get; set; }

    public LayoutState Capture() => Now;

    public void Apply(LayoutState state)
    {
        Applied.Add(state);
        OnApply?.Invoke();
    }
}

public sealed class LayoutCoordinatorTests : IDisposable
{
    private const string Key = "kaldb:5280";

    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeTarget _target = new();
    private readonly LayoutCoordinator _coordinator;

    public LayoutCoordinatorTests() => _coordinator = new LayoutCoordinator(_target, _dialogs, _t.Settings, _t.Time);

    public void Dispose()
    {
        _coordinator.Dispose();
        _t.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task StartAsync() => _coordinator.StartAsync(_t.Api, Key);

    private Task SavedAsync(string name = "default") => Wait.FireUntilAsync(_t.Time, () => _t.Api.LayoutRows.ContainsKey(name));

    [Fact]
    public async Task WithNoLayoutYetTheDefaultNameIsUsedAndTheFirstChangeMakesIt()
    {
        await StartAsync();

        Assert.Equal((LayoutCoordinator.DefaultName, true, string.Empty), (_coordinator.Current, _coordinator.Saving, _coordinator.Problem));
        Assert.Equal(["default"], _coordinator.Names);
        Assert.Empty(_target.Applied);
        Assert.Equal("default", _t.Settings.LoadLayoutName(Key));
        Assert.DoesNotContain(_t.Api.LayoutCalls, c => c.StartsWith("put", StringComparison.Ordinal));

        _coordinator.Changed();
        await SavedAsync();

        Assert.Equal(LayoutSamples.State().Window, LayoutState.FromJson(_t.Api.LayoutRows["default"])!.Window);
    }

    [Fact]
    public async Task TheLayoutLastUsedOnThisComputerIsReadAndAppliedAndOthersAreListed()
    {
        _t.Settings.SaveLayoutName(Key, "desk");
        _t.Api.LayoutRows["desk"] = LayoutSamples.State(page: "Alerts").ToJson();
        _t.Api.LayoutRows["laptop"] = LayoutSamples.State().ToJson();

        await StartAsync();

        Assert.Equal("desk", _coordinator.Current);
        Assert.Equal(["desk", "laptop"], _coordinator.Names);
        Assert.Equal("Alerts", Assert.Single(_target.Applied).Page);
        Assert.True(_coordinator.Saving);
    }

    [Fact]
    public async Task ChangesMadeWhileALayoutIsBeingAppliedAreNotSaved()
    {
        _t.Api.LayoutRows["default"] = LayoutSamples.State().ToJson();
        _target.OnApply = _coordinator.Changed;

        await StartAsync();

        Assert.Equal(0, _t.Time.PendingTimers);
    }

    [Fact]
    public async Task ManyChangesInARowAreOneSave()
    {
        await StartAsync();

        _coordinator.Changed();
        _coordinator.Changed();
        _coordinator.Changed();
        await SavedAsync();
        await Task.Delay(50, Ct);

        Assert.Single(_t.Api.LayoutCalls, c => c.StartsWith("put", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALayoutThatCannotBeUsedIsNeverWrittenOver()
    {
        _t.Settings.SaveLayoutName(Key, "desk");
        _t.Api.LayoutRows["desk"] = LayoutSamples.Raw("""{"version":2}""");

        await StartAsync();
        _coordinator.Changed();
        await _coordinator.FlushAsync(Ct);

        Assert.False(_coordinator.Saving);
        Assert.Contains("newer KalTerm", _coordinator.Problem, StringComparison.Ordinal);
        Assert.True(_coordinator.HasProblem);
        Assert.Equal("""{"version":2}""", _t.Api.LayoutRows["desk"].GetRawText());
        Assert.Empty(_target.Applied);
    }

    [Fact]
    public async Task IfTheLayoutsCannotBeReadNothingIsSavedUntilTheyCanBe()
    {
        _t.Api.LayoutFails = new ApiUnavailableException("no route");

        await StartAsync();
        _coordinator.Changed();
        await _coordinator.FlushAsync(Ct);

        Assert.False(_coordinator.Saving);
        Assert.Contains("Could not read your layouts: no route", _coordinator.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain(_t.Api.LayoutCalls, c => c.StartsWith("put", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlushingSavesAtOnceWhatChangedAndNothingWhenNothingDid()
    {
        await StartAsync();
        await _coordinator.FlushAsync(Ct);
        Assert.DoesNotContain(_t.Api.LayoutCalls, c => c.StartsWith("put", StringComparison.Ordinal));

        _coordinator.Changed();
        await _coordinator.FlushAsync(Ct);
        await _coordinator.FlushAsync(Ct);

        Assert.Single(_t.Api.LayoutCalls, c => c.StartsWith("put", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedSaveIsShownKeptForRetryAndClearedByTheNextGoodOne()
    {
        await StartAsync();
        _coordinator.Changed();
        _t.Api.LayoutFails = new ApiUnavailableException("down");
        await _coordinator.FlushAsync(Ct);
        Assert.Equal("Could not save the layout default: down", _coordinator.Problem);

        await _coordinator.FlushAsync(Ct);

        Assert.Equal(string.Empty, _coordinator.Problem);
        Assert.True(_t.Api.LayoutRows.ContainsKey("default"));
    }

    [Fact]
    public async Task ChoosingAnotherLayoutSavesTheOneInUseThenReadsAndAppliesTheOther()
    {
        _t.Api.LayoutRows["desk"] = LayoutSamples.State(page: "Alerts").ToJson();
        _t.Api.LayoutRows["default"] = LayoutSamples.State().ToJson();
        await StartAsync();
        _target.Applied.Clear();
        _target.Now = LayoutSamples.State(width: 1234);
        _coordinator.Changed();

        _coordinator.Current = "desk";
        await Wait.UntilAsync(() => _target.Applied.Count == 1);

        Assert.Equal("desk", _coordinator.Current);
        Assert.Equal(1234, LayoutState.FromJson(_t.Api.LayoutRows["default"])!.Window!.Width);
        Assert.Equal("Alerts", _target.Applied[0].Page);
        Assert.Equal("desk", _t.Settings.LoadLayoutName(Key));
        _coordinator.Current = string.Empty;
        _coordinator.Current = "desk";
    }

    [Fact]
    public async Task SavingAsMakesANewLayoutAndSwitchesToIt()
    {
        await StartAsync();
        _dialogs.Name = "mine";

        await _coordinator.SaveAsCommand.RunAsync();

        Assert.Equal("mine", _coordinator.Current);
        Assert.Contains("mine", _coordinator.Names);
        Assert.Equal(LayoutSamples.State().Window, LayoutState.FromJson(_t.Api.LayoutRows["mine"])!.Window);
        Assert.Equal("mine", _t.Settings.LoadLayoutName(Key));
        Assert.Equal("Save layout as: A name for this layout (lower-case letters, digits, dots, dashes and underscores).", _dialogs.Asked.Single());
        Assert.True(_coordinator.Saving);
    }

    [Fact]
    public async Task SavingAsCanBeGivenUpAndAFailureIsShown()
    {
        await StartAsync();
        _dialogs.Name = null;
        await _coordinator.SaveAsCommand.RunAsync();
        Assert.Empty(_t.Api.LayoutRows);

        _dialogs.Name = "mine";
        _t.Api.LayoutFails = new ApiException(HttpStatusCode.Conflict, "limit_reached");
        await _coordinator.SaveAsCommand.RunAsync();

        Assert.Equal("You already have 20 layouts. Delete one first.", _coordinator.Problem);
        Assert.Equal("default", _coordinator.Current);
    }

    [Fact]
    public async Task SavingAsOverAnUnusableLayoutMakesItUsableAgain()
    {
        _t.Settings.SaveLayoutName(Key, "desk");
        _t.Api.LayoutRows["desk"] = LayoutSamples.Raw("""{"version":2}""");
        await StartAsync();
        Assert.False(_coordinator.Saving);
        _dialogs.Name = "desk2";

        await _coordinator.SaveAsCommand.RunAsync();

        Assert.True(_coordinator.Saving);
        Assert.Equal(string.Empty, _coordinator.Problem);
    }

    [Fact]
    public async Task DeletingAsksFirstThenRemovesTheLayoutAndGoesBackToTheDefault()
    {
        _t.Settings.SaveLayoutName(Key, "desk");
        _t.Api.LayoutRows["desk"] = LayoutSamples.State().ToJson();
        _t.Api.LayoutRows["default"] = LayoutSamples.State(page: "Alerts").ToJson();
        await StartAsync();
        _target.Applied.Clear();
        _dialogs.Confirm = false;

        await _coordinator.DeleteCommand.RunAsync();
        Assert.True(_t.Api.LayoutRows.ContainsKey("desk"));
        _dialogs.Confirm = true;
        await _coordinator.DeleteCommand.RunAsync();

        Assert.False(_t.Api.LayoutRows.ContainsKey("desk"));
        Assert.Equal(("default", "Alerts"), (_coordinator.Current, _target.Applied.Single().Page));
        Assert.Equal(["default"], _coordinator.Names);
        Assert.Contains("Delete the layout desk?", _dialogs.Asked[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingTheDefaultLeavesTheTerminalAsItIsAndTheNextChangeSavesItAgain()
    {
        _t.Api.LayoutRows["default"] = LayoutSamples.State().ToJson();
        await StartAsync();

        await _coordinator.DeleteCommand.RunAsync();
        Assert.False(_t.Api.LayoutRows.ContainsKey("default"));
        Assert.Equal("default", _coordinator.Current);
        Assert.Equal(["default"], _coordinator.Names);

        _coordinator.Changed();
        await SavedAsync();
    }

    [Fact]
    public async Task ADeleteThatFailsIsShownAndKeepsTheLayout()
    {
        _t.Api.LayoutRows["default"] = LayoutSamples.State().ToJson();
        await StartAsync();
        _t.Api.LayoutFails = new ApiUnavailableException("down");

        await _coordinator.DeleteCommand.RunAsync();

        Assert.Equal("Could not delete the layout default: down", _coordinator.Problem);
        Assert.True(_t.Api.LayoutRows.ContainsKey("default"));
    }

    [Fact]
    public async Task DetachingStopsEverything()
    {
        await StartAsync();
        _coordinator.Changed();

        _coordinator.Detach();
        _coordinator.Changed();
        await _coordinator.FlushAsync(Ct);

        Assert.Empty(_coordinator.Names);
        Assert.False(_coordinator.Saving);
        Assert.False(_coordinator.SaveAsCommand.CanExecute(null));
        Assert.DoesNotContain(_t.Api.LayoutCalls, c => c.StartsWith("put", StringComparison.Ordinal));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _coordinator.StartAsync(null!, Key));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _coordinator.StartAsync(_t.Api, null!));
    }
}
