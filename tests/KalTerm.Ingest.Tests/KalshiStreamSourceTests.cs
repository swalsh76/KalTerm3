using System.Text.Json;
using KalTerm.Kalshi;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Tests.WebSocket;
using KalTerm.Kalshi.WebSocket;

namespace KalTerm.Ingest.Tests;

public sealed class KalshiStreamSourceTests : IDisposable
{
    private readonly FakeWsConnector _connector = new();
    private readonly KalshiSigner _signer = new(
        "test-key",
        Ed25519PrivateKey.FromPem(VectorFile.Load("ed25519.json").GetProperty("rawSign")[3].GetProperty("pkcs8Pem").GetString()!));

    public void Dispose() => _signer.Dispose();

    private KalshiStreamSource Source(IReadOnlyList<string>? watchList = null) =>
        new(KalshiSettings.Default, _signer, new KalshiWebSocketOptions { Connector = _connector, AutoReconnect = false }, watchList);

    [Fact]
    public async Task OpeningSubscribesToTheTickerAndTradeChannelsForEveryMarket()
    {
        _connector.OnOpen = (_, connection) => connection.AutoSubscribe(5);

        await using var session = await Source().OpenAsync(TestContext.Current.CancellationToken);

        var commands = _connector.Connection.Sent.Select(text => JsonDocument.Parse(text).RootElement).ToList();
        Assert.Equal(["ticker", "trade"], commands.Select(c => c.GetProperty("params").GetProperty("channels")[0].GetString()));
        Assert.All(commands, c => Assert.False(c.GetProperty("params").TryGetProperty("market_tickers", out _)));
        Assert.True(session.IsConnected);
        Assert.Equal(500_000, session.Stats.QueueLimit);
    }

    [Fact]
    public async Task TheSessionStreamsWhatTheServerSends()
    {
        _connector.OnOpen = (_, connection) => connection.AutoSubscribe(5);
        await using var session = await Source().OpenAsync(TestContext.Current.CancellationToken);

        _connector.Connection.Push("""{"type":"ticker","sid":5,"seq":3,"msg":{"market_ticker":"A-1","price_dollars":"0.5600"}}""");
        await using var messages = session.Messages(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await messages.MoveNextAsync());
        Assert.Equal("ticker", messages.Current.Type);
    }

    [Fact]
    public async Task ADisposedSessionClosesTheConnection()
    {
        _connector.OnOpen = (_, connection) => connection.AutoSubscribe(5);
        var session = await Source().OpenAsync(TestContext.Current.CancellationToken);

        await session.DisposeAsync();

        Assert.True(_connector.Connection.CloseCalled);
    }

    [Fact]
    public async Task AConnectionThatCannotBeOpenedIsReportedAsAFailure()
    {
        _connector.FailWith = new KalshiWsException("refused");

        var failure = await Assert.ThrowsAsync<KalshiWsException>(() => Source().OpenAsync(TestContext.Current.CancellationToken));

        Assert.Equal("refused", failure.Message);
    }

    [Fact]
    public async Task ARefusedSubscriptionFailsTheOpenAndClosesTheConnection()
    {
        _connector.OnOpen = (_, connection) => connection.OnSend = text =>
            connection.Push($$$"""{"id":{{{FakeWsConnection.IdOf(text)}}},"type":"error","msg":{"code":6,"msg":"already subscribed"}}""");

        await Assert.ThrowsAsync<KalshiWsException>(() => Source().OpenAsync(TestContext.Current.CancellationToken));

        Assert.True(_connector.Connection.CloseCalled);
    }

    [Fact]
    public async Task AWatchListAddsAnOrderBookSubscriptionNamingItsMarketsAndTheSessionExposesItsId()
    {
        _connector.OnOpen = (_, connection) => connection.AutoSubscribe(5);

        await using var session = await Source(["A-1", "B-2"]).OpenAsync(TestContext.Current.CancellationToken);

        var commands = _connector.Connection.Sent.Select(text => JsonDocument.Parse(text).RootElement).ToList();
        Assert.Equal(["ticker", "trade", "orderbook_delta"], commands.Select(c => c.GetProperty("params").GetProperty("channels")[0].GetString()));
        Assert.Equal(["A-1", "B-2"], commands[2].GetProperty("params").GetProperty("market_tickers").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(5, session.OrderBookSid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithNoWatchListThereIsNoOrderBookSubscriptionNeverAnEmptyOneThatKalshiWouldReadAsEveryMarket(bool emptyList)
    {
        _connector.OnOpen = (_, connection) => connection.AutoSubscribe(5);

        await using var session = await Source(emptyList ? [] : null).OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _connector.Connection.Sent.Count);
        Assert.Null(session.OrderBookSid);
    }

    [Fact]
    public async Task ASessionCanAskForAFreshSnapshotOfOneMarket()
    {
        _connector.OnOpen = (_, connection) => connection.AutoSubscribe(5);
        await using var session = await Source(["A-1"]).OpenAsync(TestContext.Current.CancellationToken);

        var id = await session.RequestSnapshotAsync(5, "A-1", TestContext.Current.CancellationToken);

        var sent = JsonDocument.Parse(_connector.Connection.Sent[^1]).RootElement;
        Assert.Equal(id, sent.GetProperty("id").GetInt32());
        Assert.Equal("get_snapshot", sent.GetProperty("params").GetProperty("action").GetString());
    }
}
