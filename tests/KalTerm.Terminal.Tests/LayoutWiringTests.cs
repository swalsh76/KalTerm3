using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KalTerm.Contracts;
using KalTerm.Terminal.Alerts;
using KalTerm.Terminal.Layouts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Shell;
using KalTerm.Terminal.Status;
using KalTerm.Terminal.Trading;
using KalTerm.Terminal.Views;

namespace KalTerm.Terminal.Tests;

public sealed class MarketsLayoutTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly MarketsViewModel _model;
    private int _changes;

    public MarketsLayoutTests()
    {
        _model = new MarketsViewModel(_format, _t.Time, InlineContext.Instance, _dialogs);
        _model.LayoutChanged += () => _changes++;
        _t.Api.ScreenerRows.Add(Samples.Row("KX-A"));
        _t.Api.Lists["macro"] = ["KX-A"];
    }

    public void Dispose()
    {
        _model.Dispose();
        _t.Dispose();
    }

    private async Task OpenAsync()
    {
        _model.Attach(() => _t.Api);
        await _model.LoadAsync();
    }

    [Fact]
    public void TheStartingPageCapturesItsDefaults()
    {
        var layout = _model.CaptureLayout();

        Assert.Equal(("active", null, null, "volume", true), (layout.Status, layout.MinVolume, layout.List, layout.Sort, layout.Descending));
        Assert.Empty(layout.Columns!);
    }

    [Fact]
    public async Task ALayoutAppliedBeforeTheServerIsKnownIsWhatTheFirstLoadAsksFor()
    {
        _model.ApplyLayout(new MarketsLayout("closed", "5", "macro", "spread", false, [new ColumnLayout("Last", 90, 3)]));
        Assert.Empty(_t.Api.ScreenQueries);

        await OpenAsync();

        var query = Assert.Single(_t.Api.ScreenQueries);
        Assert.Equal(("spread", "asc", null, "5", "macro"), (query.Sort, query.Order, query.Status, query.MinVolume, query.Watchlist));   // a list shows every status
        Assert.Equal(("macro", "Smallest first", "5"), (_model.ListFilter.Name, _model.OrderText, _model.MinVolumeText));
        Assert.Equal(["All markets", "macro (1)"], _model.ListChoices.Select(c => c.Text));
    }

    [Fact]
    public void WhatIsCapturedWhileAListIsChosenIsTheStatusToGoBackTo()
    {
        _model.ApplyLayout(new MarketsLayout("determined", null, "macro", null, null, null));

        var layout = _model.CaptureLayout();

        Assert.Equal(("determined", "macro"), (layout.Status, layout.List));
        Assert.Null(_model.StatusFilter.Value);
        _model.ApplyLayout(new MarketsLayout("any", null, null, null, null, null));
        Assert.Equal(("any", null), (_model.CaptureLayout().Status, _model.CaptureLayout().List));
    }

    [Fact]
    public async Task ALayoutAppliedToAPageWithAServerAsksOnce()
    {
        await OpenAsync();

        _model.ApplyLayout(new MarketsLayout("closed", "7", null, "change", true, null));
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 2);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(2, _t.Api.ScreenQueries.Count);
        Assert.Equal(("change", "desc", "closed", "7"), (_t.Api.ScreenQueries[1].Sort, _t.Api.ScreenQueries[1].Order, _t.Api.ScreenQueries[1].Status, _t.Api.ScreenQueries[1].MinVolume));
    }

    [Fact]
    public void ThingsALayoutCannotMeanAreLeftAlone()
    {
        _model.ApplyLayout(null);
        _model.ApplyLayout(new MarketsLayout("no-such-status", "not a number", null, "no-such-sort", null, null));

        Assert.Equal(("active", string.Empty, "volume"), (_model.StatusFilter.Value, _model.MinVolumeText, _model.Sort.Key));
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void ChangesToWhatTheLayoutHoldsAreAnnouncedButTheSearchAndALayoutBeingAppliedAreNot()
    {
        _model.SearchText = "rain";
        Assert.Equal(0, _changes);

        _model.MinVolumeText = "5";
        _model.StatusFilter = _model.Statuses[2];
        _model.Sort = _model.Sorts[1];
        _model.Descending = false;
        _model.Preferences.SetBookDepth(20);
        Assert.Equal(5, _changes);

        _model.ApplyLayout(new MarketsLayout("closed", "9", null, "last", true, [new ColumnLayout("Last", 90, 0)]));
        Assert.Equal(5, _changes);
    }

    [Fact]
    public void ColumnsReportedByTheGridAreRememberedAndAnnouncedOnlyWhenTheyDiffer()
    {
        IReadOnlyList<ColumnLayout> columns = [new ColumnLayout("Last", 90, 1), new ColumnLayout("Ticker", 0, 0)];
        var applied = 0;
        _model.ColumnsApplied += () => applied++;

        _model.SetColumns(columns);
        _model.SetColumns([.. columns]);
        _model.ApplyLayout(new MarketsLayout(null, null, null, null, null, [new ColumnLayout("Last", 100, 0)]));

        Assert.Equal((1, 1), (_changes, applied));
        Assert.Equal(100, _model.Columns[0].Width);
        Assert.Throws<ArgumentNullException>(() => _model.SetColumns(null!));
    }

    [Fact]
    public async Task AListRemovedElsewhereIsDroppedOnceTheListsAreRead()
    {
        _model.ApplyLayout(new MarketsLayout(null, null, "gone", null, null, null));

        await OpenAsync();

        Assert.Null(_model.ListFilter.Name);
        Assert.Equal(["All markets", "macro (1)"], _model.ListChoices.Select(c => c.Text));
    }
}

