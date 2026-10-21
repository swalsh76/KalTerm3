using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Views;
using ScottPlot.Plottables;
using ScottPlot.WPF;

namespace KalTerm.Terminal.Tests;

public sealed class MarketDetailTabsTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly MarketFeed _feed;

    public MarketDetailTabsTests()
    {
        _feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
        _feed.Start();
        _t.Api.CandleRows.AddRange(Enumerable.Range(0, 5).Select(h => Samples.Candle(h)));
    }

    public void Dispose()
    {
        _feed.Dispose();
        _t.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MarketDetailViewModel Make() => new("KX-A", _t.Api, _feed, _format, _t.Time);

    [Fact]
    public void ThePageStartsOnTheOverviewWithNeitherTabMadeYet()
    {
        using var detail = Make();

        Assert.Equal((0, null, null), (detail.SelectedTab, detail.Chart, detail.Book));
    }

    [Fact]
    public void ChoosingTheChartTabMakesAndLoadsTheChartOnceAndComingBackReusesIt()
    {
        using var detail = Make();

        detail.SelectedTab = 1;
        var chart = detail.Chart;
        detail.SelectedTab = 0;
        detail.SelectedTab = 1;

        Assert.NotNull(chart);
        Assert.Same(chart, detail.Chart);
        Assert.Single(_t.Api.CandleQueries);
        Assert.Null(detail.Book);
        Assert.Equal(5, chart.Series.Candles.Count);
    }

    [Fact]
    public void ChoosingTheBookTabMakesAndLoadsTheBookOnce()
    {
        using var detail = Make();

        detail.SelectedTab = 2;
        var book = detail.Book;
        detail.SelectedTab = 0;
        detail.SelectedTab = 2;

        Assert.NotNull(book);
        Assert.Same(book, detail.Book);
        Assert.Single(_t.Api.BookQueries);
        Assert.Null(detail.Chart);
        Assert.True(book.HasBook);
    }

    [Fact]
    public void ATabThatCannotLoadReportsItselfAndTheSetterNeverThrows()
    {
        _t.Api.CandlesFail = new ApiUnavailableException("down");
        _t.Api.BookFails = new ApiUnavailableException("down too");
        using var detail = Make();

        detail.SelectedTab = 1;
        detail.SelectedTab = 2;

        Assert.Equal(("down", "down too"), (detail.Chart!.Error, detail.Book!.Error));
    }

    [Fact]
    public async Task ClosingThePageClosesBothTabsAndTheirWatching()
    {
        var detail = Make();
        detail.SelectedTab = 1;
        detail.SelectedTab = 2;
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        await _t.Api.Stream.Writer.WriteAsync(new ReadyEvent(new StreamReadyDto("abc", "t0", [], [], [], [], 50)), Ct);
        await Wait.UntilAsync(() => _feed.State == FeedState.Live);

        detail.Dispose();

        await Wait.UntilAsync(() => _t.Api.StreamChanges.Contains("remove books KX-A"));
        await Task.Delay(30, Ct);
        _t.Time.Advance(TimeSpan.FromMinutes(1));
        var asked = _t.Api.CandleQueries.Count;
        for (var i = 0; i < _t.Time.PendingTimers; i++)
        {
            _t.Time.FireNext();
        }

        await Task.Delay(50, Ct);
        Assert.Equal(asked, _t.Api.CandleQueries.Count);
    }
}

public sealed class ChartViewTests : IDisposable
{
    private readonly TestServices _t = new();

    public ChartViewTests() => _t.Api.CandleRows.AddRange(Enumerable.Range(0, 6).Select(h => Samples.Candle(h)));

    public void Dispose() => _t.Dispose();

    private (CandleChartViewModel Chart, MarketFeed Feed) Loaded(Formatting format)
    {
        var feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
        var chart = new CandleChartViewModel("KX-A", _t.Api, feed, format, _t.Time);
        chart.LoadAsync().GetAwaiter().GetResult();
        return (chart, feed);
    }

