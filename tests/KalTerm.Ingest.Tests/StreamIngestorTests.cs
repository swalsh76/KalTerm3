using KalTerm.Data.Gaps;
using KalTerm.Data.Markets;
using KalTerm.Data.Writing;
using KalTerm.Kalshi.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using static KalTerm.Ingest.Tests.StreamTestKit;

namespace KalTerm.Ingest.Tests;

public class StreamIngestorTests
{
    private readonly MarketIdMap _ids = new();
    private readonly FakeRowWriter<TickerRow> _tickers = new();
    private readonly FakeRowWriter<TradeRow> _trades = new();
    private readonly FakeGapLog _gaps = new();
    private readonly AutoAdvanceTimeProvider _clock = new(autoFire: false, utcStart: Start);

    public StreamIngestorTests()
    {
        _ids.AddRange([KeyValuePair.Create("A-1", 1), KeyValuePair.Create("B-2", 2)]);
    }

    private StreamIngestor Ingestor(StreamIngestorOptions? options = null) =>
        new(_ids, _tickers, _trades, _gaps, options, _clock, NullLogger.Instance);

    [Fact]
    public async Task TickersAndTradesBecomeRowsWithTheStoredMarketIds()
    {
        var ingestor = Ingestor();

        await ingestor.RunAsync(Stream([Ticker("A-1"), Trade("B-2", "t-9", side: "no"), Ticker("B-2")]), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], _tickers.Rows.Select(r => r.MarketId));
        var trade = Assert.Single(_trades.Rows);
        Assert.Equal((2, "t-9", TakerSide.No, 300L), (trade.MarketId, trade.TradeId, trade.TakerSide, trade.CountE2));
        var stats = ingestor.Stats;
        Assert.Equal((2L, 1L), (stats.Tickers, stats.Trades));
        Assert.Equal(Start, stats.LastDataAt);
    }

    [Fact]
    public async Task AMarketNotInTheCatalogIsCountedAndNotStored()
    {
        var ingestor = Ingestor();

        await ingestor.RunAsync(
            Stream(Enumerable.Range(0, 12).Select(i => Ticker("NEW-" + i)).Append(Trade("NEW-X")).Append(Ticker("A-1"))),
            TestContext.Current.CancellationToken);

        Assert.Single(_tickers.Rows);
        Assert.Empty(_trades.Rows);
        Assert.Equal(13, ingestor.Stats.UnknownMarkets);
    }

    [Fact]
    public async Task AMessageWithoutAReadablePayloadIsCountedAndTheStreamCarriesOn()
    {
        var ingestor = Ingestor();
        var broken = new KalTerm.Kalshi.Models.WsMessage("ticker", Sid: 1, Seq: 1, ReceivedAt: Start);
        var wrongShape = Message("trade", """{"market_ticker":"A-1"}""");

        await ingestor.RunAsync(Stream([broken, wrongShape, Ticker("A-1")]), TestContext.Current.CancellationToken);

        Assert.Equal(2, ingestor.Stats.Malformed);
        Assert.Single(_tickers.Rows);
    }

    [Fact]
    public async Task ControlMarkersAndOtherTypesAreIgnored()
    {
        var ingestor = Ingestor();
        var control = new KalTerm.Kalshi.Models.WsMessage(KalshiWebSocket.ControlType, Sid: 1, Seq: 2, ReceivedAt: Start);

        await ingestor.RunAsync(Stream([control, Message("market_lifecycle_v2", """{"event_type":"created"}""")]), TestContext.Current.CancellationToken);

        Assert.Equal(2, ingestor.Stats.Ignored);
        Assert.Empty(_gaps.Stored);
    }

    [Fact]
    public async Task AReplacedLinkIsLoggedAsAGapFromTheLastDataToTheReconnect()
    {
        var ingestor = Ingestor();
        var lastData = Start.AddSeconds(5);
        var back = Start.AddSeconds(35);

        await ingestor.RunAsync(Stream([Ticker("A-1", Start), Ticker("A-1", lastData), Reconnected(back, dropped: 12)]), TestContext.Current.CancellationToken);

        var gap = Assert.Single(_gaps.Stored);
        Assert.Equal(GapKinds.WsReconnected, gap.Kind);
        Assert.Equal(lastData.UtcDateTime, gap.StartedAt);
        Assert.Equal(back.UtcDateTime, gap.EndedAt);
        Assert.Contains("connection_lost", gap.Detail, StringComparison.Ordinal);
        Assert.Contains("12 messages dropped", gap.Detail, StringComparison.Ordinal);
        Assert.Equal(1, ingestor.Stats.Reconnects);
        Assert.Equal(1, ingestor.Stats.GapsLogged);
    }

    [Fact]
    public async Task AReconnectBeforeAnyDataStartsTheGapAtTheReconnect()
    {
        await Ingestor().RunAsync(Stream([Reconnected(Start.AddSeconds(2))]), TestContext.Current.CancellationToken);

        var gap = Assert.Single(_gaps.Stored);
        Assert.Equal(gap.EndedAt, gap.StartedAt);
    }

    [Fact]
    public async Task ALinkLostForGoodIsLoggedAndTheErrorIsRethrown()
    {
        var ingestor = Ingestor();
        var lost = new KalshiWsException("reconnect attempts exhausted");

        var thrown = await Assert.ThrowsAsync<KalshiWsException>(
            () => ingestor.RunAsync(Stream([Ticker("A-1")], lost, _ => _clock.Advance(TimeSpan.FromMilliseconds(100))), TestContext.Current.CancellationToken));

        Assert.Same(lost, thrown);
        var gap = Assert.Single(_gaps.Stored);
        Assert.Equal(GapKinds.WsLost, gap.Kind);
        Assert.Equal("reconnect attempts exhausted", gap.Detail);
        Assert.Equal(Start.UtcDateTime, gap.StartedAt);
    }

    [Fact]
    public async Task RowsAWriterDroppedBecomeGapsNamingTheTable()
    {
        var from = Start.UtcDateTime;
        _tickers.Dropped.Enqueue(new DroppedRange(from, from.AddSeconds(3), 900));
        _trades.Dropped.Enqueue(new DroppedRange(from.AddSeconds(1), from.AddSeconds(2), 4));

        await Ingestor().RunAsync(Stream([]), TestContext.Current.CancellationToken);

        Assert.Equal(2, _gaps.Stored.Count);
        Assert.All(_gaps.Stored, g => Assert.Equal(GapKinds.WriterDropped, g.Kind));
        var ticker = Assert.Single(_gaps.Stored, g => g.Detail!.StartsWith("ticker:", StringComparison.Ordinal));
        Assert.Equal((from, from.AddSeconds(3)), (ticker.StartedAt, ticker.EndedAt));
        Assert.Contains("900 rows", ticker.Detail, StringComparison.Ordinal);
        Assert.Contains(_gaps.Stored, g => g.Detail!.StartsWith("trade: 4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GapsAreStoredWhileTheStreamRunsOnceTheFlushIntervalHasPassed()
    {
        var ingestor = Ingestor(new StreamIngestorOptions { FlushInterval = TimeSpan.FromSeconds(1) });
        var storedMidStream = -1;
        var messages = new[] { Reconnected(Start), Ticker("A-1"), Ticker("A-1") };

        await ingestor.RunAsync(
            Stream(
                messages,
                beforeEach: m =>
                {
                    if (ReferenceEquals(m, messages[2]))
                    {
                        storedMidStream = _gaps.Stored.Count;
                    }

                    if (ReferenceEquals(m, messages[1]))
                    {
                        _clock.Advance(TimeSpan.FromSeconds(2));
                    }
                }),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, storedMidStream);
        Assert.Equal(1, _gaps.Calls);
    }

    [Fact]
    public async Task GapsWaitForTheDatabaseAndAreStoredOnceWithoutDuplicates()
    {
        _gaps.FailNext = 1;
        var ingestor = Ingestor(new StreamIngestorOptions { FlushInterval = TimeSpan.FromSeconds(1) });
        var messages = new[] { Reconnected(Start), Ticker("A-1"), Ticker("A-1") };
        var pendingAfterFailure = -1;

        await ingestor.RunAsync(
            Stream(
                messages,
                beforeEach: m =>
                {
                    if (ReferenceEquals(m, messages[1]))
                    {
                        _clock.Advance(TimeSpan.FromSeconds(2));
                    }

                    if (ReferenceEquals(m, messages[2]))
                    {
                        pendingAfterFailure = ingestor.Stats.GapsPending;
                    }
                }),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, pendingAfterFailure);
        Assert.Single(_gaps.Stored);
        Assert.Equal((1L, 0), (ingestor.Stats.GapsLogged, ingestor.Stats.GapsPending));
        Assert.Equal(2, _gaps.Calls);
    }

    [Fact]
    public async Task WhenTooManyGapsWaitTheOldestAreDiscardedAndCounted()
    {
        _gaps.FailNext = int.MaxValue;
        var ingestor = Ingestor(new StreamIngestorOptions { MaxPendingGaps = 2 });

        await ingestor.RunAsync(Stream([Reconnected(Start.AddSeconds(1)), Reconnected(Start.AddSeconds(2)), Reconnected(Start.AddSeconds(3))]), TestContext.Current.CancellationToken);

        var stats = ingestor.Stats;
        Assert.Equal((2, 1L, 0L), (stats.GapsPending, stats.GapsLost, stats.GapsLogged));
    }

    [Fact]
    public async Task CancellingStopsTheStreamAndStillFlushes()
    {
        using var cts = new CancellationTokenSource();
        var ingestor = Ingestor();
        var messages = new[] { Reconnected(Start), Ticker("A-1"), Ticker("A-1") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ingestor.RunAsync(Stream(messages, beforeEach: m =>
            {
                if (ReferenceEquals(m, messages[2]))
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
            }), cts.Token));

        Assert.Single(_gaps.Stored);
        Assert.Single(_tickers.Rows);
    }

    [Fact]
    public void BadOptionsAndMissingPartsAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Ingestor(new StreamIngestorOptions { FlushInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ingestor(new StreamIngestorOptions { MaxPendingGaps = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ingestor(new StreamIngestorOptions { FinalFlushTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentNullException>(() => new StreamIngestor(null!, _tickers, _trades, _gaps));
        Assert.Throws<ArgumentNullException>(() => new StreamIngestor(_ids, null!, _trades, _gaps));
        Assert.Throws<ArgumentNullException>(() => new StreamIngestor(_ids, _tickers, null!, _gaps));
        Assert.Throws<ArgumentNullException>(() => new StreamIngestor(_ids, _tickers, _trades, null!));
    }

    [Fact]
    public async Task ANullStreamIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Ingestor().RunAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AGapNotedBetweenStreamsIsStoredByAFlush()
    {
        var ingestor = Ingestor();

        ingestor.NoteGap(GapKinds.WsDown, Start.UtcDateTime, Start.AddSeconds(5).UtcDateTime, "no stream for 5 s");
        Assert.Empty(_gaps.Stored);
        await ingestor.FlushAsync(TestContext.Current.CancellationToken);

        var gap = Assert.Single(_gaps.Stored);
        Assert.Equal((GapKinds.WsDown, Start.UtcDateTime, Start.AddSeconds(5).UtcDateTime), (gap.Kind, gap.StartedAt, gap.EndedAt));
        Assert.Equal(0, ingestor.Stats.GapsPending);
    }

    [Fact]
    public async Task ItCanRunForALaterStreamAndCountersAndTheLastDataCarryOver()
    {
        var ingestor = Ingestor();
        var lastData = Start.AddSeconds(1);

        await ingestor.RunAsync(Stream([Ticker("A-1", lastData)]), TestContext.Current.CancellationToken);
        await ingestor.RunAsync(Stream([Ticker("B-2", Start.AddSeconds(9)), Reconnected(Start.AddSeconds(30))]), TestContext.Current.CancellationToken);

        Assert.Equal(2, ingestor.Stats.Tickers);
        var gap = Assert.Single(_gaps.Stored);
        Assert.Equal(Start.AddSeconds(9).UtcDateTime, gap.StartedAt);
    }
}
