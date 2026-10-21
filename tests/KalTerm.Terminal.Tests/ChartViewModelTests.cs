using System.Net;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Tests;

public sealed class CandleChartViewModelTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly MarketFeed _feed;
    private CandleChartViewModel? _chart;

    public CandleChartViewModelTests()
    {
        _feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
        _feed.Start();
        _t.Api.CandleRows.AddRange(Enumerable.Range(0, 5).Select(h => Samples.Candle(h)));
    }

    public void Dispose()
    {
        _chart?.Dispose();
        _feed.Dispose();
        _t.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CandleChartViewModel Make() => _chart = new CandleChartViewModel("KX-A", _t.Api, _feed, _format, _t.Time);

    private async Task<CandleChartViewModel> LoadedAsync()
    {
        var chart = Make();
        await chart.LoadAsync();
        return chart;
    }

    [Fact]
    public async Task ItLoadsTheLatestHourlyCandlesOfAWeekAndSaysWhereTheyCameFrom()
    {
        var chart = await LoadedAsync();

        var query = Assert.Single(_t.Api.CandleQueries);
        Assert.Equal(("KX-A", "1h", 168), (query.Ticker, query.Interval, query.Query.Limit));
        Assert.Null(query.Query.Start);   // no start: the latest ones, so what is shown ends now
        Assert.Equal((5, 3600, "stored 1h", false, false), (chart.Series.Candles.Count, chart.Series.IntervalSeconds, chart.Source, chart.Loading, chart.HasError));
        Assert.True(chart.HasCandles);
        Assert.False(chart.NothingToShow);
    }

    [Theory]
    [InlineData("1m", "12 hours", 720)]
    [InlineData("5m", "1 day", 288)]
    [InlineData("15m", "1 week", 672)]
    [InlineData("1h", "1 day", 24)]
    [InlineData("1h", "1 month", 720)]
    [InlineData("4h", "1 week", 42)]
    [InlineData("1d", "1 month", 30)]
    [InlineData("5m", "All", 1_000)]
    public async Task TheIntervalAndTheRangeSetHowManyCandlesAreAsked(string interval, string range, int limit)
    {
        var chart = await LoadedAsync();

        chart.Interval = chart.Intervals.Single(i => i.Text == interval);
        chart.Range = chart.Ranges.Single(r => r.Text == range);

        var last = _t.Api.CandleQueries[^1];
        Assert.Equal((interval, limit), (last.Interval, last.Query.Limit));
        Assert.Equal(chart.Interval.Seconds, chart.Series.IntervalSeconds);
    }

    [Theory]
    [InlineData("1m", new[] { "1 hour", "3 hours", "6 hours", "12 hours", "All" })]
    [InlineData("5m", new[] { "1 hour", "3 hours", "6 hours", "12 hours", "1 day", "3 days", "All" })]
    [InlineData("15m", new[] { "3 hours", "6 hours", "12 hours", "1 day", "3 days", "1 week", "All" })]
    [InlineData("1h", new[] { "12 hours", "1 day", "3 days", "1 week", "1 month", "All" })]
    [InlineData("4h", new[] { "3 days", "1 week", "1 month", "3 months", "All" })]
    [InlineData("1d", new[] { "1 month", "3 months", "1 year", "All" })]
    public async Task EachIntervalOffersOnlyTheRangesThatGiveASensibleNumberOfCandles(string interval, string[] offered)
    {
        var chart = await LoadedAsync();

        chart.Interval = chart.Intervals.Single(i => i.Text == interval);

        Assert.Equal(offered, chart.Ranges.Select(r => r.Text));
        Assert.Equal(offered, CandleChartViewModel.RangesFor(chart.Interval).Select(r => r.Text));
        Assert.All(chart.Ranges.Where(r => r.Span is not null), r => Assert.InRange(r.Span!.Value.TotalSeconds / chart.Interval.Seconds, CandleChartViewModel.MinCandles, CandleChartViewModel.MaxCandles));
        Assert.Contains(chart.Range, chart.Ranges);   // what is selected is always one of what is offered
    }

    [Theory]
    [InlineData("1h", "1m", "3 hours")]   // a week (where it starts) is not offered at 1m: the comfortable one is, at most 200 candles
    [InlineData("1h", "5m", "12 hours")]
    [InlineData("1h", "1d", "3 months")]
    [InlineData("1h", "15m", "1 week")]   // still offered: kept
    [InlineData("1m", "15m", "3 hours")]   // 3 hours (where it is at 1m) is still offered at 15m: kept
    [InlineData("1m", "1h", "1 week")]   // 3 hours is not offered at 1h: the comfortable one
    [InlineData("1m", "4h", "1 month")]
    public async Task ChangingTheIntervalKeepsTheRangeWhenItIsStillOfferedAndOtherwiseTakesTheComfortableOne(string first, string second, string expected)
    {
        var chart = await LoadedAsync();
        chart.Interval = chart.Intervals.Single(i => i.Text == first);

        chart.Interval = chart.Intervals.Single(i => i.Text == second);

        Assert.Equal(expected, chart.Range.Text);
    }

    [Fact]
    public async Task ChangingTheIntervalKeepsTheRangeChosenWhenItIsStillOfferedAndOtherwiseTakesTheNewIntervalsOwn()
    {
        var chart = await LoadedAsync();
        Assert.Equal("1 week", chart.Range.Text);
        var asked = _t.Api.CandleQueries.Count;

        chart.Interval = chart.Intervals.Single(i => i.Text == "15m");   // a week of 15-minute candles is 672: fine
        Assert.Equal(("1 week", 672), (chart.Range.Text, _t.Api.CandleQueries[^1].Query.Limit));
        chart.Interval = chart.Intervals.Single(i => i.Text == "1m");   // a week of minutes is 10,080: not offered
        Assert.Equal(("3 hours", 180), (chart.Range.Text, _t.Api.CandleQueries[^1].Query.Limit));
        chart.Interval = chart.Intervals.Single(i => i.Text == "1d");   // and 3 hours is not offered at 1d
        Assert.Equal("3 months", chart.Range.Text);

        Assert.Equal(asked + 3, _t.Api.CandleQueries.Count);   // each change asked once, not once for the interval and again for the range
    }

    [Fact]
    public async Task ChangingTheIntervalAnnouncesNewRangesAndANullChoiceFromTheBoxIsIgnored()
    {
        var chart = await LoadedAsync();
        var raised = new List<string>();
        chart.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
        var asked = _t.Api.CandleQueries.Count;

        chart.Interval = chart.Intervals.Single(i => i.Text == "5m");
        chart.Range = null!;
        chart.Interval = null!;

        Assert.Contains("Ranges", raised);
        Assert.Contains("Range", raised);
        Assert.NotNull(chart.Range);
        Assert.Equal("5m", chart.Interval.Text);
        Assert.Equal(asked + 1, _t.Api.CandleQueries.Count);
        Assert.Throws<ArgumentNullException>(() => CandleChartViewModel.RangesFor(null!));
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterANewerChoiceIsThrownAway()
    {
        var chart = Make();
        var hold = new TaskCompletionSource<CandlePage>();
        _t.Api.CandleHold = hold;
        var first = chart.LoadAsync();
        _t.Api.CandleHold = null;

        chart.Interval = chart.Intervals.Single(i => i.Text == "15m");
        hold.SetResult(new CandlePage("1h", "stale answer", [Samples.Candle(0)], null));
        await first;

        Assert.Equal((900, "stored 15m", 5), (chart.Series.IntervalSeconds, chart.Source, chart.Series.Candles.Count));
        Assert.False(chart.Loading);
    }

    [Fact]
    public async Task EveryFiveSecondsTheNewestCandlesAreFoldedIn()
    {
        var chart = await LoadedAsync();
        var changes = 0;
        chart.SeriesChanged += () => changes++;
        _t.Api.CandleRows[4] = Samples.Candle(4, close: "0.600000", high: "0.620000");   // the candle that was forming has moved
        _t.Api.CandleRows.Add(Samples.Candle(5));   // and a new one has opened

        await Wait.FireUntilAsync(_t.Time, () => chart.Series.Candles.Count == 6);

        Assert.Equal(3, _t.Api.CandleQueries[^1].Query.Limit);
        Assert.Equal("0.600000", chart.Series.Candles[4].Source.Close);
        Assert.Equal(new DateTime(2026, 10, 20, 5, 0, 0, DateTimeKind.Utc), chart.Series.Candles[5].Start);
        Assert.True(changes >= 1);
    }

    [Fact]
    public async Task TheChartNeverHoldsMoreThanAThousandCandlesAndTheOldestDropOff()
    {
        _t.Api.CandleRows.Clear();
        _t.Api.CandleRows.AddRange(Enumerable.Range(0, 1_000).Select(h => Samples.Candle(h)));
        var chart = Make();
        chart.Range = chart.Ranges.Single(r => r.Text == "All");
        await chart.LoadAsync();
        Assert.Equal(1_000, chart.Series.Candles.Count);

        _t.Api.CandleRows.Add(Samples.Candle(1_000));
        await Wait.FireUntilAsync(_t.Time, () => chart.Series.Candles[^1].Start == new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc).AddHours(1_000));

        Assert.Equal(1_000, chart.Series.Candles.Count);
        Assert.Equal(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc).AddHours(1), chart.Series.Candles[0].Start);
    }

    [Fact]
    public async Task ARefreshThatFindsNothingNewLeavesTheCandlesAlone()
    {
        var chart = await LoadedAsync();
        var before = chart.Series;

        await Wait.FireUntilAsync(_t.Time, () => _t.Api.CandleQueries.Count >= 2);

        Assert.Equal(before.Candles.Count, chart.Series.Candles.Count);
    }

    [Fact]
    public async Task TheLastPriceIsTheNewestTradeTheCandlesKnowUntilTheStreamSaysOtherwiseAndRepeatsAreQuiet()
    {
        var chart = await LoadedAsync();
        Assert.Equal(("0.520000", "0.52"), (chart.LastPrice, chart.LastPriceText));
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        var changes = 0;
        chart.SeriesChanged += () => changes++;

        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(Samples.MarketTick("KX-A", Samples.Tick(price: "0.570000")), "t1"), Ct);
        await Wait.UntilAsync(() => chart.LastPrice == "0.570000");
        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(Samples.MarketTick("KX-A", Samples.Tick("2026-10-18T22:59:31.000000+00:00", price: "0.570000")), "t2"), Ct);   // the same price again
        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(Samples.MarketTick("KX-B", Samples.Tick(price: "0.100000")), "t3"), Ct);   // another market
        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(Samples.MarketTick("KX-A", Samples.Tick("2026-10-18T22:59:32.000000+00:00", price: null)), "t4"), Ct);   // a tick with no price
        await Task.Delay(50, Ct);

        Assert.Equal(("0.570000", "0.57", 1), (chart.LastPrice, chart.LastPriceText, changes));
    }

    [Fact]
    public async Task WithNoPriceYetThereIsADash()
    {
        _t.Api.CandleRows.Clear();

        var chart = await LoadedAsync();

        Assert.Equal((null, "-", true), (chart.LastPrice, chart.LastPriceText, chart.NothingToShow));
        Assert.False(chart.HasCandles);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not_found", "no such market")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid_parameter", "invalid_parameter")]
    public async Task ARefusedAskIsSaidAndALaterGoodOneClearsIt(HttpStatusCode status, string code, string said)
    {
        _t.Api.CandlesFail = new ApiException(status, code);
        var chart = await LoadedAsync();
        Assert.Contains(said, chart.Error, StringComparison.Ordinal);
        Assert.False(chart.NothingToShow);   // there is something to say, so it is not "no trades"

        _t.Api.CandlesFail = null;
        chart.Interval = chart.Intervals.Single(i => i.Text == "4h");

        Assert.Equal((string.Empty, 5), (chart.Error, chart.Series.Candles.Count));
    }

    [Fact]
    public async Task AServerThatCannotBeReachedIsSaidAndTheRefreshKeepsTryingWithoutLosingTheCandles()
    {
        var chart = await LoadedAsync();

        _t.Api.CandlesFail = new ApiUnavailableException("could not reach srv01:5280");
        await Wait.FireUntilAsync(_t.Time, () => chart.HasError);
        var said = chart.Error;
        var held = chart.Series.Candles.Count;
        _t.Api.CandlesFail = null;
        await Wait.FireUntilAsync(_t.Time, () => !chart.HasError);

        Assert.Equal(("could not reach srv01:5280", 5, 5), (said, held, chart.Series.Candles.Count));
    }

    [Fact]
    public async Task NoTradesInTheRangeIsSaidNotShownAsAnError()
    {
        _t.Api.CandleRows.Clear();

        var chart = await LoadedAsync();

        Assert.Equal((true, false), (chart.NothingToShow, chart.HasError));
    }

    [Fact]
    public async Task HoveringTellsTheCandleUnderThePointerInExactFigures()
    {
        _t.Api.CandleRows.Clear();
        _t.Api.CandleRows.AddRange([Samples.Candle(12), Samples.Candle(13, partial: true, gapKind: "ingest_down", gapSeconds: 600), Samples.Candle(14, open: null, high: null, low: null, close: null, volume: "0.00", trades: 0)]);
        var chart = await LoadedAsync();

        chart.HoverAt(new DateTime(2026, 10, 20, 12, 30, 0));
        var plain = chart.HoverText;
        chart.HoverAt(new DateTime(2026, 10, 20, 13, 5, 0));
        var gap = chart.HoverText;
        chart.HoverAt(new DateTime(2026, 10, 20, 14, 5, 0));
        var empty = chart.HoverText;
        chart.HoverAt(new DateTime(2026, 10, 20, 20, 0, 0));
        var outside = chart.HoverText;
        chart.HoverAt(null);

        Assert.Contains("2026-10-20 12:00:00 UTC", plain, StringComparison.Ordinal);
        Assert.Contains("open 0.50  high 0.55  low 0.45  close 0.52", plain, StringComparison.Ordinal);
        Assert.Contains("volume 100", plain, StringComparison.Ordinal);
        Assert.Contains("trades 7", plain, StringComparison.Ordinal);
        Assert.Contains("bid 0.51  ask 0.53", plain, StringComparison.Ordinal);
        Assert.Contains("open interest 99", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("missing", plain, StringComparison.Ordinal);
        Assert.Contains("data may be missing (ingest down, 600 s)", gap, StringComparison.Ordinal);
        Assert.Contains("no trades", empty, StringComparison.Ordinal);
        Assert.Equal(string.Empty, outside);
        Assert.Equal(string.Empty, chart.HoverText);
    }

    [Fact]
    public async Task ACandleWithAGapAndNoKindIsStillSaidToBeIncomplete()
    {
        _t.Api.CandleRows.Clear();
        _t.Api.CandleRows.Add(Samples.Candle(12, partial: true));
        var chart = await LoadedAsync();

        chart.HoverAt(new DateTime(2026, 10, 20, 12, 1, 0));

        Assert.EndsWith("data may be missing", chart.HoverText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoveringReadsTheTimeAsTheClockOfTheChosenZone()
    {
        _t.Api.CandleRows.Clear();
        _t.Api.CandleRows.Add(Samples.Candle(12));
        var chart = await LoadedAsync();
        _format.Zone = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

        chart.HoverAt(new DateTime(2026, 10, 20, 6, 30, 0));   // 06:30 in Denver is 12:30 UTC: inside the 12:00 candle

        Assert.Contains("2026-10-20 06:00:00 -06:00", chart.HoverText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingTheZoneAsksForADrawingAgain()
    {
        var chart = await LoadedAsync();
        var changes = 0;
        chart.SeriesChanged += () => changes++;

        _format.Zone = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

        Assert.Equal(1, changes);
        Assert.Same(_format.Zone, chart.Zone);
        Assert.NotEqual(TimeZoneInfo.Utc, chart.Zone);
    }

    [Fact]
    public async Task ClosingItStopsEverythingAndTwiceIsTheSameAsOnce()
    {
        var chart = await LoadedAsync();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);

        chart.Dispose();
        chart.Dispose();
        var asked = _t.Api.CandleQueries.Count;
        _t.Time.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < _t.Time.PendingTimers; i++)
        {
            _t.Time.FireNext();
        }

        await Task.Delay(50, Ct);
        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(Samples.MarketTick("KX-A", Samples.Tick(price: "0.900000")), "t1"), Ct);
        await Task.Delay(50, Ct);

        Assert.Equal(asked, _t.Api.CandleQueries.Count);
        Assert.NotEqual("0.900000", chart.LastPrice);
    }

    [Fact]
    public void MissingArgumentsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new CandleChartViewModel(null!, _t.Api, _feed, _format, _t.Time));
        Assert.Throws<ArgumentNullException>(() => new CandleChartViewModel("KX-A", null!, _feed, _format, _t.Time));
        Assert.Throws<ArgumentNullException>(() => new CandleChartViewModel("KX-A", _t.Api, null!, _format, _t.Time));
        Assert.Throws<ArgumentNullException>(() => new CandleChartViewModel("KX-A", _t.Api, _feed, null!, _t.Time));
        Assert.Throws<ArgumentNullException>(() => new CandleChartViewModel("KX-A", _t.Api, _feed, _format, null!));
    }
}

