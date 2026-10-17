using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Tests.Rest;
using KalTerm.Kalshi.WebSocket;
using static KalTerm.Kalshi.Tests.WebSocket.WsTestKit;

namespace KalTerm.Kalshi.Tests.WebSocket;

/// <summary>
/// Overflow: a consumer that falls behind makes the client close the connection and (by default) reconnect, with a message
/// that says what was dropped. The first five mirror the reference's test_ws_overflow.py.
/// </summary>
public sealed class OverflowTests : IDisposable
{
    private readonly FakeWsConnector _connector = new();
    private readonly AutoAdvanceTimeProvider _time = new(autoFire: false);
    private readonly Ed25519PrivateKey _key = Ed25519PrivateKey.FromPem(RestTestKit.SamplePem());
    private readonly KalshiSigner _signer;

    public OverflowTests()
    {
        _signer = new KalshiSigner("test-key", _key, _time);
    }

    public void Dispose()
    {
        _signer.Dispose();
        _key.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private KalshiWebSocket Client(int queue, bool autoReconnect = true) =>
        new(
            KalshiSettings.Default,
            _signer,
            new KalshiWebSocketOptions
            {
                Connector = _connector,
                TimeProvider = _time,
                MaxQueueMessages = queue,
                AutoReconnect = autoReconnect,
                ReconnectBase = TimeSpan.FromMilliseconds(1),
                ReconnectMax = TimeSpan.FromMilliseconds(2),
                Delay = (_, token) => Task.Delay(1, token),
            });

    /// <summary>Connection 0 floods the client with <paramref name="floods"/> trades after the subscribe reply; later ones just reply.</summary>
    private void Flood(int floods, int afterReconnect = 0) =>
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            Subscribed(c, sent, 1);
            for (var seq = 1; seq <= (n == 0 ? floods : afterReconnect); seq++)
            {
                c.Push(Trade(1, seq));
            }
        };

    private static async Task<List<WsMessage>> ReadAsync(IAsyncEnumerator<WsMessage> stream, int count)
    {
        var read = new List<WsMessage>();
        for (var i = 0; i < count; i++)
        {
            read.Add(await NextAsync(stream));
        }

        return read;
    }