public sealed class ChartPreferencesTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly MarketFeed _feed;

    public ChartPreferencesTests() => _feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);

    public void Dispose()
    {
        _feed.Dispose();
        _t.Dispose();
    }

    [Fact]
    public void AChartStartsWithTheLastIntervalAndRangeAndRemembersWhatIsChosenOnIt()
    {
        var prefs = new ViewPreferences();
        prefs.Apply(new ChartLayout("15m", "1 day", 20));
        using var chart = new CandleChartViewModel("KX-A", _t.Api, _feed, _format, _t.Time, prefs);
        Assert.Equal(("15m", "1 day"), (chart.Interval.Text, chart.Range.Text));

        chart.Interval = chart.Intervals.Single(i => i.Text == "1h");
        Assert.Equal(("1h", chart.Range.Text), (prefs.ChartInterval, prefs.ChartRange));
        chart.Range = chart.Ranges.Single(r => r.Text == "3 days");

        Assert.Equal(("1h", "3 days"), (prefs.ChartInterval, prefs.ChartRange));
    }

    [Fact]
    public void AnIntervalOrRangeThatDoesNotExistGivesTheDefaultsOfTheInterval()
    {
        var prefs = new ViewPreferences();
        prefs.Apply(new ChartLayout("2h", "7 fortnights", null));
        using var unknown = new CandleChartViewModel("KX-A", _t.Api, _feed, _format, _t.Time, prefs);
        prefs.Apply(new ChartLayout("5m", "1 year", null));
        using var impossible = new CandleChartViewModel("KX-A", _t.Api, _feed, _format, _t.Time, prefs);

        Assert.Equal(("1h", "1 week"), (unknown.Interval.Text, unknown.Range.Text));
        Assert.Equal(("5m", "12 hours"), (impossible.Interval.Text, impossible.Range.Text));
    }

    [Fact]
    public void ABookStartsWithTheLastDepthAndRemembersWhatIsChosenOnIt()
    {
        var prefs = new ViewPreferences();
        prefs.SetBookDepth(20);
        var book = new OrderBookViewModel("KX-A", _t.Api, _feed, _format, prefs);
        Assert.Equal(20, book.Depth);

        book.Depth = 50;
        prefs.SetBookDepth(33);   // a depth the book has no choice for
        var odd = new OrderBookViewModel("KX-A", _t.Api, _feed, _format, prefs);

        Assert.Equal(50, book.Depth);
        Assert.Equal(10, odd.Depth);
        book.Dispose();
        odd.Dispose();
    }
}

public sealed class LayoutWiringTests : IDisposable
{
    private static readonly Uri Server = new("https://srv01:5280");

    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeToaster _toaster = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly StatusViewModel _status;
    private readonly MarketsViewModel _markets;
    private readonly AlertsViewModel _alerts;
    private readonly AlertNotifier _notifier;
    private readonly MainViewModel _model;
    private readonly FakePlacement _placement = new();

    public LayoutWiringTests()
    {
        _status = new StatusViewModel(_format, _t.Time);
        _markets = new MarketsViewModel(_format, _t.Time, InlineContext.Instance, _dialogs);
        _alerts = new AlertsViewModel(_format, _dialogs);
        _notifier = new AlertNotifier(_toaster, _format, _t.Settings, _t.Time, () => _alerts.Silent, InlineContext.Instance);
        _model = new MainViewModel(_t.Services, _t.Settings, _dialogs, _format, _status, _markets, _alerts, _notifier, _toaster, new TradingViewModel(new FakeTradingGateway(), _format, _dialogs, _t.Time, InlineContext.Instance)) { Placement = _placement };
        _t.Settings.SaveServer(Server);
        _t.Api.ScreenerRows.Add(Samples.Row("KX-A"));
    }

    public void Dispose()
    {
        _model.Layouts.Dispose();
        _notifier.Dispose();
        _alerts.Dispose();
        _status.Dispose();
        _markets.Dispose();
        _t.Dispose();
    }

