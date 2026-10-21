using System.Net;
using System.Text;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Rest;
using NSec.Cryptography;
using static KalTerm.Kalshi.Tests.Rest.RestTestKit;

namespace KalTerm.Kalshi.Tests.Rest;

/// <summary>The remaining read endpoints; the reference's REST and multivariate tests, ported, plus the extras.</summary>
public class KalshiRestEndpointTests
{
    private const string MveMarket = """
        {"ticker":"KXMVECROSSCATEGORY-S20264B622355CC-ABC","event_ticker":"KXMVECROSSCATEGORY-S20264B622355CC",
         "market_type":"binary","status":"active","mve_collection_ticker":"KXMVECROSSCATEGORY-R",
         "mve_selected_legs":[{"event_ticker":"E","market_ticker":"M","side":"yes"}],"is_provisional":false,"exchange_index":1}
        """;

    private static string Event(string ticker, string? nestedMarkets = null) =>
        "{\"event_ticker\":\"" + ticker + "\",\"series_ticker\":\"KXMVECROSSCATEGORY-SHARD1\",\"title\":\"Arsenal vs Coventry\"," +
        "\"sub_title\":\"\",\"collateral_return_type\":\"\",\"mutually_exclusive\":false" +
        (nestedMarkets is null ? string.Empty : ",\"markets\":[" + nestedMarkets + "]") + "}";

    private static string Trade(string id, string ticker = "A") =>
        "{\"trade_id\":\"" + id + "\",\"ticker\":\"" + ticker + "\",\"yes_price_dollars\":\"0.5900\",\"no_price_dollars\":\"0.4100\"," +
        "\"count_fp\":\"149.83\",\"taker_side\":\"yes\",\"taker_book_side\":\"bid\",\"is_block_trade\":false," +
        "\"created_time\":\"2026-10-07T21:22:01.978691Z\"}";

