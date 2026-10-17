using System.Text.Json;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Tests.Rest;
using KalTerm.Kalshi.WebSocket;
using Microsoft.Extensions.Logging;

namespace KalTerm.Kalshi.Tests.WebSocket;

/// <summary>The client against a scripted in-memory connection and a fake clock.</summary>
public sealed class KalshiWebSocketTests : IDisposable
{
    private const string Tick = """{"type":"ticker","sid":7,"seq":2,"msg":{"market_ticker":"A","price_dollars":"0.5600"}}""";

    private readonly FakeWsConnector _connector = new();
    private readonly AutoAdvanceTimeProvider _time = new(autoFire: false);
    private readonly ListLogger _log = new();
    private readonly Ed25519PrivateKey _key = Ed25519PrivateKey.FromPem(RestTestKit.SamplePem());
    private readonly KalshiSigner _signer;

    public KalshiWebSocketTests()
    {
        _signer = new KalshiSigner("test-key", _key, _time);
    }

    public void Dispose()
    {
        _signer.Dispose();
        _key.Dispose();
    }

    private KalshiWebSocket Client(KalshiSettings? settings = null, int queue = 500_000) =>
        new(
            settings ?? KalshiSettings.Default,
            _signer,
            new KalshiWebSocketOptions { Connector = _connector, TimeProvider = _time, Logger = _log, MaxQueueMessages = queue, AutoReconnect = false });

    private async Task<KalshiWebSocket> ConnectedAsync(int? subscribeSid = null, int? seq = null, int queue = 500_000)
    {
        if (subscribeSid is { } sid)
        {
            _connector.Connection.AutoSubscribe(sid, seq);
        }

        var client = Client(queue: queue);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        return client;
    }

