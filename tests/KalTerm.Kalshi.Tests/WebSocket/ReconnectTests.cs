using System.Text.Json;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Tests.Rest;
using KalTerm.Kalshi.WebSocket;
using Microsoft.Extensions.Logging;
using static KalTerm.Kalshi.Tests.WebSocket.WsTestKit;

namespace KalTerm.Kalshi.Tests.WebSocket;

/// <summary>
/// Reconnect and resubscribe, against scripted connections. The first eight mirror the reference's test_ws_reconnect.py;
/// the rest cover what the port adds (typed event, fast failure while reconnecting, bounded flush).
/// </summary>
public sealed class ReconnectTests : IDisposable
{
    private readonly FakeWsConnector _connector = new();
    private readonly AutoAdvanceTimeProvider _time = new(autoFire: false);
    private readonly ListLogger _log = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly Ed25519PrivateKey _key = Ed25519PrivateKey.FromPem(RestTestKit.SamplePem());
    private readonly KalshiSigner _signer;

    public ReconnectTests()
    {
        _signer = new KalshiSigner("test-key", _key, _time);
    }

    public void Dispose()
    {
        _signer.Dispose();
        _key.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private KalshiWebSocket Client(
        double jitter = 1.0,
        TimeSpan? reconnectBase = null,
        TimeSpan? reconnectMax = null,
        int? maxAttempts = null,
        int queue = 500_000,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            KalshiSettings.Default,
            _signer,
            new KalshiWebSocketOptions
            {
                Connector = _connector,
                TimeProvider = _time,
                Logger = _log,
                MaxQueueMessages = queue,
                ReconnectBase = reconnectBase ?? TimeSpan.FromSeconds(1),
                ReconnectMax = reconnectMax ?? TimeSpan.FromSeconds(30),
                MaxReconnectAttempts = maxAttempts,
                JitterSample = () => jitter,
                Delay = delay ?? RecordDelayAsync,
            });

    private async Task RecordDelayAsync(TimeSpan span, CancellationToken token)
    {
        _delays.Add(span);
        await Task.Yield();
    }

    // ---- ports of the reference's reconnect tests -----------------------------------------------------------------------

    [Fact]
    public async Task ReconnectResubscribesWithTheSameParametersAndSignals()
    {
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            Subscribed(c, sent, n == 0 ? 1 : 9);
            c.Push(Trade(n == 0 ? 1 : 9, 1));
            if (n == 0)
            {
                c.PushClose();
            }
        };
        await using var client = Client();
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", ["M"], cancellationToken: Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);

        var first = await NextAsync(stream);
        var eventMessage = await NextAsync(stream);
        var after = await NextAsync(stream);

        Assert.Equal(1, first.Sid);
        Assert.Equal(KalshiWebSocket.ReconnectedType, eventMessage.Type);
        var payload = Assert.IsType<ReconnectedMsg>(eventMessage.Payload());
        Assert.Equal([new ResubscribedEntry("trade", 9, 1)], payload.Resubscribed);
        Assert.Empty(payload.Failed);
        Assert.Equal("connection_lost", payload.Reason);
        Assert.Equal(0, payload.Dropped);
        Assert.Equal(9, after.Sid);
        Assert.Equal("""{"channels":["trade"],"market_tickers":["M"]}""", ParamsOf(_connector.At(1).Sent.Single()));
        var subscription = Assert.Single(client.Subscriptions);
        Assert.Equal((9, "trade"), (subscription.Sid, subscription.Channel));
        Assert.Equal(["M"], subscription.MarketTickers);
    }

    [Fact]
    public async Task BackoffGrowsAndGivesUpAfterTheAttemptLimit()
    {
        _connector.Fail = attempt => attempt >= 1 ? new HttpRequestException("unreachable") : null;
        _connector.OnOpen = (_, c) => c.OnSend = sent =>
        {
            Subscribed(c, sent, 1);
            c.PushClose();
        };
        await using var client = Client(maxAttempts: 4);
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);