    [Fact]
    public void TheChartViewShowsItsControlsDrawsTheCandlesAndHasNoBindingErrors()
    {
        var (texts, plottables) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var (chart, feed) = Loaded(new Formatting(TimeZoneInfo.Utc));
            using var disposeFeed = feed;
            using var disposeChart = chart;
            var view = new CandleChartView { DataContext = chart };
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            var plot = VisualTreeKit.All<WpfPlot>(view).Single().Plot;
            return (VisualTreeKit.Texts(view).ToList(), plot.GetPlottables().Select(p => p.GetType().Name).Order(StringComparer.Ordinal).ToList());
        });

        Assert.Contains("Interval", texts);
        Assert.Contains("Range", texts);
        Assert.Contains(texts, t => t.Contains("Last", StringComparison.Ordinal) && t.Contains("0.52", StringComparison.Ordinal));   // the last price, from the newest candle
        Assert.Contains("stored 1h", texts);
        Assert.Equal(["BarPlot", "CandlestickPlot", "HorizontalLine"], plottables);
        Assert.DoesNotContain("No trades in this range.", texts);
    }

    [Fact]
    public void TheRangeBoxFollowsTheIntervalAndKeepsAValidSelection()
    {
        var (before, after, selected, back) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var (chart, feed) = Loaded(new Formatting(TimeZoneInfo.Utc));
            using var disposeFeed = feed;
            using var disposeChart = chart;
            var view = new CandleChartView { DataContext = chart };
            VisualTreeKit.Realize(view);
            var range = VisualTreeKit.All<ComboBox>(view).Single(c => c.ItemsSource == chart.Ranges || c.Items.OfType<RangeChoice>().Any());
            var first = range.Items.OfType<RangeChoice>().Select(r => r.Text).ToList();

            chart.Interval = chart.Intervals.Single(i => i.Text == "5m");
            VisualTreeKit.Realize(view);
            var second = range.Items.OfType<RangeChoice>().Select(r => r.Text).ToList();
            var shown = (range.SelectedItem as RangeChoice)?.Text;

            chart.Interval = chart.Intervals.Single(i => i.Text == "1h");
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return (first, second, shown, (range.SelectedItem as RangeChoice)?.Text);
        });

        Assert.Equal(["12 hours", "1 day", "3 days", "1 week", "1 month", "All"], before);
        Assert.Equal(["1 hour", "3 hours", "6 hours", "12 hours", "1 day", "3 days", "All"], after);
        Assert.Equal("12 hours", selected);
        Assert.Equal("12 hours", back);   // 12 hours is offered at 1h too, so it stays
    }

    [Fact]
    public void ALegendUnderTheChartNamesEveryColorAndLine()
    {
        var texts = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var (chart, feed) = Loaded(new Formatting(TimeZoneInfo.Utc));
            using var disposeFeed = feed;
            using var disposeChart = chart;
            var view = new CandleChartView { DataContext = chart };
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return VisualTreeKit.Texts(view).ToList();
        });

        Assert.Contains("Closed at or above its open", texts);
        Assert.Contains("Closed below its open", texts);
        Assert.Contains("Data may be missing (a gap)", texts);
        Assert.Contains("Volume (right axis)", texts);
        Assert.Contains("Last price", texts);
    }

    [Fact]
    public void TheLegendSwatchesAreTheColorsTheChartIsDrawnWith()
    {
        var swatches = Sta.Run(() =>
        {
            var (chart, feed) = Loaded(new Formatting(TimeZoneInfo.Utc));
            using var disposeFeed = feed;
            using var disposeChart = chart;
            var view = new CandleChartView { DataContext = chart };
            VisualTreeKit.Realize(view);
            return VisualTreeKit.All<System.Windows.Shapes.Rectangle>(view)
                .Where(r => r.Width == 12)
                .Select(r => (((System.Windows.Media.SolidColorBrush)r.Fill).Color.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Opacity))
                .ToList();
        });

        Assert.Equal(
            [("#FF2E9E5B", 1.0), ("#FFD64545", 1.0), ("#FFD49A1B", ChartColors.GapOpacity), ("#FF7C93B8", ChartColors.VolumeOpacity)],
            swatches);
        Assert.Equal(ScottPlot.Color.FromHex(ChartColors.Rising), ScottPlot.Color.FromHex("#2E9E5B"));
    }

    [Fact]
    public void ChangingTheZoneRedrawsTheChartOnTheClockOfTheNewZone()
    {
        var (before, after) = Sta.Run(() =>
        {
            var format = new Formatting(TimeZoneInfo.Utc);
            var (chart, feed) = Loaded(format);
            using var disposeFeed = feed;
            using var disposeChart = chart;
            var view = new CandleChartView { DataContext = chart };
            VisualTreeKit.Realize(view);
            var plot = VisualTreeKit.All<WpfPlot>(view).Single().Plot;
            var first = plot.GetPlottables().OfType<CandlestickPlot>().Single().Data.GetOHLCs()[0].DateTime;

            format.Zone = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

            return (first, plot.GetPlottables().OfType<CandlestickPlot>().Single().Data.GetOHLCs()[0].DateTime);
        });

        Assert.Equal(new DateTime(2026, 10, 20, 0, 0, 0), before);
        Assert.Equal(new DateTime(2026, 10, 19, 18, 0, 0), after);   // midnight UTC is 18:00 the evening before in Denver
    }

    [Fact]
    public void ResetViewPutsTheChartBackToShowingEveryCandle()
    {
        var (moved, reset) = Sta.Run(() =>
        {
            var (chart, feed) = Loaded(new Formatting(TimeZoneInfo.Utc));
            using var disposeFeed = feed;
            using var disposeChart = chart;
            var view = new CandleChartView { DataContext = chart };
            VisualTreeKit.Realize(view);
            var plot = VisualTreeKit.All<WpfPlot>(view).Single().Plot;
            var start = plot.Axes.Bottom.Min;
            plot.Axes.SetLimits(0, 1, 0, 1);
            var moved = plot.Axes.Bottom.Min;

            VisualTreeKit.All<Button>(view).Single(b => b.Content as string == "Reset view").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            return (moved, plot.Axes.Bottom.Min == start);
        });

        Assert.Equal(0, moved);
        Assert.True(reset);
    }

    [Fact]
    public void TheMouseOverTheChartTellsTheViewModelAndLeavingClearsIt()
    {
        var text = Sta.Run(() =>
        {
            var (chart, feed) = Loaded(new Formatting(TimeZoneInfo.Utc));
            using var disposeFeed = feed;
            using var disposeChart = chart;
            var view = new CandleChartView { DataContext = chart };
            VisualTreeKit.Realize(view);
            var plot = VisualTreeKit.All<WpfPlot>(view).Single();
            chart.HoverAt(new DateTime(2026, 10, 20, 1, 30, 0));
            var before = chart.HoverText;

            plot.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseMoveEvent });
            plot.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });

            return (before, chart.HoverText);
        });

        Assert.Contains("open 0.50", text.before, StringComparison.Ordinal);
        Assert.Equal(string.Empty, text.Item2);   // leaving the chart clears the line
    }

    [Fact]
    public void AViewWithNoViewModelAndAChangedOneBothWork()
    {
        Sta.Run(() =>
        {
            var view = new CandleChartView();
            VisualTreeKit.Realize(view);
            var (first, feedOne) = Loaded(new Formatting(TimeZoneInfo.Utc));
            var (second, feedTwo) = Loaded(new Formatting(TimeZoneInfo.Utc));
            using var disposeFeeds = feedOne;
            using var disposeSecondFeed = feedTwo;
            using var disposeFirst = first;
            using var disposeSecond = second;

            view.DataContext = first;
            view.DataContext = second;
            view.DataContext = null;
            VisualTreeKit.Realize(view);
        });
    }

    [Fact]
    public void NoTradesAndAFailureAreEachSaidOnTheChart()
    {
        _t.Api.CandleRows.Clear();
        var empty = Sta.Run(() =>
        {
            var (chart, feed) = Loaded(new Formatting(TimeZoneInfo.Utc));
            using var disposeFeed = feed;
            using var disposeChart = chart;
            var view = new CandleChartView { DataContext = chart };
            VisualTreeKit.Realize(view);
            return VisualTreeKit.Texts(view).ToList();
        });
        _t.Api.CandlesFail = new ApiUnavailableException("could not reach kaldb:5280");
        var failed = Sta.Run(() =>
        {
            var (chart, feed) = Loaded(new Formatting(TimeZoneInfo.Utc));
            using var disposeFeed = feed;
            using var disposeChart = chart;
            var view = new CandleChartView { DataContext = chart };
            VisualTreeKit.Realize(view);
            return VisualTreeKit.Texts(view).ToList();
        });

        Assert.Contains("No trades in this range.", empty);
        Assert.Contains("could not reach kaldb:5280", failed);
        Assert.DoesNotContain("No trades in this range.", failed);
    }

    private static List<string> BookTexts(OrderBookViewModel book)
    {
        using var errors = BindingErrors.Listen();
        var view = new OrderBookView { DataContext = book };
        VisualTreeKit.Realize(view);
        Assert.Empty(errors.Messages);
        return [.. VisualTreeKit.Texts(view)];
    }

    [Fact]
    public void TheBookViewShowsTheLadderTheSpreadAndWarnsWhenTheBookCannotBeTrusted()
    {
        _t.Api.BookAnswer = Samples.Book("KX-A", stale: true);

        var texts = Sta.Run(() =>
        {
            var feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
            using var disposeFeed = feed;
            var book = new OrderBookViewModel("KX-A", _t.Api, feed, new Formatting(TimeZoneInfo.Utc));
            using var disposeBook = book;
            book.LoadAsync().GetAwaiter().GetResult();
            return BookTexts(book);
        });

        Assert.Contains("Yes price", texts);
        Assert.Contains("0.57", texts);
        Assert.Contains("0.58", texts);
        Assert.Contains("0.60", texts);
        Assert.Contains("0.55", texts);
        Assert.Contains("35.50", texts);
        Assert.Contains(texts, t => t.Contains("Bid", StringComparison.Ordinal) && t.Contains("0.55", StringComparison.Ordinal) && t.Contains("Ask", StringComparison.Ordinal) && t.Contains("0.57", StringComparison.Ordinal)
            && t.Contains("Spread", StringComparison.Ordinal) && t.Contains("0.02", StringComparison.Ordinal) && t.Contains("Middle", StringComparison.Ordinal) && t.Contains("0.56", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("This book is stale", StringComparison.Ordinal));
        Assert.Contains("2026-10-18 22:59:58 UTC", texts);
    }

    [Fact]
    public void AMarketWithoutABookSaysSoAndALadderIsHidden()
    {
        _t.Api.BookFails = new ApiException(System.Net.HttpStatusCode.NotFound, "not_found");

        var texts = Sta.Run(() =>
        {
            var feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
            using var disposeFeed = feed;
            var book = new OrderBookViewModel("KX-A", _t.Api, feed, new Formatting(TimeZoneInfo.Utc));
            using var disposeBook = book;
            book.LoadAsync().GetAwaiter().GetResult();
            return BookTexts(book);
        });

        Assert.Contains(texts, t => t.StartsWith("No order book is published for this market", StringComparison.Ordinal));
        Assert.DoesNotContain("Yes price", texts);
    }

    [Fact]
    public void TheMarketDetailHasThreeTabsAndTheChartAndTheBookAppearWhenChosen()
    {
        var (overview, chartTab, bookTab, plots) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var format = new Formatting(TimeZoneInfo.Utc);
            using var model = new MarketsViewModel(format, _t.Time, InlineContext.Instance);
            _t.Api.ScreenerRows.Add(Samples.Row("KX-A"));
            model.Attach(() => _t.Api);
            model.LoadAsync().GetAwaiter().GetResult();
            model.OpenAsync(model.Rows[0]).GetAwaiter().GetResult();
            var view = new MarketsView { DataContext = model };
            VisualTreeKit.Realize(view);
            var first = VisualTreeKit.Texts(view).ToList();

            model.Detail!.SelectedTab = 1;
            VisualTreeKit.Realize(view);
            var chart = VisualTreeKit.Texts(view).ToList();
            var plotCount = VisualTreeKit.All<WpfPlot>(view).Count();

            model.Detail.SelectedTab = 2;
            VisualTreeKit.Realize(view);
            var book = VisualTreeKit.Texts(view).ToList();
            Assert.Empty(errors.Messages);
            return (first, chart, book, plotCount);
        });

        Assert.Contains("Overview", overview);
        Assert.Contains("Chart", overview);
        Assert.Contains("Book", overview);
        Assert.Contains("Metrics", overview);
        Assert.Contains("Interval", chartTab);
        Assert.Equal(1, plots);
        Assert.Contains("Yes price", bookTab);
    }
}