public sealed class OrderBookViewModelTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly MarketFeed _feed;
    private OrderBookViewModel? _book;

    public OrderBookViewModelTests()
    {
        _feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
        _feed.Start();
    }

    public void Dispose()
    {
        _book?.Dispose();
        _feed.Dispose();
        _t.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<OrderBookViewModel> LoadedAsync()
    {
        _book = new OrderBookViewModel("KX-A", _t.Api, _feed, _format);
        await _book.LoadAsync();
        return _book;
    }

    [Fact]
    public async Task TheLadderHasTheAsksDerivedFromTheNoBidsAboveTheBidsWithTheirTotals()
    {
        var book = await LoadedAsync();

        Assert.Equal([("0.60", "1", "20"), ("0.58", "12", "19"), ("0.57", "7", "7")], book.Asks.Select(r => (r.Price, r.Size, r.Total)));   // farthest first; the best ask is last
        Assert.Equal([("0.55", "10", "10"), ("0.54", "20.50", "30.50"), ("0.53", "5", "35.50")], book.Bids.Select(r => (r.Price, r.Size, r.Total)));   // the best first
        Assert.All(book.Asks, r => Assert.Equal(BookSide.Ask, r.Side));
        Assert.All(book.Bids, r => Assert.Equal(BookSide.Bid, r.Side));
        Assert.Equal(("0.55", "0.57", "0.02", "0.56"), (book.BestBid, book.BestAsk, book.Spread, book.Middle));
        Assert.Equal(("KX-A", 50), (_t.Api.BookQueries.Single().Ticker, _t.Api.BookQueries.Single().Depth));
    }

    [Fact]
    public async Task TheBarsAreTheTotalsAsAPartOfTheBiggerSide()
    {
        var book = await LoadedAsync();

        Assert.Equal(1.0, book.Bids[^1].Fraction, 6);   // 35.50 of 35.50
        Assert.Equal(7.0 / 35.5, book.Asks[^1].Fraction, 6);   // the best ask: 7.00 of 35.50
        Assert.Equal(20.0 / 35.5, book.Asks[0].Fraction, 6);
    }

    [Fact]
    public async Task OnlyAsManyLevelsAsAreChosenAreShown()
    {
        var levels = Enumerable.Range(0, 12).Select(i => ("yes", (0.50 - (i * 0.01)).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture), "1.00"))
            .Concat(Enumerable.Range(0, 12).Select(i => ("no", (0.40 - (i * 0.01)).ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture), "2.00"))).ToArray();
        _t.Api.BookAnswer = Samples.Book("KX-A", levels: levels);
        var book = await LoadedAsync();
        Assert.Equal((10, 10), (book.Bids.Count, book.Asks.Count));

        book.Depth = 20;

        Assert.Equal((12, 12), (book.Bids.Count, book.Asks.Count));
        Assert.Equal([10, 20, 50], OrderBookViewModel.Depths);
    }

    [Fact]
    public async Task AStaleBookAndAnApproximateOneAreEachWarnedAboutAndStaleComesFirst()
    {
        _t.Api.BookAnswer = Samples.Book("KX-A", stale: true, approximate: true);
        var book = await LoadedAsync();

        Assert.True(book.HasWarning);
        Assert.StartsWith("This book is stale", book.Warning, StringComparison.Ordinal);

        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        await _t.Api.Stream.Writer.WriteAsync(new BookEvent(Samples.Book("KX-A", approximate: true)), Ct);
        await Wait.UntilAsync(() => !book.Stale);
        Assert.StartsWith("This book was rebuilt from a snapshot", book.Warning, StringComparison.Ordinal);

        await _t.Api.Stream.Writer.WriteAsync(new BookEvent(Samples.Book("KX-A")), Ct);
        await Wait.UntilAsync(() => !book.HasWarning);
        Assert.Equal(string.Empty, book.Warning);
    }

    [Fact]
    public async Task AMarketWithNoPublishedBookSaysSoNotThatItIsBroken()
    {
        _t.Api.BookFails = new ApiException(HttpStatusCode.NotFound, "not_found");

        var book = await LoadedAsync();

        Assert.Equal((true, false, false, false), (book.NoBook, book.HasBook, book.HasError, book.Loading));
        Assert.Empty(book.Bids);
    }

    [Fact]
    public async Task AFailureToReadTheBookIsSaid()
    {
        _t.Api.BookFails = new ApiUnavailableException("down");

        var book = await LoadedAsync();

        Assert.Equal(("down", false, false), (book.Error, book.NoBook, book.HasBook));
    }

    [Fact]
    public async Task EachBookTheStreamSendsReplacesTheOneShownAndAnotherMarketsIsIgnored()
    {
        var book = await LoadedAsync();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        Assert.Equal(["KX-A"], _t.Api.StreamOpens[0].Books);
        Assert.Equal(MarketFeed.BookDepth, _t.Api.StreamOpens[0].Depth);

        await _t.Api.Stream.Writer.WriteAsync(new BookEvent(Samples.Book("KX-B", levels: [("yes", "0.100000", "1.00")])), Ct);
        await _t.Api.Stream.Writer.WriteAsync(new BookEvent(Samples.Book("KX-A", levels: [("yes", "0.600000", "3.00"), ("no", "0.350000", "4.00")])), Ct);
        await Wait.UntilAsync(() => book.BestBid == "0.60");

        Assert.Equal(("0.60", "0.65", "0.05", "0.625"), (book.BestBid, book.BestAsk, book.Spread, book.Middle));
        Assert.Single(book.Bids);
        Assert.Single(book.Asks);
    }

    [Fact]
    public async Task ABookThatArrivesAfterNoBookWasSaidIsShown()
    {
        _t.Api.BookFails = new ApiException(HttpStatusCode.NotFound, "not_found");
        var book = await LoadedAsync();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);

        await _t.Api.Stream.Writer.WriteAsync(new BookEvent(Samples.Book("KX-A")), Ct);
        await Wait.UntilAsync(() => book.HasBook);

        Assert.False(book.NoBook);
    }

    [Fact]
    public async Task AnEmptySideHasDashesAndNoSpread()
    {
        _t.Api.BookAnswer = Samples.Book("KX-A", levels: [("yes", "0.500000", "2.00")]);

        var book = await LoadedAsync();

        Assert.Equal(("0.50", "-", "-", "-"), (book.BestBid, book.BestAsk, book.Spread, book.Middle));
        Assert.Empty(book.Asks);
    }

    [Fact]
    public async Task AnEmptyBookHasNothingButDashes()
    {
        _t.Api.BookAnswer = Samples.Book("KX-A", levels: [("yes", "0.500000", "2.00")]) with { YesBids = [], NoBids = [] };

        var book = await LoadedAsync();

        Assert.Equal(("-", "-"), (book.BestBid, book.BestAsk));
        Assert.Empty(book.Bids);
    }

    [Fact]
    public async Task TheTimeOfTheBookFollowsTheZone()
    {
        var book = await LoadedAsync();
        Assert.Equal("2026-10-18 22:59:58 UTC", book.AsOf);

        _format.Zone = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

        Assert.Equal("2026-10-18 16:59:58 -06:00", book.AsOf);
    }

    [Fact]
    public async Task ClosingItStopsWatchingTheBook()
    {
        var book = await LoadedAsync();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        await _t.Api.Stream.Writer.WriteAsync(new ReadyEvent(new StreamReadyDto("abc", "t0", [], ["KX-A"], [], [], 50)), Ct);
        await Wait.UntilAsync(() => _feed.State == FeedState.Live);

        book.Dispose();
        book.Dispose();
        await Wait.UntilAsync(() => _t.Api.StreamChanges.Contains("remove books KX-A"));
        await _t.Api.Stream.Writer.WriteAsync(new BookEvent(Samples.Book("KX-A", levels: [("yes", "0.900000", "1.00")])), Ct);
        await Task.Delay(50, Ct);

        Assert.Equal("0.55", book.BestBid);
    }

    [Fact]
    public void MissingArgumentsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new OrderBookViewModel(null!, _t.Api, _feed, _format));
        Assert.Throws<ArgumentNullException>(() => new OrderBookViewModel("KX-A", null!, _feed, _format));
        Assert.Throws<ArgumentNullException>(() => new OrderBookViewModel("KX-A", _t.Api, null!, _format));
        Assert.Throws<ArgumentNullException>(() => new OrderBookViewModel("KX-A", _t.Api, _feed, null!));
    }
}