    /// <summary>Reads the stream until it ends, returning what arrived and how it ended (null: cleanly).</summary>
    private static async Task<(List<WsMessage> Messages, Exception? End)> DrainAsync(KalshiWebSocket client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var messages = new List<WsMessage>();
        try
        {
            await foreach (var message in client.Messages(timeout.Token))
            {
                messages.Add(message);
            }

            return (messages, null);
        }
        catch (KalshiWsException ex)
        {
            return (messages, ex);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    // ---- connecting -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConnectUsesTheSettingsUrlAndSignsTheUpgrade()
    {
        await using var client = await ConnectedAsync();

        Assert.Equal(KalshiSettings.Default.WebSocketUrl, _connector.Url);
        var headers = _connector.Headers!;
        Assert.Equal("test-key", headers[KalshiSigner.KeyHeader]);
        var signature = _signer.Sign(long.Parse(headers[KalshiSigner.TimestampHeader], CultureInfo.InvariantCulture), "GET", _connector.Url!.AbsolutePath);
        Assert.Equal(signature, headers[KalshiSigner.SignatureHeader]);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task ConnectingTwiceIsRefused()
    {
        await using var client = await ConnectedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, _connector.ConnectCount);
    }

    [Fact]
    public async Task AFailedConnectLeavesTheClientNotConnected()
    {
        _connector.FailWith = new HttpRequestException("refused");
        await using var client = Client();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.False(client.IsConnected);
        var error = await Assert.ThrowsAsync<KalshiWsException>(() => client.SubscribeAsync("ticker", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("not connected", error.Message);
    }

    [Fact]
    public async Task CommandsBeforeConnectingSayNotConnected()
    {
        await using var client = Client();

        var error = await Assert.ThrowsAsync<KalshiWsException>(() => client.UnsubscribeAsync(1, TestContext.Current.CancellationToken));

        Assert.Equal("not connected", error.Message);
    }

    [Fact]
    public async Task TheProductionBannerIsLoggedOnlyInProduction()
    {
        await using (Client(KalshiSettings.Create(KalshiEnvironment.Production)))
        {
        }

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("PRODUCTION", StringComparison.Ordinal));

        var quiet = new ListLogger();
        await using (new KalshiWebSocket(KalshiSettings.Default, _signer, new KalshiWebSocketOptions { Connector = _connector, Logger = quiet }))
        {
        }

        Assert.Empty(quiet.Entries);
    }

    [Fact]
    public void ANonPositiveQueueLimitIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Client(queue: 0));
    }

    // ---- commands -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SubscribeSendsTheCommandAndReturnsTheSid()
    {
        await using var client = await ConnectedAsync(subscribeSid: 7);

        var sid = await client.SubscribeAsync("ticker", ["A", "B"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(7, sid);
        Assert.Equal("""{"id":1,"cmd":"subscribe","params":{"channels":["ticker"],"market_tickers":["A","B"]}}""", _connector.Connection.Sent.Single());
        var subscription = Assert.Single(client.Subscriptions);
        Assert.Equal(new WsSubscription(7, "ticker", ["A", "B"], """{"market_tickers":["A","B"]}"""), subscription, new SubscriptionComparer());
    }

    [Fact]
    public async Task SubscribePassesExtraParametersAndAnyChannelNameThrough()
    {
        await using var client = await ConnectedAsync(subscribeSid: 9);
        var extra = new Dictionary<string, JsonElement> { ["send_initial_snapshot"] = JsonDocument.Parse("true").RootElement };

        await client.SubscribeAsync("brand_new_channel", parameters: extra, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            """{"id":1,"cmd":"subscribe","params":{"channels":["brand_new_channel"],"send_initial_snapshot":true}}""",
            _connector.Connection.Sent.Single());
        Assert.Equal("""{"send_initial_snapshot":true}""", client.Subscriptions.Single().ParametersJson);
        Assert.Empty(client.Subscriptions.Single().MarketTickers);
    }

    [Fact]
    public async Task SubscribeRefusesBadArguments()
    {
        await using var client = await ConnectedAsync(subscribeSid: 1);
        var channels = new Dictionary<string, JsonElement> { ["channels"] = JsonDocument.Parse("[]").RootElement };

        await Assert.ThrowsAsync<ArgumentException>(() => client.SubscribeAsync(" ", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SubscribeAsync("ticker", parameters: channels, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(_connector.Connection.Sent);
    }

    [Fact]
    public async Task CommandIdsIncrease()
    {
        await using var client = await ConnectedAsync(subscribeSid: 7);

        await client.SubscribeAsync("ticker", cancellationToken: TestContext.Current.CancellationToken);
        await client.SubscribeAsync("trade", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], _connector.Connection.Sent.Select(FakeWsConnection.IdOf));
    }

    [Fact]
    public async Task ASubscribeReplyWithoutASidIsAnError()
    {
        _connector.Connection.OnSend = text => _connector.Connection.Push($$$"""{"id":{{{FakeWsConnection.IdOf(text)}}},"type":"subscribed","msg":{}}""");
        await using var client = await ConnectedAsync();

        var error = await Assert.ThrowsAsync<KalshiWsException>(() => client.SubscribeAsync("ticker", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("no sid", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Subscriptions);
    }

    [Fact]
    public async Task AnErrorReplyBecomesAnExceptionWithKalshisCode()
    {
        _connector.Connection.OnSend = text =>
            _connector.Connection.Push($$$"""{"id":{{{FakeWsConnection.IdOf(text)}}},"type":"error","msg":{"code":6,"msg":"Already subscribed"}}""");
        await using var client = await ConnectedAsync();

        var error = await Assert.ThrowsAsync<KalshiWsException>(() => client.SubscribeAsync("ticker", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(6, error.Code);
        Assert.Equal("[6] Already subscribed", error.Message);
    }

    [Fact]
    public async Task AnErrorReplyWithoutDetailStillFailsTheCommand()
    {
        _connector.Connection.OnSend = text => _connector.Connection.Push($$$"""{"id":{{{FakeWsConnection.IdOf(text)}}},"type":"error"}""");
        await using var client = await ConnectedAsync();

        var error = await Assert.ThrowsAsync<KalshiWsException>(() => client.SubscribeAsync("ticker", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(error.Code);
        Assert.Equal("error", error.Message);
    }

    [Fact]
    public async Task ACommandWithoutAReplyTimesOutOnTheClock()
    {
        await using var client = await ConnectedAsync();
        var pending = client.SubscribeAsync("ticker", cancellationToken: TestContext.Current.CancellationToken);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(10));
        while (_time.PendingTimers == 0)
        {
            await Task.Delay(1, wait.Token);
        }

        _time.FireNext();

        var error = await Assert.ThrowsAsync<KalshiWsException>(() => pending);
        Assert.Equal("subscribe timed out", error.Message);
        Assert.Equal(TimeSpan.FromSeconds(10), _time.Delays[^1]);
    }

    [Fact]
    public async Task TheLinkClosingFailsAWaitingCommand()
    {
        _connector.Connection.OnSend = _ => _connector.Connection.PushClose();
        await using var client = await ConnectedAsync();

        var error = await Assert.ThrowsAsync<KalshiWsException>(() => client.SubscribeAsync("ticker", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("connection closed", error.Message);
    }

    [Fact]
    public async Task UpdatingASubscriptionStoresTheFullMarketSetKalshiReturns()
    {
        await using var client = await ConnectedAsync(subscribeSid: 7);
        await client.SubscribeAsync("ticker", ["A"], cancellationToken: TestContext.Current.CancellationToken);
        _connector.Connection.OnSend = text =>
            _connector.Connection.Push($$$"""{"id":{{{FakeWsConnection.IdOf(text)}}},"type":"ok","sid":7,"msg":{"market_tickers":["A","B"]}}""");

        var set = await client.UpdateSubscriptionAsync(7, "add_markets", ["B"], TestContext.Current.CancellationToken);

        Assert.Equal(["A", "B"], set);
        Assert.Equal(["A", "B"], client.Subscriptions.Single().MarketTickers);
        Assert.Equal("""{"market_tickers":["A","B"]}""", client.Subscriptions.Single().ParametersJson);
        Assert.Equal(
            """{"id":2,"cmd":"update_subscription","params":{"sid":7,"market_tickers":["B"],"action":"add_markets"}}""",
            _connector.Connection.Sent[1]);
    }

    [Fact]
    public async Task AnUpdateReplyWithoutAMarketSetIsAnError()
    {
        await using var client = await ConnectedAsync();
        _connector.Connection.OnSend = text => _connector.Connection.Push($$$"""{"id":{{{FakeWsConnection.IdOf(text)}}},"type":"ok","msg":{}}""");

        var error = await Assert.ThrowsAsync<KalshiWsException>(
            () => client.UpdateSubscriptionAsync(7, "add_markets", ["B"], TestContext.Current.CancellationToken));

        Assert.Contains("no market set", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatingAnUnknownSubscriptionStillReturnsTheSet()
    {
        await using var client = await ConnectedAsync();
        _connector.Connection.OnSend = text =>
            _connector.Connection.Push($$$"""{"id":{{{FakeWsConnection.IdOf(text)}}},"type":"ok","msg":{"market_tickers":["B"]}}""");

        var set = await client.UpdateSubscriptionAsync(99, "delete_markets", ["A"], TestContext.Current.CancellationToken);

        Assert.Equal(["B"], set);
        Assert.Empty(client.Subscriptions);
    }

    [Fact]
    public async Task UnsubscribingRemovesTheSubscription()
    {
        await using var client = await ConnectedAsync(subscribeSid: 7);
        await client.SubscribeAsync("ticker", cancellationToken: TestContext.Current.CancellationToken);
        _connector.Connection.OnSend = text => _connector.Connection.Push($$$"""{"id":{{{FakeWsConnection.IdOf(text)}}},"type":"unsubscribed","msg":{}}""");

        await client.UnsubscribeAsync(7, TestContext.Current.CancellationToken);

        Assert.Empty(client.Subscriptions);
        Assert.Equal("""{"id":2,"cmd":"unsubscribe","params":{"sids":[7]}}""", _connector.Connection.Sent[1]);
    }

    // ---- the message stream ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DataMessagesArriveInOrderStampedWithTheLocalReceiveTime()
    {
        await using var client = await ConnectedAsync();
        _time.Advance(TimeSpan.FromSeconds(5));
        _connector.Connection.Push(Tick);
        _connector.Connection.Push("""{"type":"trade","sid":7,"seq":3,"msg":{"trade_id":"t"}}""");
        _connector.Connection.PushClose();

        var (messages, end) = await DrainAsync(client);

        Assert.Equal(["ticker", "trade"], messages.Select(m => m.Type));
        Assert.Equal([2L, 3L], messages.Select(m => m.Seq!.Value));
        Assert.All(messages, m => Assert.Equal(_time.GetUtcNow(), m.ReceivedAt));
        Assert.IsType<TickerMsg>(messages[0].Payload());
        Assert.IsType<KalshiWsException>(end);
    }

    [Fact]
    public async Task ASubscribeReplyThatUsedASequenceNumberLeavesAControlMarkerInTheStream()
    {
        await using var client = await ConnectedAsync(subscribeSid: 7, seq: 1);

        await client.SubscribeAsync("ticker", cancellationToken: TestContext.Current.CancellationToken);
        _connector.Connection.Push(Tick);
        _connector.Connection.PushClose();

        var (messages, _) = await DrainAsync(client);
        Assert.Equal([KalshiWebSocket.ControlType, "ticker"], messages.Select(m => m.Type));
        Assert.Equal(7, messages[0].Sid);
        Assert.Equal(1, messages[0].Seq);
        Assert.Equal(1, messages[0].Id);
    }

    [Fact]
    public async Task RepliesWithoutSequenceLeaveNoMarker()
    {
        await using var client = await ConnectedAsync(subscribeSid: 7);

        await client.SubscribeAsync("ticker", cancellationToken: TestContext.Current.CancellationToken);
        _connector.Connection.PushClose();

        var (messages, _) = await DrainAsync(client);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task UnparseableMessagesAreDroppedAndLoggedWhileTheStreamContinues()
    {
        await using var client = await ConnectedAsync();
        _connector.Connection.Push("not json at all");
        _connector.Connection.Push("""{"no_type":true}""");
        _connector.Connection.Push(Tick);
        _connector.Connection.PushClose();

        var (messages, _) = await DrainAsync(client);

        Assert.Single(messages);
        Assert.Equal(2, _log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("unparseable", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ALongUnparseableMessageIsTruncatedInTheLog()
    {
        await using var client = await ConnectedAsync();
        _connector.Connection.Push(new string('x', 1000));
        _connector.Connection.PushClose();

        await DrainAsync(client);

        var entry = _log.Entries.Single(e => e.Message.Contains("unparseable", StringComparison.Ordinal));
        Assert.True(entry.Message.Length < 300);
    }

    [Fact]
    public async Task AnUnmatchedReplyIsLoggedAtDebugButAnUnmatchedErrorIsPassedOn()
    {
        await using var client = await ConnectedAsync();
        _connector.Connection.Push("""{"id":99,"type":"ok","msg":{}}""");
        _connector.Connection.Push("""{"id":98,"type":"error","msg":{"code":1,"msg":"x"}}""");
        _connector.Connection.PushClose();

        var (messages, _) = await DrainAsync(client);

        Assert.Equal(["error"], messages.Select(m => m.Type));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("unmatched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALostLinkDeliversTheQueuedMessagesThenThrows()
    {
        await using var client = await ConnectedAsync();
        _connector.Connection.Push(Tick);
        _connector.Connection.PushClose();

        var (messages, end) = await DrainAsync(client);

        Assert.Single(messages);
        Assert.Equal("connection closed", Assert.IsType<KalshiWsException>(end).Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task AConnectionFailureSurfacesItsMessageToTheConsumer()
    {
        await using var client = await ConnectedAsync();
        _connector.Connection.PushFailure(new KalshiWsException("a message is larger than 10 bytes"));

        var (messages, end) = await DrainAsync(client);

        Assert.Empty(messages);
        Assert.Equal("a message is larger than 10 bytes", Assert.IsType<KalshiWsException>(end).Message);
    }

    [Fact]
    public async Task AFullQueueClosesTheConnectionAndEndsTheStreamWithAnError()
    {
        await using var client = await ConnectedAsync(queue: 2);
        _connector.Connection.Push(Tick);
        _connector.Connection.Push(Tick);
        _connector.Connection.Push(Tick);
        await WaitUntilAsync(() => _connector.Connection.CloseCalled); // the third message did not fit, so the client closed

        var (messages, end) = await DrainAsync(client);

        Assert.Equal(2, messages.Count);
        Assert.Equal("receive queue overflow", Assert.IsType<KalshiWsException>(end).Message);
        Assert.True(_connector.Connection.CloseCalled);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ClosingEndsTheStreamCleanlyAndIsRepeatable()
    {
        var client = await ConnectedAsync();
        _connector.Connection.Push(Tick);
        var reader = DrainAsync(client);

        await client.CloseAsync();
        await client.CloseAsync();
        var (messages, end) = await reader;

        Assert.Null(end);
        Assert.True(_connector.Connection.CloseCalled);
        Assert.False(client.IsConnected);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ClosingAClientThatNeverConnectedIsHarmless()
    {
        var client = Client();

        await client.CloseAsync();
        await client.DisposeAsync();

        var (messages, end) = await DrainAsync(client);
        Assert.Empty(messages);
        Assert.Null(end);
    }

    // ---- the public surface ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheClientsPublicSurfaceIsExactlyTheApprovedSubscriptionMethods()
    {
        // An allow-list, as for the REST client: the only commands are subscribe, unsubscribe and update_subscription.
        var names = typeof(KalshiWebSocket)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            ["CloseAsync", "ConnectAsync", "DisposeAsync", "Messages", "SubscribeAsync", "UnsubscribeAsync", "UpdateSubscriptionAsync", "get_IsConnected", "get_Subscriptions"],
            names);
    }

    [Fact]
    public void KalshiWsExceptionsHaveTheStandardShapes()
    {
        Assert.Equal("[3] x", new KalshiWsException("x", 3).Message);
        Assert.Equal("x", new KalshiWsException("x", (int?)null).Message);
        Assert.Null(new KalshiWsException().Code);
        Assert.Same(KalshiWsExceptionInner, new KalshiWsException("x", KalshiWsExceptionInner).InnerException);
    }

    private static readonly InvalidOperationException KalshiWsExceptionInner = new("inner");

    private sealed class SubscriptionComparer : IEqualityComparer<WsSubscription>
    {
        public bool Equals(WsSubscription? x, WsSubscription? y) =>
            x is not null && y is not null && x.Sid == y.Sid && x.Channel == y.Channel
            && x.MarketTickers.SequenceEqual(y.MarketTickers) && x.ParametersJson == y.ParametersJson;

        public int GetHashCode(WsSubscription obj) => obj.Sid;
    }
}