    [Fact]
    public async Task AnOverflowForcesAReconnectWithAnOrderedSignal()
    {
        Flood(floods: 2000, afterReconnect: 1);
        await using var client = Client(queue: 100);
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await WaitUntilAsync(() => client.Stats.Overflows == 1); // nobody is consuming

        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);
        var backlog = await ReadAsync(stream, 100);
        var eventMessage = await NextAsync(stream);
        var after = await NextAsync(stream);
        var stats = client.Stats;

        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), backlog.Select(m => m.Seq!.Value)); // in order, none skipped
        var payload = Assert.IsType<ReconnectedMsg>(eventMessage.Payload());
        Assert.Equal("overflow", payload.Reason);
        Assert.InRange(payload.Dropped, 1, 1900);
        Assert.Equal("trade", payload.Resubscribed.Single().Channel);
        Assert.Equal(("trade", 1L), (after.Type, after.Seq!.Value)); // fresh connection, fresh counter
        Assert.Equal(100, stats.QueueLimit);
        Assert.Equal(100, stats.HighWater);
        Assert.Equal(1, stats.Overflows);
        Assert.Equal(payload.Dropped, stats.Dropped);
    }

    [Fact]
    public async Task AnOverflowWithoutAutoReconnectEndsTheStreamWithAnErrorAfterTheBacklog()
    {
        Flood(floods: 1000);
        await using var client = Client(queue: 50, autoReconnect: false);
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await WaitUntilAsync(() => client.Stats.Overflows == 1);

        var (messages, end) = await DrainAsync(client);

        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), messages.Select(m => m.Seq!.Value)); // the backlog is still delivered first
        Assert.Equal("receive queue overflow", Assert.IsType<KalshiWsException>(end).Message);
    }

    [Fact]
    public async Task ThereIsNoOverflowWhenTheConsumerKeepsUp()
    {
        Flood(floods: 300);
        await using var client = Client(queue: 1000);
        await client.ConnectAsync(Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);

        var seqs = (await ReadAsync(stream, 300)).Select(m => m.Seq!.Value).ToList();
        var stats = client.Stats;

        Assert.Equal(Enumerable.Range(1, 300).Select(i => (long)i), seqs);
        Assert.Equal((0L, 0L), (stats.Overflows, stats.Dropped));
        Assert.InRange(stats.HighWater, 1, 300);
        Assert.Equal(0, stats.QueueDepth);
    }

    [Fact]
    public async Task TheReconnectAfterAnOverflowWaitsForTheConsumerToDrain()
    {
        Flood(floods: 1000);
        await using var client = Client(queue: 100);
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await WaitUntilAsync(() => client.Stats.Overflows == 1);

        await Task.Delay(300, Token); // many times the backoff
        Assert.Equal(1, _connector.ConnectCount); // no reconnect storm while the queue is still full
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);
        var first = await ReadAsync(stream, 50); // drain to half the limit
        await WaitUntilAsync(() => _connector.ConnectCount == 2); // now it reconnects
        var rest = await ReadAsync(stream, 50);
        var eventMessage = await NextAsync(stream);

        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), first.Concat(rest).Select(m => m.Seq!.Value));
        Assert.Equal("overflow", Assert.IsType<ReconnectedMsg>(eventMessage.Payload()).Reason);
        Assert.Equal(1, client.Stats.Overflows); // the new connection did not overflow again
    }

    [Fact]
    public async Task ClosingWhileWaitingForTheConsumerToDrainEndsCleanly()
    {
        Flood(floods: 1000);
        var client = Client(queue: 100);
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await WaitUntilAsync(() => client.Stats.Overflows == 1);

        await client.CloseAsync();
        var (messages, end) = await DrainAsync(client);

        Assert.Null(end);
        Assert.Equal(100, messages.Count);
        Assert.Equal(1, _connector.ConnectCount);
        await client.DisposeAsync();
    }

    // ---- what the port adds ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheReconnectedMessageIsNeverLostWhenTheQueueIsFull()
    {
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            Subscribed(c, sent, n + 1);
            if (n == 0)
            {
                c.Push(Trade(1, 1));
                c.Push(Trade(1, 2));
                c.PushClose(); // the queue (2) is exactly full; this is a plain connection loss, not an overflow
            }
        };
        await using var client = Client(queue: 2);
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await WaitUntilAsync(() => client.Subscriptions is [{ Sid: 2 }]); // resubscribed; the message announcing it is waiting for room

        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);
        var messages = await ReadAsync(stream, 3);

        Assert.Equal(["trade", "trade", KalshiWebSocket.ReconnectedType], messages.Select(m => m.Type));
        var payload = Assert.IsType<ReconnectedMsg>(messages[2].Payload());
        Assert.Equal(("connection_lost", 0), (payload.Reason, payload.Dropped));
        Assert.Equal(0, client.Stats.Overflows);
    }

    [Fact]
    public async Task DataHeldBeyondTheQueueLimitIsAnOverflowAndTheClientRecovers()
    {
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            Subscribed(c, sent, n + 1);
            switch (n)
            {
                case 0:
                    c.PushClose();
                    break;
                case 1: // a burst while the subscriptions are being restored
                    for (var seq = 1; seq <= 8; seq++)
                    {
                        c.Push(Trade(2, seq));
                    }

                    break;
            }
        };
        await using var client = Client(queue: 3);
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);

        // Every reconnected message up to the one that says "overflow". A restore that finishes while the reader is
        // overflowing may carry the drop count itself, so the count is judged over all of them: reported once, in total.
        var events = new List<ReconnectedMsg>();
        for (var i = 0; i < 40 && events.All(e => e.Reason != "overflow"); i++)
        {
            if ((await NextAsync(stream)).Payload() is ReconnectedMsg found)
            {
                events.Add(found);
            }
        }

        Assert.Contains(events, e => e.Reason == "overflow");
        Assert.True(events.Sum(e => e.Dropped) >= 1);
        Assert.True(client.Stats.Overflows >= 1);
        Assert.Equal(events.Sum(e => e.Dropped), (int)client.Stats.Dropped);
    }

    [Fact]
    public async Task TheStatsStartEmptyAndReportTheLimit()
    {
        await using var client = Client(queue: 7);

        Assert.Equal(new WsStats(0, 7, 0, 0, 0), client.Stats);
    }

    [Fact]
    public async Task DroppingTheConnectionMakesTheClientReconnect()
    {
        _connector.OnOpen = (n, c) => c.OnSend = sent => Subscribed(c, sent, n + 1);
        await using var client = Client(queue: 10);
        await client.DropConnectionAsync(); // before connecting: nothing to drop
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);

        await client.DropConnectionAsync();

        var payload = Assert.IsType<ReconnectedMsg>((await NextAsync(stream)).Payload());
        Assert.Equal(("connection_lost", 2), (payload.Reason, payload.Resubscribed.Single().Sid));
    }
}