    [Fact]
    public async Task TradesFollowTheCursorAndKeepExactValues()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, "{\"trades\":[" + Trade("t1") + "," + Trade("t2") + "],\"cursor\":\"c1\"}")
            .Respond(HttpStatusCode.OK, "{\"trades\":[" + Trade("t3", "B") + "],\"cursor\":\"\"}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var found = new List<KalTerm.Kalshi.Models.Trade>();
        var query = KalshiQuery.Empty.With("min_ts", 100).With("max_ts", 200).With("limit", 2);
        await foreach (var trade in client.IterateTradesAsync(query, TestContext.Current.CancellationToken))
        {
            found.Add(trade);
        }

        Assert.Equal(["t1", "t2", "t3"], found.Select(t => t.TradeId));
        Assert.Equal(Contracts.Parse("149.83"), found[0].CountFp);
        Assert.Equal(Dollars.Parse("0.59"), found[0].YesPriceDollars);
        Assert.Equal(978_691, found[0].CreatedTime.Ticks / 10 % 1_000_000);   // microseconds survive
        Assert.Equal(Base + "/markets/trades?min_ts=100&max_ts=200&limit=2", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(Base + "/markets/trades?min_ts=100&max_ts=200&limit=2&cursor=c1", stub.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task EventsAndSeriesList()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, "{\"events\":[{\"event_ticker\":\"EV-1\",\"series_ticker\":\"S\",\"title\":\"t\",\"mutually_exclusive\":true}],\"cursor\":\"\"}")
            .Respond(HttpStatusCode.OK, "{\"series\":[{\"ticker\":\"S\",\"title\":\"t\",\"frequency\":\"daily\",\"category\":\"x\"}]}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var events = new List<string>();
        await foreach (var e in client.IterateEventsAsync(null, TestContext.Current.CancellationToken))
        {
            events.Add(e.EventTicker);
        }

        var series = await client.SeriesListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["EV-1"], events);
        Assert.Equal("S", series[0].Ticker);
        Assert.Equal(Base + "/events", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(Base + "/series", stub.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task SeriesListPassesItsFilters()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{\"series\":[]}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var series = await client.SeriesListAsync(KalshiQuery.Empty.With("category", "Politics"), TestContext.Current.CancellationToken);

        Assert.Empty(series);
        Assert.EndsWith("/series?category=Politics", stub.Requests[0].Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultivariateEventsUseTheirOwnEndpointAndPaginate()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, "{\"events\":[" + Event("E1", MveMarket) + "],\"cursor\":\"c1\"}")
            .Respond(HttpStatusCode.OK, "{\"events\":[" + Event("E2") + "],\"cursor\":\"\"}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var events = new List<KalTerm.Kalshi.Models.KalshiEvent>();
        var query = KalshiQuery.Empty.With("collection_ticker", "KXMVECROSSCATEGORY-R").With("with_nested_markets", true).With("limit", 1);
        await foreach (var e in client.IterateMultivariateEventsAsync(query, TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        Assert.Equal(["E1", "E2"], events.Select(e => e.EventTicker));
        Assert.Equal(
            Base + "/events/multivariate?collection_ticker=KXMVECROSSCATEGORY-R&with_nested_markets=true&limit=1",
            stub.Requests[0].Uri.AbsoluteUri);
        Assert.EndsWith("&cursor=c1", stub.Requests[1].Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.True(events[0].Markets![0].IsMultivariate);
        Assert.All(stub.Requests, r => Assert.StartsWith(Base + "/events/multivariate", r.Uri.AbsoluteUri, StringComparison.Ordinal));   // never the ordinary endpoint
    }

    [Fact]
    public async Task APageOfEachEventKindComesFromItsOwnEndpoint()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, "{\"events\":[],\"cursor\":\"\"}")
            .Respond(HttpStatusCode.OK, "{\"events\":[],\"cursor\":\"\"}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await client.EventsPageAsync(cancellationToken: TestContext.Current.CancellationToken);
        await client.MultivariateEventsPageAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Base + "/events", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(Base + "/events/multivariate", stub.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task AMarketIsUnwrappedFromItsEnvelope()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{\"market\":" + Market("KXFOO-1") + "}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var market = await client.MarketAsync("KXFOO-1", TestContext.Current.CancellationToken);

        Assert.Equal("KXFOO-1", market.Ticker);
        Assert.Equal(Dollars.Parse("0.56"), market.YesBidDollars);
        Assert.Equal(Base + "/markets/KXFOO-1", stub.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task AMarketCarriesItsOwnTitleAndAMarketWithoutOneHasAnEmptyTitle()
    {
        var withTitle = "{\"ticker\":\"KXA-1\",\"event_ticker\":\"EV-1\",\"market_type\":\"binary\",\"status\":\"active\",\"title\":\"Philadelphia wins\",\"yes_sub_title\":\"Philadelphia\"}";
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{\"market\":" + withTitle + "}").Respond(HttpStatusCode.OK, "{\"market\":" + Market("KXB-1") + "}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var titled = await client.MarketAsync("KXA-1", TestContext.Current.CancellationToken);
        var plain = await client.MarketAsync("KXB-1", TestContext.Current.CancellationToken);

        Assert.Equal(("Philadelphia wins", "Philadelphia"), (titled.Title, titled.YesSubTitle));
        Assert.Equal(string.Empty, plain.Title);
    }

    [Fact]
    public async Task AnEventAndASeriesAreUnwrappedFromTheirEnvelopes()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, "{\"event\":" + Event("E1") + "}")
            .Respond(HttpStatusCode.OK, "{\"series\":{\"ticker\":\"S\",\"title\":\"t\",\"frequency\":\"daily\",\"category\":\"x\",\"fee_type\":\"quadratic\",\"fee_multiplier\":0.07}}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var ev = await client.EventAsync("E1", TestContext.Current.CancellationToken);
        var series = await client.SeriesAsync("S", TestContext.Current.CancellationToken);

        Assert.Equal("E1", ev.EventTicker);
        Assert.Equal("quadratic", series.FeeType);
        Assert.Equal(70_000, series.FeeMultiplier!.Value.Micros);
        Assert.Equal(Base + "/events/E1", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(Base + "/series/S", stub.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task AMissingEnvelopeIsAResponseErrorNamingWhatWasAsked()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{\"mkt\":{}}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var error = await Assert.ThrowsAsync<KalshiResponseException>(() => client.MarketAsync("KXFOO-1", TestContext.Current.CancellationToken));

        Assert.StartsWith("market KXFOO-1:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownTickerIsAnApiErrorAndIsNotRetried()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var error = await Assert.ThrowsAsync<KalshiApiException>(() => client.MarketAsync("NOPE", TestContext.Current.CancellationToken));

        Assert.Equal(404, error.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ATickerIsEscapedInTheUrlAndTheSignatureCoversTheEscapedPath()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{\"market\":" + Market("A B") + "}");
        using var key = Ed25519PrivateKey.FromPem(SamplePem());
        using var client = Client(stub, time, signer: new KalshiSigner("kid", key, time));

        await client.MarketAsync("A B", TestContext.Current.CancellationToken);

        Assert.Equal(Base + "/markets/A%20B", stub.Requests[0].Uri.AbsoluteUri);
        var headers = stub.Requests[0].Headers;
        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, key.GetPublicKey(), KeyBlobFormat.RawPublicKey);
        var message = Encoding.UTF8.GetBytes(headers[KalshiSigner.TimestampHeader] + "GET/trade-api/v2/markets/A%20B");
        Assert.True(SignatureAlgorithm.Ed25519.Verify(publicKey, message, Convert.FromBase64String(headers[KalshiSigner.SignatureHeader])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankTickerIsRefusedBeforeAnythingIsSent(string ticker)
    {
        var stub = new StubHttpMessageHandler();
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.MarketAsync(ticker, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.EventAsync(ticker, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SeriesAsync(ticker, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.OrderBookAsync(ticker, 0, TestContext.Current.CancellationToken));

        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task TheOrderBookParsesAndAlwaysSendsTheDepth()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, """{"orderbook_fp":{"yes_dollars":[["0.1500","100.00"]],"no_dollars":[]}}""")
            .Respond(HttpStatusCode.OK, """{"orderbook_fp":{"yes_dollars":[],"no_dollars":[]}}""");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var book = await client.OrderBookAsync("T-1", 5, TestContext.Current.CancellationToken);
        await client.OrderBookAsync("T-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Dollars.Parse("0.15"), book.Yes[0].Price);
        Assert.Equal(Contracts.Parse("100"), book.Yes[0].Quantity);
        Assert.Empty(book.No);
        Assert.Equal(Base + "/markets/T-1/orderbook?depth=5", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(Base + "/markets/T-1/orderbook?depth=0", stub.Requests[1].Uri.AbsoluteUri);   // 0 means every level, and is sent
    }

    [Fact]
    public async Task ANegativeDepthIsRefused()
    {
        using var client = Client(new StubHttpMessageHandler(), new AutoAdvanceTimeProvider());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.OrderBookAsync("T-1", -1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABadOrderBookIsAResponseError()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, """{"orderbook_fp":{"yes_dollars":[["0.1234567","1"]]}}""");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await Assert.ThrowsAsync<KalshiResponseException>(() => client.OrderBookAsync("T-1", 0, TestContext.Current.CancellationToken));
    }
}
