using KalTerm.Core.Money;
using KalTerm.Kalshi.Books;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using KalTerm.Kalshi.WebSocket;
using static KalTerm.Kalshi.Tests.Live.LiveSupport;

namespace KalTerm.Kalshi.Tests.Live;

/// <summary>
/// Opt-in integration tests of the WebSocket client against the real Kalshi API, using the read-only key. Skipped by default
/// and in CI; run exactly as <see cref="LiveKalshiTests"/> (see CLAUDE.md).
///
/// They only read: one signed WebSocket upgrade per test, <c>subscribe</c> commands on public data channels, and one REST
/// markets page. The client has no command that could place or change an order. Nothing about the account is read or printed;
/// what was read is summarised in <c>live-summary.txt</c>.
/// </summary>
public class LiveKalshiWebSocketTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Reads the stream for <paramref name="duration"/>, then stops. A stream that ends or fails earlier ends the read.</summary>
    private static async Task<List<WsMessage>> ReadForAsync(KalshiWebSocket client, TimeSpan duration)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(Token);
        window.CancelAfter(duration);
        var messages = new List<WsMessage>();
        try
        {
            await foreach (var message in client.Messages(window.Token))
            {
                messages.Add(message);
            }
        }
        catch (OperationCanceledException) when (window.IsCancellationRequested && !Token.IsCancellationRequested)
        {
            // The window closed: that is how a timed read ends.
        }

        return messages;
    }

    private static void AssertIsAPrice(Dollars price) =>
        Assert.True(price >= Dollars.Zero && price <= Dollars.One, $"{price} is not a price between $0 and $1");

    /// <summary>Every message that carries a sequence number must continue the one before it, within its subscription.</summary>
    private static void AssertContiguous(IEnumerable<WsMessage> messages)
    {
        var last = new Dictionary<int, long>();
        foreach (var message in messages.Where(m => m is { Sid: not null, Seq: not null }))
        {
            if (last.TryGetValue(message.Sid!.Value, out var previous))
            {
                Assert.True(message.Seq == previous + 1, $"sid {message.Sid}: seq {message.Seq} after {previous}");
            }

            last[message.Sid!.Value] = message.Seq!.Value;
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task TheTradeStreamParsesWithOneContiguousSequence()
    {
        var (settings, signer) = Credentials();
        using (signer)
        {
            await using var client = new KalshiWebSocket(settings, signer);
            await client.ConnectAsync(Token);
            await client.SubscribeAsync("trade", cancellationToken: Token);

            var messages = await ReadForAsync(client, TimeSpan.FromSeconds(8));

            var trades = messages.Where(m => m.Type == "trade").Select(m => (TradeMsg)m.Payload()!).ToList();
            Say($"trade channel, 8 s: {trades.Count} trades");
            foreach (var trade in trades.Take(3))
            {
                Say($"  {trade.MarketTicker}: yes {trade.YesPriceDollars} / no {trade.NoPriceDollars} x {trade.CountFp}");
            }

            Assert.True(trades.Count >= 3, $"expected at least 3 trades in 8 s, got {trades.Count}");
            Assert.All(trades, t => Assert.Equal(Dollars.One, t.YesPriceDollars + t.NoPriceDollars));
            AssertContiguous(messages);
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task TheClientReconnectsAndResubscribesAfterTheLinkIsCut()
    {
        var (settings, signer) = Credentials();
        using (signer)
        {
            await using var client = new KalshiWebSocket(settings, signer, new KalshiWebSocketOptions { ReconnectBase = TimeSpan.FromSeconds(0.2) });
            await client.ConnectAsync(Token);
            var oldSid = await client.SubscribeAsync("trade", cancellationToken: Token);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
            limit.CancelAfter(TimeSpan.FromSeconds(40));
            var types = new List<string>();
            ReconnectedMsg? reconnected = null;

            await foreach (var message in client.Messages(limit.Token))
            {
                types.Add(message.Type);
                if (types.Count == 1)
                {
                    await client.DropConnectionAsync(); // cut the link from our side
                }

                if (message.Payload() is ReconnectedMsg found)
                {
                    reconnected = found;
                }
                else if (reconnected is not null && message.Type == "trade")
                {
                    break; // data flows again on the new connection
                }
            }

            Say($"after the cut: {types.Count(t => t == "trade")} trades, then reconnected ({reconnected?.Reason}), then trades resumed");
            Assert.NotNull(reconnected);
            Assert.Equal("connection_lost", reconnected.Reason);
            Assert.Equal(oldSid, reconnected.Resubscribed.Single().OldSid);
            Assert.Equal("trade", types[^1]);
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task TheMultivariateLifecycleStreamParsesThroughTheTypedModels()
    {
        var (settings, signer) = Credentials();
        using (signer)
        {
            await using var client = new KalshiWebSocket(settings, signer);
            await client.ConnectAsync(Token);
            await client.SubscribeAsync("multivariate_market_lifecycle", cancellationToken: Token);

            var messages = await ReadForAsync(client, TimeSpan.FromSeconds(6));

            var kinds = messages
                .Where(m => m.Type != KalshiWebSocket.ControlType)
                .Select(m => m.Payload()) // throws if a real message does not fit its model
                .GroupBy(p => p switch { LifecycleMsg l => l.EventType, EventLifecycleMsg => "event_lifecycle", _ => "other" })
                .ToDictionary(g => g.Key, g => g.Count());
            Say($"multivariate lifecycle, 6 s: {messages.Count} messages: {string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}"))}");

            Assert.NotEmpty(messages);
            Assert.DoesNotContain("other", kinds.Keys);
            AssertContiguous(messages);
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task TheBusiestMarketsOrderBookMessagesParseExactly()
    {
        var (settings, signer) = Credentials();
        using (signer)
        {
            string ticker;
            using (var rest = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer }))
            {
                var page = await rest.MarketsPageAsync(
                    KalshiQuery.Empty.With("limit", 1000).With("status", "open").With("mve_filter", "exclude"), Token);
                ticker = page.Markets.OrderByDescending(m => m.Volume24hFp ?? Contracts.Zero).First().Ticker;
            }

            await using var client = new KalshiWebSocket(settings, signer);
            await client.ConnectAsync(Token);
            await client.SubscribeAsync("orderbook_delta", [ticker], cancellationToken: Token);

            var messages = await ReadForAsync(client, TimeSpan.FromSeconds(8));

            var book = messages.Select(m => m.Payload()).OfType<OrderbookSnapshotMsg>().ToList();
            var deltas = messages.Select(m => m.Payload()).OfType<OrderbookDeltaMsg>().ToList();
            Say($"{ticker}, 8 s: {book.Count} snapshot(s), {deltas.Count} delta(s)");
            if (book.Count > 0)
            {
                Say($"  snapshot: {book[0].YesDollarsFp.Count} yes / {book[0].NoDollarsFp.Count} no levels");
            }

            if (deltas.Count > 0)
            {
                Say($"  first delta: {deltas[0].Side} {deltas[0].DeltaFp} at {deltas[0].PriceDollars}");
            }

            var first = messages.First(m => m.Type != KalshiWebSocket.ControlType);
            Assert.Equal("orderbook_snapshot", first.Type);
            Assert.All(
                book.SelectMany(b => b.YesDollarsFp.Concat(b.NoDollarsFp)),
                level =>
                {
                    AssertIsAPrice(level.Price);
                    Assert.True(level.Quantity > Contracts.Zero);
                });
            Assert.All(deltas, d => AssertIsAPrice(d.PriceDollars));
            Assert.All(deltas, d => Assert.True(d.Side is "yes" or "no", $"unexpected side '{d.Side}'"));
            AssertContiguous(messages);
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task ABookBuiltFromTheLiveStreamStaysFreshNeverCrossesAndMatchesRestWhereNothingMoved()
    {
        var (settings, signer) = Credentials();
        using (signer)
        {
            string[] tickers;
            using (var rest = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer }))
            {
                var page = await rest.MarketsPageAsync(
                    KalshiQuery.Empty.With("limit", 1000).With("status", "open").With("mve_filter", "exclude"), Token);
                // Moderately active markets: busy enough to see deltas, quiet enough that a REST read right after the window can still agree.
                tickers = [.. page.Markets.OrderByDescending(m => m.Volume24hFp ?? Contracts.Zero).Skip(20).Take(3).Select(m => m.Ticker)];
            }

            await using var client = new KalshiWebSocket(settings, signer);
            await client.ConnectAsync(Token);
            var sid = await client.SubscribeAsync("orderbook_delta", tickers, cancellationToken: Token);
            var tracker = new BookTracker();
            tracker.BeginSubscription(sid, tickers);

            var messages = await ReadForAsync(client, TimeSpan.FromSeconds(60));
            var events = messages.SelectMany(tracker.Process).ToList();

            using var restAfter = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer });
            var agreed = 0;
            foreach (var ticker in tickers)
            {
                var built = tracker.Books[ticker];
                var rest = await restAfter.OrderBookAsync(ticker, cancellationToken: Token);
                var fromRest = LocalOrderBook.FromRest(ticker, rest);
                var same = built.Bids("yes").SequenceEqual(fromRest.Bids("yes")) && built.Bids("no").SequenceEqual(fromRest.Bids("no"));
                if (same)
                {
                    agreed++;
                }

                Say($"{ticker}: {built.YesLevels} yes / {built.NoLevels} no levels, best yes bid {built.BestYesBid}, best yes ask {built.BestYesAsk}; equal to REST: {same}");
                if (built.BestYesBid is { } bid && built.BestYesAsk is { } ask)
                {
                    Assert.True(bid < ask, $"{ticker}: the book is crossed (bid {bid}, ask {ask})");
                }
            }

            var deltas = events.Count(e => e.Kind == BookEventKinds.Delta);
            Say($"orderbook_delta, 60 s, {tickers.Length} markets: {events.Count(e => e.Kind == BookEventKinds.Snapshot)} snapshot(s), {deltas} delta(s), {events.Count(e => e.Kind == BookEventKinds.Gap)} gap(s); {agreed}/{tickers.Length} books equal to REST at the end");
            Assert.DoesNotContain(events, e => e.Kind is BookEventKinds.Gap or BookEventKinds.Reset or BookEventKinds.Message);
            Assert.Empty(tracker.Stale);
            Assert.Equal(tickers.Length, tracker.Books.Count);
            Assert.True(deltas > 0, "no order book delta in 60 s on three active markets");
        }
    }
}