public sealed class MarketFeedBookTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly MarketFeed _feed;
    private readonly List<OrderBookDto> _books = [];

    public MarketFeedBookTests()
    {
        _feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
        _feed.Book += _books.Add;
    }

    public void Dispose()
    {
        _feed.Dispose();
        _t.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReadyEvent Ready() => new(new StreamReadyDto("abc", "2026-10-18T22:59:58.000Z", [], [], [], [], 50));

    [Fact]
    public async Task OnlyBooksBeingWantedOpensAStreamForBooksAtTheFullDepth()
    {
        _feed.SetWantedBooks("book", ["KX-A"]);
        _feed.Start();

        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);

        Assert.Empty(_t.Api.StreamOpens[0].Ticks);
        Assert.Equal(["KX-A"], _t.Api.StreamOpens[0].Books);
        Assert.Equal(50, _t.Api.StreamOpens[0].Depth);
    }

    [Fact]
    public async Task BooksAreHandedOverAndTicksAndBooksAreWantedIndependentlyOfEachOther()
    {
        _feed.SetWanted("list", ["KX-A", "KX-B"]);
        _feed.SetWantedBooks("book", ["KX-A"]);
        _feed.Start();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        await _t.Api.Stream.Writer.WriteAsync(Ready(), Ct);
        await Wait.UntilAsync(() => _feed.State == FeedState.Live);

        await _t.Api.Stream.Writer.WriteAsync(new BookEvent(Samples.Book("KX-A")), Ct);
        await Wait.UntilAsync(() => _books.Count == 1);
        _feed.SetWanted("list", ["KX-B"]);   // the ticks of KX-A are no longer wanted; its book still is
        await Wait.UntilAsync(() => _t.Api.StreamChanges.Count == 1);

        Assert.Equal(["KX-A", "KX-B"], _t.Api.StreamOpens[0].Ticks.Order(StringComparer.Ordinal));
        Assert.Equal(["remove ticks KX-A"], _t.Api.StreamChanges);
        Assert.Equal("KX-A", _books[0].Ticker);
    }

    [Fact]
    public async Task BooksAreAddedAndDroppedLiveToo()
    {
        _feed.SetWanted("list", ["KX-A"]);
        _feed.Start();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        await _t.Api.Stream.Writer.WriteAsync(Ready(), Ct);
        await Wait.UntilAsync(() => _feed.State == FeedState.Live);

        _feed.SetWantedBooks("book", ["KX-A"]);
        await Wait.UntilAsync(() => _t.Api.StreamChanges.Count == 1);
        _feed.SetWantedBooks("book", []);
        await Wait.UntilAsync(() => _t.Api.StreamChanges.Count == 2);

        Assert.Equal(["add books KX-A", "remove books KX-A"], _t.Api.StreamChanges);
    }
}
