using KalTerm.Core.Money;
using KalTerm.Kalshi.Books;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Models.Json;

namespace KalTerm.Kalshi.Tests.Books;

/// <summary>What the golden vectors cannot say: the derived asks, copies, the REST path, overflow, and the edges of the tracker's API.</summary>
public class LocalOrderBookTests
{
    private static PriceLevel Level(string price, string quantity) => new(Dollars.Parse(price), Contracts.Parse(quantity));

    private static OrderbookDeltaMsg Delta(string side, string price, string change) => new("A", Dollars.Parse(price), Contracts.Parse(change), side);

    private static WsMessage Envelope(string json) => KalshiJson.Parse(json, KalshiJsonContext.Default.WsMessage, nameof(WsMessage));

    [Fact]
    public void AsksAreDerivedFromTheOppositeSidesBids()
    {
        var book = new LocalOrderBook("A", [Level("0.40", "5"), Level("0.42", "1")], [Level("0.55", "2"), Level("0.50", "3")]);

        Assert.Equal(Dollars.Parse("0.42"), book.BestYesBid);
        Assert.Equal(Dollars.Parse("0.55"), book.BestNoBid);
        Assert.Equal(Dollars.Parse("0.45"), book.BestYesAsk);   // 1 - best no bid
        Assert.Equal(Dollars.Parse("0.58"), book.BestNoAsk);    // 1 - best yes bid
        Assert.Equal((2, 2), (book.YesLevels, book.NoLevels));
    }

    [Fact]
    public void AnEmptySideHasNoBestPriceAndNoDerivedAsk()
    {
        var book = new LocalOrderBook("A");

        Assert.Null(book.BestYesBid);
        Assert.Null(book.BestNoBid);
        Assert.Null(book.BestYesAsk);
        Assert.Null(book.BestNoAsk);
        Assert.Empty(book.Bids("yes"));
    }

    [Fact]
    public void BidsComeBackBestFirstAndZeroQuantityLevelsAreDropped()
    {
        var book = new LocalOrderBook("A", [Level("0.10", "1"), Level("0.30", "2"), Level("0.20", "0"), Level("0.20", "4")]);

        Assert.Equal(["0.300000", "0.200000", "0.100000"], book.Bids("yes").Select(l => l.Price.ToString()));
        Assert.Equal(["2.00", "4.00", "1.00"], book.Bids("yes").Select(l => l.Quantity.ToString()));
    }

    [Fact]
    public void ACopyIsIndependentOfTheOriginal()
    {
        var book = new LocalOrderBook("A", [Level("0.40", "5")]);
        var copy = book.Copy();

        Assert.True(book.ApplyDelta(Delta("yes", "0.41", "1")));

        Assert.Equal(1, copy.YesLevels);
        Assert.Equal(2, book.YesLevels);
        Assert.Equal("A", copy.Ticker);
    }

    [Fact]
    public void ADeltaChangesRemovesAndRefusesToGoNegative()
    {
        var book = new LocalOrderBook("A", [Level("0.40", "5")]);

        Assert.True(book.ApplyDelta(Delta("yes", "0.40", "2.50")));
        Assert.Equal("7.50", book.Bids("yes")[0].Quantity.ToString());
        Assert.False(book.ApplyDelta(Delta("yes", "0.40", "-7.51")));
        Assert.Equal("7.50", book.Bids("yes")[0].Quantity.ToString());
        Assert.True(book.ApplyDelta(Delta("yes", "0.40", "-7.50")));
        Assert.Equal(0, book.YesLevels);
        Assert.False(book.ApplyDelta(Delta("no", "0.10", "-0.01")));
    }

    [Fact]
    public void AnyDeltaSideOtherThanYesIsTheNoSideAsInTheReference()
    {
        var book = new LocalOrderBook("A");

        Assert.True(book.ApplyDelta(Delta("no", "0.60", "1")));
        Assert.True(book.ApplyDelta(Delta("NO", "0.61", "1")));
        Assert.True(book.ApplyDelta(Delta("sideways", "0.62", "1")));

        Assert.Equal((0, 3), (book.YesLevels, book.NoLevels));
    }

