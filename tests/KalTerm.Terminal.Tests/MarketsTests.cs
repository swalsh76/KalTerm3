using System.Net;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Views;

namespace KalTerm.Terminal.Tests;

/// <summary>Raises events where they are posted, in order, so a test sees them as soon as the feed has them.</summary>
internal sealed class InlineContext : SynchronizationContext
{
    public static InlineContext Instance { get; } = new();

    public override void Post(SendOrPostCallback d, object? state) => d(state);
}

internal static class Wait
{
    /// <summary>Waits (up to ten seconds) until something that happens on another thread has happened.</summary>
    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the condition did not come true");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Fires waiting timers, oldest first, until the condition holds. The test clock only moves when told to, and several loops may be waiting on it.</summary>
    public static async Task FireUntilAsync(AutoAdvanceTimeProvider time, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the condition did not come true");
            }

            if (time.PendingTimers > 0)
            {
                time.FireNext();
            }

            await Task.Delay(5);
        }
    }
}

public class MarketFormattingTests
{
    [Theory]
    [InlineData("0.560000", "0.56")]
    [InlineData("1.000000", "1.00")]
    [InlineData("0.123400", "0.1234")]
    [InlineData("-0.010000", "-0.01")]
    [InlineData(null, "-")]
    [InlineData("n/a", "n/a")]
    public void DollarsLoseTrailingZerosButKeepTwoDecimals(string? value, string expected) => Assert.Equal(expected, Formatting.Dollars(value));

    [Theory]
    [InlineData("0.020000", "+0.02")]
    [InlineData("-0.010000", "-0.01")]
    [InlineData("0.000000", "0.00")]
    [InlineData(null, "-")]
    public void AChangeShowsItsSign(string? value, string expected) => Assert.Equal(expected, Formatting.Signed(value));

    [Theory]
    [InlineData("1234.00", "1,234")]
    [InlineData("20.50", "20.50")]
    [InlineData("0.00", "0")]
    [InlineData("1234567890123.00", "1,234,567,890,123")]
    [InlineData(null, "-")]
    [InlineData("n/a", "n/a")]
    public void CountsHaveThousandsSeparatorsAndAWholeNumberLosesItsDecimals(string? value, string expected) => Assert.Equal(expected, Formatting.Count(value));

    [Theory]
    [InlineData("0.570000", "0.550000", "0.020000")]
    [InlineData("0.550000", "0.570000", "-0.020000")]
    [InlineData("1.000000", "0.000001", "0.999999")]
    [InlineData("0.5", "0.25", "0.250000")]
    [InlineData("0.5", "0.5", "0.000000")]
    public void TheDifferenceOfTwoPricesIsExact(string high, string low, string expected) => Assert.Equal(expected, Formatting.Difference(high, low));

    [Theory]
    [InlineData(null, "0.5")]
    [InlineData("0.5", null)]
    [InlineData("abc", "0.5")]
    [InlineData("0.5", "0.1234567")]
    [InlineData("99999999999999.000000", "0.5")]
    public void ThereIsNoDifferenceWhenAPriceIsMissingOrNotAPrice(string? high, string? low) => Assert.Null(Formatting.Difference(high, low));

    [Fact]
    public void MillisecondsAreShownWhenAsked()
    {
        var format = new Formatting(TimeZoneInfo.Utc);

        Assert.Equal("2026-10-18 23:00:00.250", format.Moment("2026-10-18T23:00:00.250000+00:00", milliseconds: true));
        Assert.Equal("2026-10-18 23:00:00", format.Moment("2026-10-18T23:00:00.250000+00:00"));
    }
}

public class MarketRowTests
{
    private static List<string> Changed(MarketRowViewModel row)
    {
        var names = new List<string>();
        row.PropertyChanged += (_, e) => names.Add(e.PropertyName!);
        return names;
    }

    [Fact]
    public void ARowShowsTheScreenersFiguresInTheShortForm()
    {
        var row = new MarketRowViewModel(Samples.Row("KX-A"));

        Assert.Equal(("KX-A", "EV-KX-A", "active", "Yes"), (row.Ticker, row.EventTicker, row.Status, row.YesLabel));
        Assert.Equal(("0.56", "0.55", "0.57", "0.02"), (row.Last, row.Bid, row.Ask, row.Spread));
        Assert.Equal(("+0.02", "1,234", "99"), (row.Change, row.Volume, row.OpenInterest));
    }

    [Fact]
    public void AMissingFigureIsADash()
    {
        var row = new MarketRowViewModel(Samples.Row("KX-A", last: null, bid: null, ask: null, spread: null, change: null, openInterest: null));

        Assert.Equal(("-", "-", "-", "-", "-", "-"), (row.Last, row.Bid, row.Ask, row.Spread, row.Change, row.OpenInterest));
    }

    [Fact]
    public void ATickUpdatesThePricesTheSpreadAndTheOpenInterestAndTellsTheView()
    {
        var row = new MarketRowViewModel(Samples.Row("KX-A"));
        var changed = Changed(row);

        row.Apply(Samples.Tick(price: "0.600000", bid: "0.590000", ask: "0.640000", openInterest: "120.00"));

        Assert.Equal(("0.60", "0.59", "0.64", "0.05", "120"), (row.Last, row.Bid, row.Ask, row.Spread, row.OpenInterest));
        Assert.Equal(["Last", "Bid", "Ask", "Spread", "OpenInterest"], changed.Distinct());
    }

    [Fact]
    public void ATickKeepsTheVolumeAndTheChangeOfTheLastRunBecauseItsVolumeIsATotalNotATwentyFourHourFigure()
    {
        var row = new MarketRowViewModel(Samples.Row("KX-A"));

        row.Apply(Samples.Tick());

        Assert.Equal(("+0.02", "1,234"), (row.Change, row.Volume));
    }