        var (messages, end) = await DrainAsync(client);

        Assert.Empty(messages);
        Assert.Equal("reconnect attempts exhausted", Assert.IsType<KalshiWsException>(end).Message);
        Assert.Equal([1, 2, 4, 8], _delays.Select(d => d.TotalSeconds));
        Assert.Equal(1 + 4, _connector.ConnectCount);
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.5, 0.75)]
    [InlineData(1.0, 1.0)]
    public async Task EachWaitIsTheBackoffTimesAJitterBetweenHalfAndOne(double sample, double factor)
    {
        _connector.Fail = attempt => attempt >= 1 ? new HttpRequestException("unreachable") : null;
        _connector.OnOpen = (_, c) => c.OnSend = _ => c.PushClose();
        await using var client = Client(jitter: sample, maxAttempts: 3);
        await client.ConnectAsync(Token);
        _ = client.SubscribeAsync("trade", cancellationToken: Token).ContinueWith(_ => { }, TaskScheduler.Default);

        await DrainAsync(client);

        Assert.Equal([1 * factor, 2 * factor, 4 * factor], _delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public async Task BackoffIsCapped()
    {
        _connector.Fail = attempt => attempt >= 1 ? new HttpRequestException("unreachable") : null;
        _connector.OnOpen = (_, c) => c.OnSend = _ => c.PushClose();
        await using var client = Client(maxAttempts: 8, reconnectMax: TimeSpan.FromSeconds(5));
        await client.ConnectAsync(Token);
        _ = client.SubscribeAsync("trade", cancellationToken: Token).ContinueWith(_ => { }, TaskScheduler.Default);

        await DrainAsync(client);

        Assert.Equal(8, _delays.Count);
        Assert.Equal(5, _delays.Max(d => d.TotalSeconds));
        Assert.Equal([1, 2, 4, 5, 5, 5, 5, 5], _delays.Select(d => d.TotalSeconds));
    }

    [Fact]
    public async Task ItRecoversAfterTransientConnectFailures()
    {
        _connector.Fail = attempt => attempt is 1 or 2 ? new HttpRequestException("unreachable") : null;
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            Subscribed(c, sent, n + 1);
            if (n == 0)
            {
                c.PushClose();
            }
        };
        await using var client = Client();
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);

        var eventMessage = await NextAsync(stream);

        Assert.Equal(KalshiWebSocket.ReconnectedType, eventMessage.Type);
        Assert.Equal(3, _delays.Count); // two failed attempts, then success
        Assert.Equal(2, _log.Entries.Count(e => e.Message.Contains("reconnect attempt", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ClosingDuringBackoffEndsTheStreamCleanly()
    {
        var backingOff = new TaskCompletionSource();
        _connector.Fail = attempt => attempt >= 1 ? new HttpRequestException("unreachable") : null;
        _connector.OnOpen = (_, c) => c.OnSend = _ => c.PushClose();
        var client = Client(delay: async (_, token) =>
        {
            backingOff.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await client.ConnectAsync(Token);
        _ = client.SubscribeAsync("trade", cancellationToken: Token).ContinueWith(_ => { }, TaskScheduler.Default);
        await backingOff.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        await client.CloseAsync();
        var (messages, end) = await DrainAsync(client);

        Assert.Empty(messages);
        Assert.Null(end);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task EveryConnectionIsSignedFreshly()
    {
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            Subscribed(c, sent, n + 1);
            if (n == 0)
            {
                _time.Advance(TimeSpan.FromSeconds(3));
                c.PushClose();
            }
        };
        await using var client = Client();
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);
        await NextAsync(stream);

        var seen = _connector.HeadersSeen;

        Assert.Equal(2, seen.Count);
        Assert.NotEqual(seen[0][KalshiSigner.TimestampHeader], seen[1][KalshiSigner.TimestampHeader]);
        foreach (var headers in seen)
        {
            var timestamp = long.Parse(headers[KalshiSigner.TimestampHeader], CultureInfo.InvariantCulture);
            Assert.Equal(_signer.Sign(timestamp, "GET", "/trade-api/ws/v2"), headers[KalshiSigner.SignatureHeader]);
        }
    }

    [Fact]
    public async Task ARejectedResubscribeIsReportedAndDropped()
    {
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            if (n == 0)
            {
                Subscribed(c, sent, 1);
                c.PushClose();
            }
            else
            {
                Error(c, sent, 11, "Invalid parameter");
            }
        };
        await using var client = Client();
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);

        var payload = Assert.IsType<ReconnectedMsg>((await NextAsync(stream)).Payload());

        Assert.Empty(payload.Resubscribed);
        var failure = Assert.Single(payload.Failed);
        Assert.Equal(("trade", 1), (failure.Channel, failure.OldSid));
        Assert.Contains("Invalid parameter", failure.Error, StringComparison.Ordinal);
        Assert.Empty(client.Subscriptions);
    }

    [Fact]
    public async Task UnsubscribedChannelsAreNotResubscribed()
    {
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            switch (FakeWsConnection.CommandOf(sent))
            {
                case "subscribe" when n == 0:
                    Subscribed(c, sent, FakeWsConnection.IdOf(sent));
                    break;
                case "subscribe":
                    Subscribed(c, sent, 10 + FakeWsConnection.IdOf(sent));
                    break;
                default:
                    c.Push($$$"""{"id":{{{FakeWsConnection.IdOf(sent)}}},"type":"unsubscribed","msg":{}}""");
                    c.PushClose();
                    break;
            }
        };
        await using var client = Client();
        await client.ConnectAsync(Token);
        var first = await client.SubscribeAsync("trade", cancellationToken: Token);
        await client.SubscribeAsync("ticker", cancellationToken: Token);
        await client.UnsubscribeAsync(first, Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);

        var payload = Assert.IsType<ReconnectedMsg>((await NextAsync(stream)).Payload());

        Assert.Equal(["ticker"], payload.Resubscribed.Select(r => r.Channel));
        Assert.Equal(["""{"channels":["ticker"]}"""], _connector.At(1).Sent.Select(ParamsOf));
    }

    [Fact]
    public async Task ADropDuringResubscribeDiscardsHeldDataAndRetries()
    {
        _connector.OnOpen = (n, c) =>
        {
            var seen = 0;
            c.OnSend = sent =>
            {
                seen++;
                switch (n)
                {
                    case 0:
                        Subscribed(c, sent, seen);
                        if (seen == 2)
                        {
                            c.PushClose();
                        }

                        break;
                    case 1 when seen == 1:
                        Subscribed(c, sent, 5);
                        c.Push(Trade(5, 1)); // data from a half-restored connection
                        break;
                    case 1:
                        c.PushClose(); // the second resubscribe is in flight, unanswered, when the link drops
                        break;
                    default:
                        Subscribed(c, sent, 6 + seen);
                        break;
                }
            };
        };
        await using var client = Client();
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await client.SubscribeAsync("ticker", cancellationToken: Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);

        var payload = Assert.IsType<ReconnectedMsg>((await NextAsync(stream)).Payload());
        var subscriptions = client.Subscriptions;
        await client.CloseAsync();
        var (rest, _) = await DrainAsync(client);

        Assert.Equal([("trade", 7), ("ticker", 8)], payload.Resubscribed.Select(r => (r.Channel, r.Sid)));
        Assert.Equal([(7, "trade"), (8, "ticker")], subscriptions.Select(s => (s.Sid, s.Channel)));
        Assert.DoesNotContain(rest, m => m.Type == "trade"); // the held trade was discarded
        Assert.Equal(3, _connector.ConnectCount);
    }

    // ---- what the port adds ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DataHeldDuringResubscribeIsReleasedAfterTheReconnectedMessageInOrder()
    {
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            Subscribed(c, sent, n == 0 ? 1 : 9);
            if (n == 0)
            {
                c.PushClose();
            }
            else
            {
                c.Push(Trade(9, 1));
                c.Push(Trade(9, 2));
            }
        };
        await using var client = Client();
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);

        var types = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            types.Add((await NextAsync(stream)).Type);
        }

        Assert.Equal([KalshiWebSocket.ReconnectedType, "trade", "trade"], types);
    }

    [Fact]
    public async Task CommandsWhileReconnectingFailFastAndTheClientIsNotConnected()
    {
        var backingOff = new TaskCompletionSource();
        _connector.Fail = attempt => attempt >= 1 ? new HttpRequestException("unreachable") : null;
        _connector.OnOpen = (_, c) => c.OnSend = _ => c.PushClose();
        await using var client = Client(delay: async (_, token) =>
        {
            backingOff.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await client.ConnectAsync(Token);
        _ = client.SubscribeAsync("trade", cancellationToken: Token).ContinueWith(_ => { }, TaskScheduler.Default);
        await backingOff.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        var error = await Assert.ThrowsAsync<KalshiWsException>(() => client.SubscribeAsync("ticker", cancellationToken: Token));

        Assert.Equal("not connected", error.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task AnUpdatedMarketSetIsWhatGetsResubscribed()
    {
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            switch (FakeWsConnection.CommandOf(sent))
            {
                case "subscribe":
                    Subscribed(c, sent, n + 1);
                    break;
                default:
                    c.Push($$$"""{"id":{{{FakeWsConnection.IdOf(sent)}}},"type":"ok","msg":{"market_tickers":["A","B"]}}""");
                    c.PushClose();
                    break;
            }
        };
        await using var client = Client();
        await client.ConnectAsync(Token);
        var sid = await client.SubscribeAsync("ticker", ["A"], cancellationToken: Token);
        await client.UpdateSubscriptionAsync(sid, "add_markets", ["B"], Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);

        await NextAsync(stream);

        Assert.Equal("""{"channels":["ticker"],"market_tickers":["A","B"]}""", ParamsOf(_connector.At(1).Sent.Single()));
    }

    [Fact]
    public async Task AnUnexpectedErrorInTheSupervisorEndsTheStreamWithItsMessage()
    {
        _connector.Fail = attempt => attempt >= 1 ? new InvalidOperationException("a bug, not a network problem") : null;
        _connector.OnOpen = (_, c) => c.OnSend = _ => c.PushClose();
        await using var client = Client();
        await client.ConnectAsync(Token);
        _ = client.SubscribeAsync("trade", cancellationToken: Token).ContinueWith(_ => { }, TaskScheduler.Default);

        var (_, end) = await DrainAsync(client);

        Assert.Equal("a bug, not a network problem", Assert.IsType<KalshiWsException>(end).Message);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ATimedOutResubscribeIsTreatedAsALinkProblemAndRetried()
    {
        var timeouts = new TaskCompletionSource();
        _connector.OnOpen = (n, c) => c.OnSend = sent =>
        {
            if (n == 0)
            {
                Subscribed(c, sent, 1);
                c.PushClose();
            }
            else if (n == 1)
            {
                timeouts.TrySetResult(); // never answered
            }
            else
            {
                Subscribed(c, sent, 3);
            }
        };
        await using var client = Client();
        await client.ConnectAsync(Token);
        await client.SubscribeAsync("trade", cancellationToken: Token);
        await using var stream = client.Messages(Token).GetAsyncEnumerator(Token);
        await timeouts.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        while (_time.Delays.Count(d => d == TimeSpan.FromSeconds(10)) < 2)
        {
            await Task.Delay(1, Token); // the first subscribe's timer and the unanswered resubscribe's
        }

        while (_time.PendingTimers > 0)
        {
            _time.FireNext(); // the finished command's timer is inert; the last one times the resubscribe out
        }

        var payload = Assert.IsType<ReconnectedMsg>((await NextAsync(stream)).Payload());
        Assert.Equal(3, payload.Resubscribed.Single().Sid);
        Assert.Equal(3, _connector.ConnectCount);
    }
}