    [Fact]
    public void AQuantityThatWouldOverflowIsRefusedNotWrapped()
    {
        var book = new LocalOrderBook("A", [new PriceLevel(Dollars.Parse("0.40"), Contracts.FromHundredths(long.MaxValue - 1))]);

        Assert.False(book.ApplyDelta(new OrderbookDeltaMsg("A", Dollars.Parse("0.40"), Contracts.FromHundredths(5), "yes")));
        Assert.Equal(long.MaxValue - 1, book.Bids("yes")[0].Quantity.Hundredths);
    }

    [Fact]
    public void ARestBookIsApproximateAndASnapshotBookIsNot()
    {
        var rest = LocalOrderBook.FromRest("A", new OrderBook([Level("0.40", "5")], []));
        var snapshot = LocalOrderBook.FromSnapshot(new OrderbookSnapshotMsg("A", yesDollarsFp: [Level("0.40", "5")]));

        Assert.True(rest.Approximate);
        Assert.False(snapshot.Approximate);
        Assert.True(rest.Copy().Approximate);
    }

    [Fact]
    public void ABookNeedsATickerAndNullArgumentsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new LocalOrderBook(" "));
        Assert.Throws<ArgumentNullException>(() => LocalOrderBook.FromSnapshot(null!));
        Assert.Throws<ArgumentNullException>(() => LocalOrderBook.FromRest("A", null!));
        Assert.Throws<ArgumentNullException>(() => new LocalOrderBook("A").ApplyDelta(null!));
    }

    [Fact]
    public void TheTrackerRefusesNullsAndSurfacesAnUnreadableOrderBookPayload()
    {
        var tracker = new BookTracker();

        Assert.Throws<ArgumentNullException>(() => tracker.Process(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.BeginSubscription(1, null!));
        Assert.Throws<ArgumentNullException>(() => tracker.BeginMarkets(1, null!));
        Assert.Throws<ArgumentNullException>(() => tracker.ForgetMarkets(1, null!));
        Assert.Throws<KalshiResponseException>(() => tracker.Process(Envelope("""{"type":"orderbook_delta","sid":1,"seq":1,"msg":{"market_ticker":"A"}}""")));
        Assert.Throws<KalshiResponseException>(() => tracker.Process(Envelope("""{"type":"orderbook_snapshot","sid":1,"seq":1}""")));
    }

    [Fact]
    public void AGapNamesEveryMarketOfTheSubscriptionInOrderAndSaysWhatWasExpected()
    {
        var tracker = new BookTracker();
        tracker.BeginSubscription(1, ["B", "A"]);
        tracker.Process(Envelope("""{"type":"orderbook_snapshot","sid":1,"seq":1,"msg":{"market_ticker":"A","yes_dollars_fp":[["0.4000","1.00"]],"no_dollars_fp":[]}}"""));

        var events = tracker.Process(Envelope("""{"type":"orderbook_delta","sid":1,"seq":4,"msg":{"market_ticker":"A","price_dollars":"0.4000","delta_fp":"1.00","side":"yes"}}"""));

        var gap = Assert.Single(events, e => e.Kind == BookEventKinds.Gap);
        Assert.Equal(["A", "B"], gap.AffectedTickers);
        Assert.Equal("seq 4, expected 2", gap.Detail);
        Assert.Equal(["A", "B"], tracker.Stale.Order());
    }

    [Fact]
    public void MarkingMarketsStaleMakesTheirBooksIgnoreDeltasUntilAFreshSnapshot()
    {
        var tracker = new BookTracker();
        tracker.Process(Envelope("""{"type":"orderbook_snapshot","sid":1,"seq":1,"msg":{"market_ticker":"A","yes_dollars_fp":[["0.4000","1.00"]],"no_dollars_fp":[]}}"""));

        tracker.MarkStale(["A"]);
        var ignored = tracker.Process(Envelope("""{"type":"orderbook_delta","sid":1,"seq":2,"msg":{"market_ticker":"A","price_dollars":"0.4000","delta_fp":"1.00","side":"yes"}}"""));
        Assert.Equal("1.00", tracker.Books["A"].Bids("yes")[0].Quantity.ToString());   // the delta was ignored
        var fresh = tracker.Process(Envelope("""{"type":"orderbook_snapshot","sid":1,"seq":3,"msg":{"market_ticker":"A","yes_dollars_fp":[["0.5000","2.00"]],"no_dollars_fp":[]}}"""));

        Assert.Empty(ignored);
        Assert.Single(fresh);
        Assert.Empty(tracker.Stale);
        Assert.Throws<ArgumentNullException>(() => tracker.MarkStale(null!));
    }
}