    [Fact]
    public async Task ConnectingReadsTheLayoutBeforeThePagesLoadSoTheyLoadOnceAndLaidOut()
    {
        _t.Api.LayoutRows["default"] = LayoutSamples.State(page: "Alerts", status: "closed", sound: false, width: 1234).ToJson();

        await _model.StartAsync();
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 1);

        var query = _t.Api.ScreenQueries[0];
        Assert.Equal(("spread", "asc", "closed", "5"), (query.Sort, query.Order, query.Status, query.MinVolume));
        Assert.Same(_alerts, _model.CurrentPage);
        Assert.True(_alerts.Silent);
        Assert.Equal(1234, _placement.Applied!.Width);
        Assert.Equal(("15m", 20), (_markets.Preferences.ChartInterval, _markets.Preferences.BookDepth));
        Assert.Equal("default", _model.Layouts.Current);
    }

    [Fact]
    public async Task WithoutALayoutThePagesLoadAsTheyAlwaysDid()
    {
        await _model.StartAsync();
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 1);

        Assert.Equal(("volume", "desc", "active"), (_t.Api.ScreenQueries[0].Sort, _t.Api.ScreenQueries[0].Order, _t.Api.ScreenQueries[0].Status));
        Assert.Same(_status, _model.CurrentPage);
        Assert.Null(_placement.Applied);
    }

    [Fact]
    public async Task AToastThatStartedTheTerminalKeepsTheMarketsPageWhateverTheLayoutSays()
    {
        _t.Api.LayoutRows["default"] = LayoutSamples.State(page: "Alerts").ToJson();
        _toaster.Click("KX-A");

        await _model.StartAsync();
        await Wait.UntilAsync(() => _markets.Detail is not null);

        Assert.Same(_markets, _model.CurrentPage);
    }

    [Fact]
    public async Task WhatIsSavedIsWhatTheTerminalLooksLikeNow()
    {
        await _model.StartAsync();
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 1);
        _placement.Now = new WindowLayout(5, 6, 900, 700, true);
        _model.Selected = _model.Navigation[2];
        _alerts.Sound = false;
        _markets.Sort = _markets.Sorts[2];

        await _model.FlushLayoutAsync();

        var saved = LayoutState.FromJson(_t.Api.LayoutRows["default"])!;
        Assert.Equal(new WindowLayout(5, 6, 900, 700, true), saved.Window);
        Assert.Equal(("Alerts", false, "change"), (saved.Page, saved.Sound, saved.Markets!.Sort));
        Assert.Equal(LayoutState.Current, saved.Version);
    }

    [Fact]
    public async Task AChangeInTheWindowIsSavedAMomentLater()
    {
        await _model.StartAsync();
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 1);

        _model.NotifyLayoutChanged();
        await Wait.FireUntilAsync(_t.Time, () => _t.Api.LayoutRows.ContainsKey("default"));

        Assert.True(_model.Layouts.Saving);
    }

    [Fact]
    public async Task ClosingNeverWaitsLongerThanTheLimitForAServerThatDoesNotAnswer()
    {
        await _model.StartAsync();
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 1);
        _t.Api.LayoutPutHold = new TaskCompletionSource();
        _model.NotifyLayoutChanged();

        var flush = _model.FlushLayoutAsync();
        await Wait.FireUntilAsync(_t.Time, () => flush.IsCompleted);

        Assert.True(flush.IsCompletedSuccessfully);
        _t.Api.LayoutPutHold.SetResult();
    }

    [Fact]
    public async Task TheZoneBoxShowsTheOffsetInEffectNowNotTheStandardTimeOne()
    {
        await _model.StartAsync();

        Assert.StartsWith("This computer (", _model.Zones[0].Text, StringComparison.Ordinal);
        Assert.EndsWith(", now UTC+00:00)", _model.Zones[0].Text, StringComparison.Ordinal);
        Assert.Equal("UTC", _model.Zones[1].Text);
        Assert.All(_model.Zones.Skip(2), z => Assert.Matches(@", now UTC[+-]\d\d:\d\d$", z.Text));
        Assert.DoesNotContain(_model.Zones, z => z.Text.StartsWith("(UTC", StringComparison.Ordinal));
    }

    [Fact]
    public void AZoneNameLosesItsStandardOffsetAndGainsTheOneInEffect()
    {
        var mountain = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

        Assert.Equal("Mountain Time (US & Canada), now UTC-06:00", MainViewModel.ZoneText(mountain, new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Mountain Time (US & Canada), now UTC-07:00", MainViewModel.ZoneText(mountain, new DateTimeOffset(2026, 12, 18, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Coordinated Universal Time, now UTC+00:00", MainViewModel.ZoneText(TimeZoneInfo.Utc, Samples.Now));
        var custom = TimeZoneInfo.CreateCustomTimeZone("c", TimeSpan.FromHours(5.5), "My own zone", "My own zone");
        Assert.Equal("My own zone, now UTC+05:30", MainViewModel.ZoneText(custom, Samples.Now));
        Assert.Throws<ArgumentNullException>(() => MainViewModel.ZoneText(null!, Samples.Now));
    }
}