    [Fact]
    public void AFieldTheTickDoesNotCarryKeepsWhatItHadAndTheSpreadFollowsTheOtherSide()
    {
        var row = new MarketRowViewModel(Samples.Row("KX-A"));

        row.Apply(new TickDto("2026-10-18T22:59:30.250000+00:00", null, null, "0.600000", null, null, null, null, null));

        Assert.Equal(("0.56", "0.55", "0.60", "0.05", "99"), (row.Last, row.Bid, row.Ask, row.Spread, row.OpenInterest));
    }

    [Fact]
    public void ATickForARowWithoutAQuoteGivesItOne()
    {
        var row = new MarketRowViewModel(Samples.Row("KX-A", bid: null, ask: null, spread: null));

        row.Apply(Samples.Tick(bid: "0.100000", ask: "0.900000"));

        Assert.Equal(("0.10", "0.90", "0.80"), (row.Bid, row.Ask, row.Spread));
    }

    [Fact]
    public void NothingIsAcceptedThatIsMissing()
    {
        Assert.Throws<ArgumentNullException>(() => new MarketRowViewModel(null!));
        Assert.Throws<ArgumentNullException>(() => new MarketRowViewModel(Samples.Row("KX-A")).Apply(null!));
    }
}

public sealed class MarketFeedTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly MarketFeed _feed;
    private readonly List<MarketTickDto> _ticks = [];
    private readonly List<(FeedState State, string Note)> _states = [];
    private int _resets;

    public MarketFeedTests()
    {
        _feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
        _feed.Tick += _ticks.Add;
        _feed.StateChanged += (state, note) => _states.Add((state, note));
        _feed.Reset += () => _resets++;
    }

    public void Dispose()
    {
        _feed.Dispose();
        _t.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReadyEvent Ready() => new(new StreamReadyDto("abc", "2026-10-18T22:59:58.000Z", [], [], [], [], 10));

    private static TickEvent Tick(string ticker, string id = "2026-10-18T22:59:30.250Z") => new(Samples.MarketTick(ticker), id);

    private async Task GoLiveAsync(params string[] wanted)
    {
        _feed.SetWanted("list", wanted);
        _feed.Start();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        await _t.Api.Stream.Writer.WriteAsync(Ready(), Ct);
        await Wait.UntilAsync(() => _feed.State == FeedState.Live);
    }

    [Fact]
    public async Task NothingIsOpenedUntilAMarketIsWanted()
    {
        _feed.Start();
        await Task.Delay(50, Ct);
        Assert.Empty(_t.Api.StreamOpens);
        Assert.Equal(FeedState.Stopped, _feed.State);

        _feed.SetWanted("list", ["KX-A"]);
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);

        Assert.Equal(["KX-A"], _t.Api.StreamOpens[0].Ticks);
        Assert.Empty(_t.Api.StreamOpens[0].Books);
        Assert.Equal(FeedState.Connecting, _states[0].State);
    }

    [Fact]
    public async Task TheStreamOpensForTheUnionOfWhatEveryPageWantsAndStartingTwiceOpensOnce()
    {
        _feed.SetWanted("list", ["KX-A", "KX-B"]);
        _feed.SetWanted("detail", ["KX-B", "KX-C"]);

        _feed.Start();
        _feed.Start();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        await Task.Delay(30, Ct);

        Assert.Single(_t.Api.StreamOpens);
        Assert.Equal(["KX-A", "KX-B", "KX-C"], _t.Api.StreamOpens[0].Ticks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheStreamBeingOpenMakesTheFeedLiveAndATickIsHandedOver()
    {
        await GoLiveAsync("KX-A");

        await _t.Api.Stream.Writer.WriteAsync(Tick("KX-A"), Ct);
        await Wait.UntilAsync(() => _ticks.Count == 1);

        Assert.Equal("KX-A", _ticks[0].Ticker);
        Assert.Equal(FeedState.Live, _feed.State);
    }

    [Fact]
    public async Task PagesChangingTheirMindsAreAppliedToTheOpenStreamWithoutReconnecting()
    {
        await GoLiveAsync("KX-A", "KX-B");

        _feed.SetWanted("list", ["KX-B", "KX-C"]);
        await Wait.UntilAsync(() => _t.Api.StreamChanges.Count == 2);

        Assert.Equal(["remove ticks KX-A", "add ticks KX-C"], _t.Api.StreamChanges);
        Assert.Single(_t.Api.StreamOpens);
    }

    [Fact]
    public async Task AMarketTwoPagesWantStaysUntilBothHaveDroppedIt()
    {
        await GoLiveAsync("KX-A");
        _feed.SetWanted("detail", ["KX-A", "KX-B"]);
        await Wait.UntilAsync(() => _t.Api.StreamChanges.Count == 1);

        _feed.SetWanted("list", []);
        await Task.Delay(50, Ct);
        Assert.Equal(["add ticks KX-B"], _t.Api.StreamChanges);   // KX-A is still wanted by the detail

        _feed.SetWanted("detail", ["KX-B"]);
        await Wait.UntilAsync(() => _t.Api.StreamChanges.Count == 2);

        Assert.Equal("remove ticks KX-A", _t.Api.StreamChanges[1]);
    }

    [Fact]
    public async Task TooManyMarketsIsSaidAndTheFeedKeepsRunning()
    {
        await GoLiveAsync("KX-A");
        _t.Api.StreamChangeFails = new ApiException(HttpStatusCode.Conflict, "too_many_markets");

        _feed.SetWanted("list", ["KX-A", "KX-B"]);
        await Wait.UntilAsync(() => _states.Any(s => s.Note.Contains("too many markets", StringComparison.Ordinal)));

        Assert.Equal(FeedState.Live, _feed.State);
    }

    [Fact]
    public async Task AnyOtherRefusalOfAChangeIsSaidToo()
    {
        await GoLiveAsync("KX-A");
        _t.Api.StreamChangeFails = new ApiException(HttpStatusCode.UnprocessableEntity, "invalid_parameter");

        _feed.SetWanted("list", ["KX-A", "KX-B"]);
        await Wait.UntilAsync(() => _states.Any(s => s.Note.Contains("invalid_parameter", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AResetIsPassedOn()
    {
        await GoLiveAsync("KX-A");

        await _t.Api.Stream.Writer.WriteAsync(new ResetEvent("too_far_behind"), Ct);
        await Wait.UntilAsync(() => _resets == 1);
    }

    [Fact]
    public async Task ADroppedStreamIsReportedAsReconnecting()
    {
        await GoLiveAsync("KX-A");

        _t.Api.Stream.Writer.Complete();
        await Wait.UntilAsync(() => _feed.State == FeedState.Reconnecting);

        Assert.Contains(_states, s => s.State == FeedState.Reconnecting && s.Note.Contains("reconnecting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedTokenStopsTheFeedAndSaysWhy()
    {
        _t.Api.StreamFails = Samples.Unauthorized();

        _feed.SetWanted("list", ["KX-A"]);
        _feed.Start();
        await Wait.UntilAsync(() => _feed.State == FeedState.Stopped && _states.Count > 1);

        Assert.Contains("401", _states[^1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingTheFeedClosesTheClientAndAnythingWantedAfterwardsOpensNothing()
    {
        await GoLiveAsync("KX-A");

        _feed.Dispose();
        _feed.SetWanted("list", ["KX-B"]);
        await Task.Delay(50, Ct);

        Assert.True(_t.Api.Disposed);
        Assert.Single(_t.Api.StreamOpens);
        _feed.Dispose();   // twice is the same as once
    }

    [Fact]
    public void WantingBeforeStartingOnlyRemembersAndMissingArgumentsAreRefused()
    {
        _feed.SetWanted("list", ["KX-A"]);
        Assert.Empty(_t.Api.StreamOpens);
        Assert.Throws<ArgumentNullException>(() => _feed.SetWanted(null!, ["KX-A"]));
        Assert.Throws<ArgumentNullException>(() => _feed.SetWanted("list", null!));
        Assert.Throws<ArgumentNullException>(() => new MarketFeed(null!, _t.Time));
        Assert.Throws<ArgumentNullException>(() => new MarketFeed(_t.Api, null!));
    }
}

public sealed class MarketsViewModelTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly MarketsViewModel _model;

    public MarketsViewModelTests()
    {
        _model = new MarketsViewModel(_format, _t.Time, InlineContext.Instance);
        _t.Api.ScreenerRows.AddRange([Samples.Row("KX-A"), Samples.Row("KX-B", last: "0.100000", bid: "0.090000", ask: "0.110000", spread: "0.020000", change: "-0.050000", volume: "10.50", openInterest: null)]);
    }

    public void Dispose()
    {
        _model.Dispose();
        _t.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task OpenAsync()
    {
        _model.Attach(() => _t.Api);
        await _model.LoadAsync();
    }

    private static ScreenerPage Page(string? next, params string[] tickers) => new("volume", "desc", "2026-10-18T22:58:00.000000+00:00", 120, [.. tickers.Select(t => Samples.Row(t))], next);

    [Fact]
    public async Task TheFirstPageShowsTheMarketsInTheOrderTheServerGaveAndAsksForTheDefaults()
    {
        await OpenAsync();

        Assert.Equal(["KX-A", "KX-B"], _model.Rows.Select(r => r.Ticker));
        Assert.Equal(new ScreenQuery("volume", "desc", "active", null, 50, null, null, null), _t.Api.ScreenQueries.Single());
        Assert.Equal(("Page 1", false, false, false), (_model.PageText, _model.CanPrevious, _model.CanNext, _model.Loading));
        Assert.Equal("-", _model.Rows[1].OpenInterest);
        Assert.Equal("-0.05", _model.Rows[1].Change);
    }

    [Fact]
    public async Task TheMarketsOnShowAreTheOnesTheFeedWatches()
    {
        await OpenAsync();

        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        Assert.Equal(["KX-A", "KX-B"], _t.Api.StreamOpens[0].Ticks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ALiveTickUpdatesItsRowAndATickForAMarketNotOnShowIsIgnored()
    {
        await OpenAsync();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);

        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(Samples.MarketTick("KX-B"), "t1"), Ct);
        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(Samples.MarketTick("KX-Z"), "t2"), Ct);
        await Wait.UntilAsync(() => _model.Rows[1].Last == "0.57");

        Assert.Equal("0.56", _model.Rows[0].Last);
        Assert.Equal(("0.57", "0.56", "0.58", "0.02"), (_model.Rows[1].Last, _model.Rows[1].Bid, _model.Rows[1].Ask, _model.Rows[1].Spread));
    }

    [Fact]
    public async Task TheFeedStateIsShownInWords()
    {
        await OpenAsync();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        Assert.Equal("connecting…", _model.FeedText);

        await _t.Api.Stream.Writer.WriteAsync(new ReadyEvent(new StreamReadyDto("abc", "t0", [], [], [], [], 10)), Ct);
        await Wait.UntilAsync(() => _model.FeedText == "live");

        _t.Api.Stream.Writer.Complete();
        await Wait.UntilAsync(() => _model.FeedText.StartsWith("reconnecting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AResetFromTheServerLoadsThePageAgain()
    {
        await OpenAsync();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);

        await _t.Api.Stream.Writer.WriteAsync(new ResetEvent("slow_consumer"), Ct);
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 2);
    }

    [Fact]
    public async Task TypingWaitsForAPauseAndOnlyTheLastTextIsAsked()
    {
        await OpenAsync();

        _model.SearchText = "wa";
        _model.SearchText = "was";
        await Task.Delay(50, Ct);
        Assert.Single(_t.Api.ScreenQueries);   // not asked yet
        await Wait.FireUntilAsync(_t.Time, () => _t.Api.ScreenQueries.Count == 2);

        Assert.Equal("was", _t.Api.ScreenQueries[1].Search);
        await Task.Delay(50, Ct);
        Assert.Equal(2, _t.Api.ScreenQueries.Count);
    }

    [Fact]
    public async Task OneCharacterAsksForNothingAndTextThatChangesNothingAsksNothing()
    {
        await OpenAsync();

        _model.SearchText = "w";
        _model.SearchText = "  ";
        await Task.Delay(50, Ct);
        _t.Time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < _t.Time.PendingTimers; i++)
        {
            _t.Time.FireNext();
        }

        await Task.Delay(50, Ct);
        Assert.Single(_t.Api.ScreenQueries);
        Assert.Null(_t.Api.ScreenQueries[0].Search);
    }

    [Fact]
    public async Task ChoosingAStatusOrARankingAsksAtOnceFromTheFirstPage()
    {
        await OpenAsync();

        _model.StatusFilter = _model.Statuses[0];
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 2);
        _model.Sort = _model.Sorts.Single(s => s.Key == "spread");
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 3);
        _model.ToggleOrderCommand.Execute(null);
        await Wait.UntilAsync(() => _t.Api.ScreenQueries.Count == 4);

        Assert.Null(_t.Api.ScreenQueries[1].Status);
        Assert.Equal(("spread", "desc"), (_t.Api.ScreenQueries[2].Sort, _t.Api.ScreenQueries[2].Order));
        Assert.Equal(("spread", "asc"), (_t.Api.ScreenQueries[3].Sort, _t.Api.ScreenQueries[3].Order));
        Assert.Equal("Smallest first", _model.OrderText);
    }

    [Theory]
    [InlineData("1000", "1000", false)]
    [InlineData(" 1000.5 ", "1000.5", false)]
    [InlineData("abc", null, true)]
    [InlineData("1,000", null, true)]
    [InlineData("12.345", null, true)]
    [InlineData("", null, false)]
    public async Task AMinimumVolumeIsSentOnlyWhenItIsANumberAndOtherwiseMarkedAndIgnored(string typed, string? sent, bool invalid)
    {
        await OpenAsync();

        _model.MinVolumeText = typed;
        Assert.Equal(invalid, _model.MinVolumeInvalid);
        if (typed.Trim().Length > 0 && sent is not null)
        {
            await Wait.FireUntilAsync(_t.Time, () => _t.Api.ScreenQueries.Count == 2);
            Assert.Equal(sent, _t.Api.ScreenQueries[1].MinVolume);
        }
        else
        {
            await Task.Delay(30, Ct);
            Assert.Single(_t.Api.ScreenQueries);
        }
    }

    [Fact]
    public async Task NextAndPreviousWalkThePagesByTheCursorsTheServerGave()
    {
        _t.Api.ScreenerAnswer = query => query.Cursor switch
        {
            null => Page("C1", "KX-1"),
            "C1" => Page("C2", "KX-2"),
            _ => Page(null, "KX-3"),
        };
        await OpenAsync();
        Assert.True(_model.CanNext);
        Assert.False(_model.CanPrevious);

        await _model.NextCommand.RunAsync();
        await _model.NextCommand.RunAsync();
        Assert.Equal(("Page 3", "KX-3", false, true), (_model.PageText, _model.Rows[0].Ticker, _model.CanNext, _model.CanPrevious));
        await _model.PreviousCommand.RunAsync();
        await _model.PreviousCommand.RunAsync();

        Assert.Equal([null, "C1", "C2", "C1", null], _t.Api.ScreenQueries.Select(q => q.Cursor));
        Assert.Equal(("Page 1", "KX-1"), (_model.PageText, _model.Rows[0].Ticker));
    }

    [Fact]
    public async Task AFailedPageChangeLeavesThePageWhereItWasAndSaysWhy()
    {
        _t.Api.ScreenerAnswer = query => query.Cursor is null ? Page("C1", "KX-1") : Page(null, "KX-2");
        await OpenAsync();

        _t.Api.ScreenerFails = new ApiUnavailableException("could not reach kaldb:5280");
        await _model.NextCommand.RunAsync();

        Assert.Equal(("Page 1", "KX-1", true, true), (_model.PageText, _model.Rows[0].Ticker, _model.CanNext, _model.Stale));
        Assert.Equal("could not reach kaldb:5280", _model.Error);
        _t.Api.ScreenerFails = null;
        await _model.NextCommand.RunAsync();
        Assert.Equal(("Page 2", string.Empty, false), (_model.PageText, _model.Error, _model.Stale));
    }

    [Fact]
    public async Task AFailedRefreshKeepsTheRowsGreyedAndARefusedTokenIsSaidInPlainWords()
    {
        await OpenAsync();

        _t.Api.ScreenerFails = Samples.Unauthorized();
        await _model.RefreshCommand.RunAsync();

        Assert.Equal(("the server did not accept the token", true, 2), (_model.Error, _model.Stale, _model.Rows.Count));
        _t.Api.ScreenerFails = null;
        await _model.RefreshCommand.RunAsync();
        Assert.Equal((string.Empty, false), (_model.Error, _model.Stale));
    }

    [Fact]
    public async Task APageThatCouldNotBeLoadedAtAllHasNothingToGreyOut()
    {
        _t.Api.ScreenerFails = new ApiUnavailableException("down");

        await OpenAsync();

        Assert.Equal(("down", false, true, false), (_model.Error, _model.Stale, _model.HasRun, _model.NothingFound));
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterANewerAskIsThrownAway()
    {
        _model.Attach(() => _t.Api);
        var hold = new TaskCompletionSource<ScreenerPage>();
        _t.Api.ScreenerHold = hold;
        var old = _model.LoadAsync();
        _t.Api.ScreenerHold = null;
        _t.Api.ScreenerRows.Clear();
        _t.Api.ScreenerRows.Add(Samples.Row("KX-NEW"));

        await _model.LoadAsync();
        hold.SetResult(Page(null, "KX-OLD"));
        await old;

        Assert.Equal(["KX-NEW"], _model.Rows.Select(r => r.Ticker));
        Assert.False(_model.Loading);
    }

    [Fact]
    public async Task NoMarketsMatchIsSaidOnlyAfterAnAskThatWorked()
    {
        _t.Api.ScreenerRows.Clear();
        Assert.False(_model.NothingFound);

        await OpenAsync();

        Assert.True(_model.NothingFound);
        Assert.Empty(_model.Rows);
    }

    [Fact]
    public async Task TheAgeOfTheFiguresIsShownAndWarnsWhenTheyGetOld()
    {
        await OpenAsync();
        Assert.Equal(("24-hour figures 2m ago", false), (_model.AgeText, _model.FiguresOld));

        _t.Time.Advance(TimeSpan.FromMinutes(5));
        await Wait.FireUntilAsync(_t.Time, () => _model.FiguresOld);

        Assert.Equal("24-hour figures 7m ago", _model.AgeText);
    }

    [Fact]
    public async Task WithoutAFinishedAnalyzerRunItSaysSo()
    {
        _t.Api.ScreenerAnswer = _ => new ScreenerPage("volume", "desc", null, null, [], null);

        await OpenAsync();

        Assert.Equal(("the metrics analyzer has not finished a run yet", false), (_model.AgeText, _model.FiguresOld));
    }

    [Fact]
    public async Task OpeningAMarketShowsItsDetailAndWatchesItAndGoingBackClosesIt()
    {
        await OpenAsync();
        _model.Selected = _model.Rows[0];

        await _model.OpenCommand.RunAsync();
        var detail = _model.Detail;

        Assert.NotNull(detail);
        Assert.True(_model.ShowingDetail);
        Assert.False(_model.ShowingList);
        Assert.Equal("KX-A", detail.Ticker);
        detail.BackCommand.Execute(null);
        Assert.Null(_model.Detail);
        Assert.False(_model.ShowingDetail);
        Assert.True(_model.ShowingList);
    }

    [Fact]
    public async Task OpeningNothingOrOpeningWithoutAServerDoesNothing()
    {
        await _model.OpenAsync(null);
        Assert.Null(_model.Detail);

        await OpenAsync();
        Assert.False(_model.OpenCommand.CanExecute(null));
        _model.Selected = _model.Rows[0];
        Assert.True(_model.OpenCommand.CanExecute(null));

        _model.Detach();
        await _model.OpenAsync(new MarketRowViewModel(Samples.Row("KX-A")));
        Assert.Null(_model.Detail);
    }

    [Fact]
    public async Task OpeningAnotherMarketReplacesTheOneOpen()
    {
        await OpenAsync();

        await _model.OpenAsync(_model.Rows[0]);
        var first = _model.Detail;
        await _model.OpenAsync(_model.Rows[1]);

        Assert.NotSame(first, _model.Detail);
        Assert.Equal("KX-B", _model.Detail!.Ticker);
    }

    [Fact]
    public async Task LettingGoOfTheServerEmptiesThePageAndClosesTheClients()
    {
        await OpenAsync();
        await _model.OpenAsync(_model.Rows[0]);

        _model.Detach();

        Assert.Empty(_model.Rows);
        Assert.Null(_model.Detail);
        Assert.True(_t.Api.Disposed);
        Assert.Equal(("not live", string.Empty, false), (_model.FeedText, _model.AgeText, _model.HasRun));
        await _model.RefreshAsync();   // nothing to ask: no server
        Assert.Single(_t.Api.ScreenQueries);
    }

    [Fact]
    public async Task AttachingAgainStartsOverOnTheNewServer()
    {
        await OpenAsync();
        await _model.NextCommand.RunAsync();   // no next page: nothing happens

        _t.Api.ScreenerRows.Clear();
        _t.Api.ScreenerRows.Add(Samples.Row("KX-OTHER"));
        await OpenAsync();

        Assert.Equal(["KX-OTHER"], _model.Rows.Select(r => r.Ticker));
        Assert.Equal("Page 1", _model.PageText);
    }

    [Fact]
    public async Task ChangingAFilterWithNoServerAsksNothing()
    {
        _model.StatusFilter = _model.Statuses[0];
        _model.Sort = _model.Sorts[1];
        _model.SearchText = "wash";
        _model.Descending = false;
        await Task.Delay(30, Ct);

        Assert.Empty(_t.Api.ScreenQueries);
        await _model.PreviousCommand.RunAsync();
        await _model.RefreshAsync();
        Assert.Empty(_t.Api.ScreenQueries);
    }

    [Fact]
    public void AMissingArgumentIsRefused() => Assert.Throws<ArgumentNullException>(() => _model.Attach(null!));
}

public sealed class MarketDetailViewModelTests : IDisposable
{
    private readonly TestServices _t = new();
    private readonly Formatting _format = new(TimeZoneInfo.Utc);
    private readonly MarketFeed _feed;
    private MarketDetailViewModel? _detail;

    public MarketDetailViewModelTests()
    {
        _feed = new MarketFeed(_t.Api, _t.Time, InlineContext.Instance);
        _feed.Start();
    }

    public void Dispose()
    {
        _detail?.Dispose();
        _feed.Dispose();
        _t.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MarketDetailViewModel Make() => _detail = new MarketDetailViewModel("KX-A", _t.Api, _feed, _format, _t.Time);

    private async Task<MarketDetailViewModel> LoadedAsync()
    {
        var detail = Make();
        await detail.LoadAsync();
        return detail;
    }

    [Fact]
    public async Task TheDetailShowsTheMarketItsMetricsItsTicksAndItsTradesNewestFirst()
    {
        _t.Api.TickHistory.AddRange([Samples.Tick("2026-10-18T22:59:01.000000+00:00", price: "0.500000"), Samples.Tick("2026-10-18T22:59:02.000000+00:00", price: "0.510000")]);
        _t.Api.TradeHistory.AddRange([Samples.Trade("t1", "2026-10-18T22:58:01.000000+00:00"), Samples.Trade("t2", "2026-10-18T22:58:02.000000+00:00", side: "no", block: true)]);

        var detail = await LoadedAsync();

        Assert.Equal(("KX-A  ·  Washington", false, false), (detail.Heading, detail.Loading, detail.HasError));
        Assert.Equal(["0.51", "0.50"], detail.Ticks.Select(t => t.Price));
        Assert.Equal("2026-10-18 22:59:02.000", detail.Ticks[0].Time);
        Assert.Equal(["t2", "t1"], detail.Trades.Select(t => t.Source.TradeId));
        Assert.Equal(("no", "block", "0.56", "3"), (detail.Trades[0].Side, detail.Trades[0].Kind, detail.Trades[0].YesPrice, detail.Trades[0].Count));
        Assert.Equal(200, _t.Api.TickQueries.Single().Limit);
    }

    [Fact]
    public async Task TheLinesDescribeTheMarketAndTheMetricsInTheChosenZone()
    {
        var detail = await LoadedAsync();

        Assert.Equal("Event", detail.About[0].Label);
        Assert.Contains(detail.About, l => l is { Label: "Opens", Text: "2026-10-18 14:00:00 UTC" });
        Assert.Contains(detail.About, l => l is { Label: "Yes / No", Text: "Washington / San Francisco" });
        Assert.DoesNotContain(detail.About, l => l.Label == "Result");
        Assert.Contains(detail.Metrics, l => l is { Label: "Last", Text: "0.56" });
        Assert.Contains(detail.Metrics, l => l is { Label: "24-hour change", Text: "+0.02" });
        Assert.Contains(detail.Metrics, l => l is { Label: "24-hour volume", Text: "1,234" });
        Assert.Contains(detail.Metrics, l => l is { Label: "SMA 20 / 50 (hourly)", Text: "0.54 / 0.53" });

        _format.Zone = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

        Assert.Contains(detail.About, l => l is { Label: "Opens", Text: "2026-10-18 08:00:00 -06:00" });
    }

    [Fact]
    public async Task ASettledMarketShowsItsResult()
    {
        _t.Api.Market = Samples.Market("KX-A") with { Result = "yes", SettlementValue = "1.000000", SettlementTime = "2026-10-19T05:00:00.000000+00:00" };

        var detail = await LoadedAsync();

        Assert.Contains(detail.About, l => l is { Label: "Result", Text: "yes" });
        Assert.Contains(detail.About, l => l is { Label: "Settled", Text: "1.00 at 2026-10-19 05:00:00 UTC" });
    }

    [Fact]
    public async Task APartialMetricsRunIsSaid()
    {
        _t.Api.Metrics = Samples.Metrics("KX-A") with { Partial = true };

        var detail = await LoadedAsync();

        Assert.Contains("a gap may have left a figure incomplete", detail.Metrics.Single(l => l.Label == "As of").Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningWatchesTheMarketAndClosingStopsWatchingIt()
    {
        _feed.SetWanted("list", ["KX-B"]);
        _feed.Start();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        await _t.Api.Stream.Writer.WriteAsync(new ReadyEvent(new StreamReadyDto("abc", "t0", [], [], [], [], 10)), Ct);
        await Wait.UntilAsync(() => _feed.State == FeedState.Live);

        var detail = await LoadedAsync();
        await Wait.UntilAsync(() => _t.Api.StreamChanges.Count == 1);
        detail.Dispose();
        await Wait.UntilAsync(() => _t.Api.StreamChanges.Count == 2);

        Assert.Equal(["add ticks KX-A", "remove ticks KX-A"], _t.Api.StreamChanges);
    }

    [Fact]
    public async Task ALiveTickIsAddedAtTheTopOnceAndOnlyForThisMarket()
    {
        _t.Api.TickHistory.Add(Samples.Tick("2026-10-18T22:59:01.000000+00:00"));
        var detail = await LoadedAsync();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);
        var fresh = Samples.Tick("2026-10-18T22:59:30.250000+00:00", price: "0.700000");

        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(new MarketTickDto("KX-A", fresh), "t1"), Ct);
        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(new MarketTickDto("KX-A", fresh), "t1"), Ct);   // the same tick again
        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(new MarketTickDto("KX-B", Samples.Tick("2026-10-18T22:59:31.000000+00:00")), "t2"), Ct);
        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(new MarketTickDto("KX-A", Samples.Tick("2026-10-18T22:59:32.000000+00:00", price: "0.710000")), "t3"), Ct);
        await Wait.UntilAsync(() => detail.Ticks.Count == 3);
        await Task.Delay(30, Ct);

        Assert.Equal(["0.71", "0.70", "0.57"], detail.Ticks.Select(t => t.Price));
    }

    [Fact]
    public async Task AtickThatTheHistoryAlreadyHoldsIsNotShownTwice()
    {
        var tick = Samples.Tick("2026-10-18T22:59:30.250000+00:00");
        _t.Api.TickHistory.Add(tick);
        var detail = await LoadedAsync();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);

        await _t.Api.Stream.Writer.WriteAsync(new TickEvent(new MarketTickDto("KX-A", tick), "t1"), Ct);
        await Task.Delay(50, Ct);

        Assert.Single(detail.Ticks);
    }

    [Fact]
    public async Task TheTicksKeptAreCappedAndTheOldestDropOff()
    {
        var detail = await LoadedAsync();
        await Wait.UntilAsync(() => _t.Api.StreamOpens.Count == 1);

        for (var i = 0; i < MarketDetailViewModel.MaxTicks + 10; i++)
        {
            var time = new DateTime(2026, 10, 18, 22, 0, 0, DateTimeKind.Utc).AddSeconds(i).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", System.Globalization.CultureInfo.InvariantCulture);
            await _t.Api.Stream.Writer.WriteAsync(new TickEvent(new MarketTickDto("KX-A", Samples.Tick(time)), time), Ct);
        }

        await Wait.UntilAsync(() => detail.Ticks.Count > 0 && detail.Ticks[0].Time.EndsWith(":08:29.000", StringComparison.Ordinal));
        Assert.Equal(MarketDetailViewModel.MaxTicks, detail.Ticks.Count);
    }

    [Fact]
    public async Task TheMetricsAndTheTradesAreAskedForAgainOnTheirTimers()
    {
        _t.Api.TradeHistory.Add(Samples.Trade("t1"));
        var detail = await LoadedAsync();
        Assert.Equal((1, 1), (_t.Api.MetricsCalls, _t.Api.TradeCalls));
        _t.Api.TradeHistory.Add(Samples.Trade("t2", "2026-10-18T22:59:10.000000+00:00"));
        _t.Api.Metrics = Samples.Metrics("KX-A") with { Last = "0.900000" };

        await Wait.FireUntilAsync(_t.Time, () => _t.Api.MetricsCalls >= 2 && _t.Api.TradeCalls >= 2);

        Assert.Equal(["t2", "t1"], detail.Trades.Select(t => t.Source.TradeId));
        Assert.Contains(detail.Metrics, l => l is { Label: "Last", Text: "0.90" });
    }

    [Fact]
    public async Task APartThatCannotBeLoadedIsSaidAndTheRestIsStillShown()
    {
        _t.Api.TicksFail = new ApiUnavailableException("no route");
        _t.Api.TradeHistory.Add(Samples.Trade("t1"));

        var detail = await LoadedAsync();

        Assert.True(detail.HasError);
        Assert.Contains("ticks: no route", detail.Error, StringComparison.Ordinal);
        Assert.Single(detail.Trades);
        Assert.NotEmpty(detail.Metrics);
    }

    [Fact]
    public async Task AMarketTheServerDoesNotKnowIsSaidInPlainWordsAndAFailureClearsWhenTheTimerSucceeds()
    {
        _t.Api.MarketFails = new ApiException(HttpStatusCode.NotFound, "not_found");
        _t.Api.TradesFail = new ApiException(HttpStatusCode.UnprocessableEntity, "invalid_parameter");
        var detail = await LoadedAsync();
        Assert.Contains("market: no such market", detail.Error, StringComparison.Ordinal);
        Assert.Contains("trades:", detail.Error, StringComparison.Ordinal);

        _t.Api.TradesFail = null;
        await Wait.FireUntilAsync(_t.Time, () => !detail.Error.Contains("trades:", StringComparison.Ordinal));

        Assert.Contains("market: no such market", detail.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingTheZoneRedrawsTheTimesOfTheTicksAndTheTrades()
    {
        _t.Api.TickHistory.Add(Samples.Tick("2026-10-18T22:59:01.500000+00:00"));
        _t.Api.TradeHistory.Add(Samples.Trade("t1", "2026-10-18T22:58:01.000000+00:00"));
        var detail = await LoadedAsync();

        _format.Zone = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

        Assert.Equal("2026-10-18 16:59:01.500", detail.Ticks[0].Time);
        Assert.Equal("2026-10-18 16:58:01.000", detail.Trades[0].Time);
    }

    [Fact]
    public async Task GoingBackAsksTheListToCloseItAndClosingTwiceIsHarmless()
    {
        var detail = await LoadedAsync();
        var closed = 0;
        detail.Closed += () => closed++;

        detail.BackCommand.Execute(null);
        detail.Dispose();
        detail.Dispose();

        Assert.Equal(1, closed);
    }

    [Fact]
    public void MissingArgumentsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new MarketDetailViewModel(null!, _t.Api, _feed, _format, _t.Time));
        Assert.Throws<ArgumentNullException>(() => new MarketDetailViewModel("KX-A", null!, _feed, _format, _t.Time));
        Assert.Throws<ArgumentNullException>(() => new MarketDetailViewModel("KX-A", _t.Api, null!, _format, _t.Time));
        Assert.Throws<ArgumentNullException>(() => new MarketDetailViewModel("KX-A", _t.Api, _feed, null!, _t.Time));
        Assert.Throws<ArgumentNullException>(() => new MarketDetailViewModel("KX-A", _t.Api, _feed, _format, null!));
    }
}

public sealed class MarketsViewTests : IDisposable
{
    private readonly TestServices _t = new();

    public MarketsViewTests() => _t.Api.ScreenerRows.AddRange([Samples.Row("KX-A"), Samples.Row("KX-B", label: "Seattle")]);

    public void Dispose() => _t.Dispose();

    private MarketsViewModel Loaded(Formatting format)
    {
        var model = new MarketsViewModel(format, _t.Time, InlineContext.Instance);
        model.Attach(() => _t.Api);
        model.LoadAsync().GetAwaiter().GetResult();
        return model;
    }

    [Fact]
    public void TheMarketsViewShowsTheRowsTheControlsAndTheState()
    {
        var texts = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            using var model = Loaded(new Formatting(TimeZoneInfo.Utc));
            var view = new MarketsView { DataContext = model };
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return VisualTreeKit.Texts(view).ToList();
        });

        Assert.Contains("KX-A", texts);
        Assert.Contains("Seattle", texts);
        Assert.Contains("0.56", texts);
        Assert.Contains("+0.02", texts);
        Assert.Contains("1,234", texts);
        Assert.Contains("Page 1", texts);
        Assert.Contains("24-hour figures 2m ago", texts);
        Assert.Contains("Largest first", texts);
        Assert.Contains("Next", texts);
        Assert.DoesNotContain("No markets match.", texts);
    }

    [Fact]
    public void AnEmptyAnswerSaysNoMarketsMatch()
    {
        _t.Api.ScreenerRows.Clear();

        var texts = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            using var model = Loaded(new Formatting(TimeZoneInfo.Utc));
            var view = new MarketsView { DataContext = model };
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return VisualTreeKit.Texts(view).ToList();
        });

        Assert.Contains("No markets match.", texts);
    }

    [Fact]
    public void AFailureIsShownOnThePage()
    {
        _t.Api.ScreenerFails = new ApiUnavailableException("could not reach kaldb:5280");

        var texts = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            using var model = Loaded(new Formatting(TimeZoneInfo.Utc));
            var view = new MarketsView { DataContext = model };
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return VisualTreeKit.Texts(view).ToList();
        });

        Assert.Contains("could not reach kaldb:5280", texts);
    }

    [Fact]
    public void AnOpenedMarketIsShownOverTheListWithItsLinesTicksAndTrades()
    {
        _t.Api.TickHistory.Add(Samples.Tick("2026-10-18T22:59:01.000000+00:00", price: "0.512000"));
        _t.Api.TradeHistory.Add(Samples.Trade("t1"));

        var texts = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            using var model = Loaded(new Formatting(TimeZoneInfo.Utc));
            model.OpenAsync(model.Rows[0]).GetAwaiter().GetResult();
            var view = new MarketsView { DataContext = model };
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return VisualTreeKit.Texts(view).ToList();
        });

        Assert.Contains("KX-A  ·  Washington", texts);
        Assert.Contains("Market", texts);
        Assert.Contains("Metrics", texts);
        Assert.Contains("Washington / San Francisco", texts);
        Assert.Contains("0.512", texts);
        Assert.Contains("2026-10-18 22:59:01.000", texts);
        Assert.Contains("0.56", texts);
        Assert.Contains("yes", texts);
    }

    [Fact]
    public void AnOpenedMarketCoversTheListWithAnOpaqueBackgroundAndTheListCannotBeUsedUnderIt()
    {
        var (covered, listUsable, listUsableAgain) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            using var model = Loaded(new Formatting(TimeZoneInfo.Utc));
            var view = new MarketsView { DataContext = model };
            VisualTreeKit.Realize(view);
            var grids = VisualTreeKit.All<System.Windows.Controls.DataGrid>(view).ToList();
            var list = grids[0];
            var before = list.IsEnabled || System.Windows.Media.VisualTreeHelper.GetParent(list) is System.Windows.Controls.Panel { IsEnabled: true };

            model.OpenAsync(model.Rows[0]).GetAwaiter().GetResult();
            VisualTreeKit.Realize(view);
            var border = VisualTreeKit.All<System.Windows.Controls.Border>(view).First(b => b.Child is System.Windows.Controls.ContentControl);
            var opaque = border.Background is System.Windows.Media.SolidColorBrush { Color.A: 255 };
            var usableWhileOpen = VisualTreeKit.All<System.Windows.Controls.TextBox>(view).Take(1).All(t => t.IsEnabled);

            model.Detail!.BackCommand.Execute(null);
            VisualTreeKit.Realize(view);
            var usableAgain = VisualTreeKit.All<System.Windows.Controls.TextBox>(view).Take(1).All(t => t.IsEnabled);
            Assert.Empty(errors.Messages);
            return (opaque && before, usableWhileOpen, usableAgain);
        });

        Assert.True(covered);
        Assert.False(listUsable);
        Assert.True(listUsableAgain);
    }

    [Fact]
    public void TheMainWindowShowsTheMarketsPageWhenChosen()
    {
        var texts = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var window = Composition.Create(_t.Services, _t.Settings);
            var model = (KalTerm.Terminal.Shell.MainViewModel)window.DataContext;
            model.Selected = model.Navigation[1];
            VisualTreeKit.Realize(window);
            Assert.Empty(errors.Messages);
            return VisualTreeKit.Texts(window).ToList();
        });

        Assert.Contains("Search", texts);
        Assert.Contains("Rank by", texts);
    }
}
