using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Tests.Rest;
using KalTerm.Kalshi.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetWebSocket = System.Net.WebSockets.WebSocket;

namespace KalTerm.Kalshi.Tests.WebSocket;

/// <summary>
/// A few tests over a real socket: <see cref="ClientWebSocketConnector"/> and the client against an in-process Kestrel server
/// on a loopback port Windows picks. Everything about the protocol is covered by the in-memory tests; these prove the
/// transport behaves: headers arrive, fragments are joined, binary and oversize messages are refused, closes are seen.
/// </summary>
public sealed class RealSocketTests
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>Starts a server whose single WebSocket endpoint runs <paramref name="serve"/> for each connection.</summary>
    private static async Task<(WebApplication App, Uri Url)> StartAsync(Func<HttpContext, NetWebSocket, Task> serve, Func<int, bool>? refuse = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        var requests = 0;
        app.Map("/ws", async context =>
        {
            if (refuse?.Invoke(Interlocked.Increment(ref requests)) == true)
            {
                context.Response.StatusCode = 503;
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await serve(context, socket);
        });

        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return (app, new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws"));
    }

    private static Task SendTextAsync(NetWebSocket socket, string text, bool endOfMessage = true) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage, CancellationToken.None);

    private static async Task<string> ReceiveTextAsync(NetWebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheUpgradeCarriesTheHeadersAndTextRoundTrips()
    {
        var (app, url) = await StartAsync(async (context, socket) =>
        {
            var echoed = $"{context.Request.Headers["KALSHI-ACCESS-KEY"]}|{await ReceiveTextAsync(socket)}";
            await SendTextAsync(socket, echoed);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        await using (app)
        {
            await using var connection = await new ClientWebSocketConnector(1024).ConnectAsync(
                url, new Dictionary<string, string> { ["KALSHI-ACCESS-KEY"] = "abc" }, Token);

            await connection.SendAsync("héllo", Token);

            Assert.Equal("abc|héllo", await connection.ReceiveAsync(Token));
            Assert.Null(await connection.ReceiveAsync(Token));
        }
    }

    [Fact]
    public async Task AMessageSentInFragmentsIsJoined()
    {
        var (app, url) = await StartAsync(async (_, socket) =>
        {
            await SendTextAsync(socket, "one-", endOfMessage: false);
            await SendTextAsync(socket, "two-", endOfMessage: false);
            await SendTextAsync(socket, "three");
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        await using (app)
        {
            await using var connection = await new ClientWebSocketConnector(1024).ConnectAsync(url, NoHeaders, Token);

            Assert.Equal("one-two-three", await connection.ReceiveAsync(Token));
        }
    }

    [Fact]
    public async Task ABinaryMessageIsRefused()
    {
        var (app, url) = await StartAsync(async (_, socket) =>
        {
            await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
            await ReceiveTextAsync(socket);
        });
        await using (app)
        {
            await using var connection = await new ClientWebSocketConnector(1024).ConnectAsync(url, NoHeaders, Token);

            var error = await Assert.ThrowsAsync<KalshiWsException>(() => connection.ReceiveAsync(Token));

            Assert.Contains("binary", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnOversizeMessageIsRefused()
    {
        var (app, url) = await StartAsync(async (_, socket) =>
        {
            await SendTextAsync(socket, new string('x', 5000));
            await ReceiveTextAsync(socket);
        });
        await using (app)
        {
            await using var connection = await new ClientWebSocketConnector(1000).ConnectAsync(url, NoHeaders, Token);

            var error = await Assert.ThrowsAsync<KalshiWsException>(() => connection.ReceiveAsync(Token));

            Assert.Contains("1000", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ADroppedLinkWithoutACloseHandshakeLooksLikeAClose()
    {
        var (app, url) = await StartAsync(async (context, socket) =>
        {
            await ReceiveTextAsync(socket); // abort only once the client is surely connected
            context.Abort();
        });
        await using (app)
        {
            await using var connection = await new ClientWebSocketConnector(1024).ConnectAsync(url, NoHeaders, Token);
            await connection.SendAsync("go", Token);

            Assert.Null(await connection.ReceiveAsync(Token));
        }
    }

    [Fact]
    public async Task SendingAfterTheConnectionIsClosedIsAnErrorNotAHang()
    {
        var (app, url) = await StartAsync(async (_, socket) =>
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        await using (app)
        {
            var connection = await new ClientWebSocketConnector(1024).ConnectAsync(url, NoHeaders, Token);
            Assert.Null(await connection.ReceiveAsync(Token));
            await connection.CloseAsync();
            await connection.DisposeAsync();

            var error = await Assert.ThrowsAsync<KalshiWsException>(() => connection.SendAsync("late", Token));

            Assert.Equal("connection closed", error.Message);
        }
    }

    [Fact]
    public async Task ConnectingToAServerThatRefusesTheUpgradeFails()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/ws", () => Results.StatusCode(401));
        await app.StartAsync(Token);
        await using (app)
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var url = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws");

            await Assert.ThrowsAsync<WebSocketException>(() => new ClientWebSocketConnector(1024).ConnectAsync(url, NoHeaders, Token));
        }
    }

    [Fact]
    public async Task TheClientSubscribesAndStreamsOverARealSocket()
    {
        var (app, url) = await StartAsync(async (context, socket) =>
        {
            var command = JsonDocument.Parse(await ReceiveTextAsync(socket)).RootElement;
            var id = command.GetProperty("id").GetInt32();
            await SendTextAsync(socket, $$$"""{"id":{{{id}}},"type":"subscribed","msg":{"channel":"ticker","sid":5}}""");
            await SendTextAsync(socket, $$$"""{"type":"ticker","sid":5,"seq":1,"msg":{"market_ticker":"{{{context.Request.Headers["KALSHI-ACCESS-KEY"]}}}","price_dollars":"0.5600"}}""");
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        await using (app)
        {
            using var key = Ed25519PrivateKey.FromPem(RestTestKit.SamplePem());
            using var signer = new KalshiSigner("real-key", key);
            await using var client = new KalshiWebSocket(KalshiSettings.Default, signer, new KalshiWebSocketOptions { Url = url, AutoReconnect = false });

            await client.ConnectAsync(Token);
            var sid = await client.SubscribeAsync("ticker", ["A"], cancellationToken: Token);
            var received = new List<WsMessage>();
            Exception? end = null;
            try
            {
                await foreach (var message in client.Messages(Token))
                {
                    received.Add(message);
                }
            }
            catch (KalshiWsException ex)
            {
                end = ex;
            }

            Assert.Equal(5, sid);
            var ticker = Assert.IsType<TickerMsg>(Assert.Single(received).Payload());
            Assert.Equal("real-key", ticker.MarketTicker);
            Assert.NotNull(end);
        }
    }

    [Fact]
    public async Task AClientReconnectsOverARealSocketAndRestoresItsSubscription()
    {
        var connections = 0;
        var (app, url) = await StartAsync(async (_, socket) =>
        {
            var mine = Interlocked.Increment(ref connections);
            var command = JsonDocument.Parse(await ReceiveTextAsync(socket)).RootElement;
            var id = command.GetProperty("id").GetInt32();
            await SendTextAsync(socket, $$$"""{"id":{{{id}}},"type":"subscribed","msg":{"channel":"ticker","sid":{{{mine}}}}}""");
            if (mine == 1)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }

            await SendTextAsync(socket, $$$"""{"type":"ticker","sid":{{{mine}}},"seq":1,"msg":{"market_ticker":"A","price_dollars":"0.5600"}}""");
            await ReceiveTextAsync(socket); // hold the connection open until the client closes it
        });
        await using (app)
        {
            await using var client = Client(url);
            await client.ConnectAsync(Token);
            await client.SubscribeAsync("ticker", ["A"], cancellationToken: Token);

            var stream = client.Messages(Token).GetAsyncEnumerator(Token);
            Assert.True(await stream.MoveNextAsync());
            var reconnected = Assert.IsType<ReconnectedMsg>(stream.Current.Payload());
            Assert.True(await stream.MoveNextAsync());

            Assert.Equal([new ResubscribedEntry("ticker", 2, 1)], reconnected.Resubscribed);
            Assert.IsType<TickerMsg>(stream.Current.Payload());
            Assert.Equal(2, client.Subscriptions.Single().Sid);
        }
    }

    [Fact]
    public async Task ARefusedReconnectUpgradeIsRetried()
    {
        var connections = 0;
        var (app, url) = await StartAsync(
            async (_, socket) =>
            {
                var mine = Interlocked.Increment(ref connections);
                var command = JsonDocument.Parse(await ReceiveTextAsync(socket)).RootElement;
                var id = command.GetProperty("id").GetInt32();
                await SendTextAsync(socket, $$$"""{"id":{{{id}}},"type":"subscribed","msg":{"channel":"ticker","sid":{{{mine}}}}}""");
                if (mine == 1)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                await ReceiveTextAsync(socket);
            },
            refuse: request => request is 2 or 3);
        await using (app)
        {
            var log = new ListLogger();
            await using var client = Client(url, log);
            await client.ConnectAsync(Token);
            await client.SubscribeAsync("ticker", cancellationToken: Token);

            var stream = client.Messages(Token).GetAsyncEnumerator(Token);
            Assert.True(await stream.MoveNextAsync());

            Assert.Equal(KalshiWebSocket.ReconnectedType, stream.Current.Type);
            Assert.Equal(2, log.Entries.Count(e => e.Message.Contains("reconnect attempt", StringComparison.Ordinal)));
        }
    }

    private static KalshiWebSocket Client(Uri url, ILogger? logger = null)
    {
        var key = Ed25519PrivateKey.FromPem(RestTestKit.SamplePem());
        var signer = new KalshiSigner("real-key", key);
        return new KalshiWebSocket(
            KalshiSettings.Default,
            signer,
            new KalshiWebSocketOptions
            {
                Url = url,
                Logger = logger,
                ReconnectBase = TimeSpan.FromMilliseconds(10),
                ReconnectMax = TimeSpan.FromMilliseconds(40),
            });
    }
}