internal sealed class FakePlacement : IWindowPlacement
{
    public WindowLayout Now { get; set; } = new(1, 2, 800, 600, false);

    public WindowLayout? Applied { get; private set; }

    public WindowLayout Capture() => Now;

    public void Apply(WindowLayout layout) => Applied = layout;
}

public sealed class LayoutViewTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);

    public LayoutViewTests()
    {
        _t.Api.ScreenerRows.Add(Samples.Row("KX-A"));
    }

    public void Dispose() => _t.Dispose();

    private MarketsViewModel Page()
    {
        var model = new MarketsViewModel(_format, _t.Time, InlineContext.Instance, _dialogs);
        model.Attach(() => _t.Api);
        model.LoadAsync().GetAwaiter().GetResult();
        return model;
    }

    [Fact]
    public void ColumnsFromALayoutAreTakenByTheGridAndTheGridsOwnAreReported()
    {
        var (widths, order, reported) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            using var model = Page();
            var view = new MarketsView { DataContext = model };
            VisualTreeKit.Realize(view);
            var grid = VisualTreeKit.All<DataGrid>(view).Single();

            var headers = grid.Columns.Select(c => (string)c.Header).ToList();
            var everyColumn = headers.Select((h, i) => new ColumnLayout(h, h == "Last" ? 99 : h == "Ticker" ? 222 : 0, h == "Last" ? 0 : h == "Ticker" ? 1 : i + 2)).ToList();
            model.ApplyLayout(new MarketsLayout(null, null, null, null, null, [.. everyColumn, new ColumnLayout("Nothing", 5, 99)]));
            VisualTreeKit.Realize(view);
            var snapshot = (grid.Columns.Single(c => c.Header as string == "Last").Width.Value, grid.Columns.Single(c => c.Header as string == "Ticker").Width.Value);
            var first = grid.Columns.Single(c => c.DisplayIndex == 0).Header;

            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = grid });
            Assert.Empty(errors.Messages);
            return (snapshot, first, model.Columns.ToList());
        });

        Assert.Equal((99d, 222d), widths);
        Assert.Equal("Last", order);
        Assert.Equal(12, reported.Count);
        Assert.Equal(0, reported.Single(c => c.Key == "Market").Width);   // a column that stretches
        Assert.Equal(99, reported.Single(c => c.Key == "Last").Width);
    }

    [Fact]
    public void AMainWindowKnowsWhereItIsAndGoesWhereALayoutSays()
    {
        var (before, after, state) = Sta.Run(() =>
        {
            var window = Composition.Create(_t.Services, _t.Settings, new FakeToaster(), new FakeTradingGateway());
            window.Width = 900;
            window.Height = 700;
            window.Left = 30;
            window.Top = 40;
            var captured = window.Capture();

            window.Apply(new WindowLayout(60, 70, 1000, 650, false));
            var moved = window.Capture();
            window.Apply(new WindowLayout(60, 70, 1000, 650, true));
            return (captured, moved, window.WindowState);
        });

        Assert.Equal(new WindowLayout(30, 40, 900, 700, false), before);
        Assert.Equal(new WindowLayout(60, 70, 1000, 650, false), after);
        Assert.Equal(WindowState.Maximized, state);
    }

    [Fact]
    public void TheMainWindowShowsTheLayoutBoxAndItsButtons()
    {
        var (texts, buttons, boxes) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var window = Composition.Create(_t.Services, _t.Settings, new FakeToaster(), new FakeTradingGateway());
            VisualTreeKit.Realize(window);
            Assert.Empty(errors.Messages);
            return (VisualTreeKit.Texts(window).ToList(), VisualTreeKit.All<Button>(window).Select(b => b.Content as string).ToList(), VisualTreeKit.All<ComboBox>(window).Count());
        });

        Assert.Contains("Layout", texts);
        Assert.Contains("Save as…", buttons);
        Assert.Contains("Delete", buttons);
        Assert.Equal(2, boxes);
    }

    [Fact]
    public void ClosingTheWindowSavesTheLayoutFirstThenCloses()
    {
        var closed = Sta.Run(() =>
        {
            var window = Composition.Create(_t.Services, _t.Settings, new FakeToaster(), new FakeTradingGateway());
            var closedEvent = false;
            window.Closed += (_, _) => closedEvent = true;

            window.Close();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            return closedEvent;
        });

        Assert.True(closed);
    }
}
