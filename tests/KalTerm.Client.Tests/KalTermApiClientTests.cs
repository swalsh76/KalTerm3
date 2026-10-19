using System.Net;
using KalTerm.Contracts;

namespace KalTerm.Client.Tests;

public sealed class KalTermApiClientTests
{
    private static readonly Uri Base = new("http://127.0.0.1:5280");
    private static readonly DateTime T = new(2026, 10, 18, 22, 30, 15, 250, DateTimeKind.Utc);

    private static MarketDto Market(string ticker) => new(ticker, "EV", "binary", "active", "Yes", "No", null, null, null, string.Empty, null, null, "2026-10-18T00:00:00.000000+00:00", "2026-10-18T00:00:00.000000+00:00");

    private static string Json<T>(T value) => ApiJson.Serialize(value);

    private static KalTermApiClient Client(StubHttpMessageHandler stub, TimeSpan? timeout = null) => KalTermApiClient.Create(Base, stub, timeout);

    // ---- what it sends --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StatusIsOneGetOfTheStatusRoute()
    {
        var status = new StatusDto("2026-10-18T22:30:15.000000+00:00", [], new IngestStateDto("running", null, 3), new StreamsDto(new StreamDto(null, null), new StreamDto(null, null)), new StorageDto(1, null, null), new Dictionary<string, long>(), new GapSummaryDto(24, 0, new Dictionary<string, long>(), "note"), new BooksStatusDto(0, 0, 0, null));
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, Json(status));
        using var client = Client(stub);

        var got = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(stub.Requests);
        Assert.Equal((HttpMethod.Get, "http://127.0.0.1:5280/v1/status"), (request.Method, request.Uri.AbsoluteUri));
        Assert.Equal("running", got.Ingest.State);
        Assert.Contains("KalTerm-Client", request.Headers["User-Agent"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarketsSendOnlyTheParametersGivenInAFixedOrderAndEscapeThem()
    {
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, Json(new MarketPage([Market("A")], "QQ")))
            .Respond(HttpStatusCode.OK, Json(new MarketPage([], null)));
        using var client = Client(stub);

        var all = await client.GetMarketsAsync(new MarketsQuery("closed", "EV-1", "n y&x", 5, "Q Q"), TestContext.Current.CancellationToken);
        await client.GetMarketsAsync(new MarketsQuery(), TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:5280/v1/markets?status=closed&event=EV-1&q=n%20y%26x&limit=5&cursor=Q%20Q", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:5280/v1/markets", stub.Requests[1].Uri.AbsoluteUri);
        Assert.Equal(("A", "QQ"), (all.Markets[0].Ticker, all.NextCursor));
    }

    [Fact]
    public async Task OneMarketHasItsTickerEscapedAsAPathSegment()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, Json(Market("A B/C")));
        using var client = Client(stub);

        var got = await client.GetMarketAsync("A B/C", TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:5280/v1/markets/A%20B%2FC", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("A B/C", got.Ticker);
    }

    [Fact]
    public async Task TicksAndTradesSendTheWindowAsUtcWithAnOffsetThatIsEscaped()
    {
        var tick = new TickDto("2026-10-18T22:30:15.250000+00:00", "0.560000", null, null, null, null, null, null, null);
        var trade = new TradeDto("2026-10-18T22:30:15.250000+00:00", "t-1", "0.400000", "0.600000", "3.00", "yes", false);
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, Json(new TickPage([tick], "Q1")))
            .Respond(HttpStatusCode.OK, Json(new TradePage([trade], null)));
        using var client = Client(stub);

        var ticks = await client.GetTicksAsync("KX-1", new WindowQuery(T, T.AddHours(1), 50, "Q0"), TestContext.Current.CancellationToken);
        var trades = await client.GetTradesAsync("KX-1", new WindowQuery(Limit: 3), TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:5280/v1/markets/KX-1/ticks?start=2026-10-18T22%3A30%3A15.250000%2B00%3A00&end=2026-10-18T23%3A30%3A15.250000%2B00%3A00&limit=50&cursor=Q0", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:5280/v1/markets/KX-1/trades?limit=3", stub.Requests[1].Uri.AbsoluteUri);
        Assert.Equal(("0.560000", "Q1"), (ticks.Ticks[0].Price, ticks.NextCursor));
        Assert.Equal("t-1", trades.Trades[0].TradeId);
    }

    [Fact]
    public async Task GapsSendTheWindowAndTheLimitButNeverACursor()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, Json(new GapPage([new GapDto(1, "ingest_down", "2026-10-18T22:00:00.000000+00:00", null, "x")])));
        using var client = Client(stub);

        var gaps = await client.GetGapsAsync(new WindowQuery(T, null, 10, "ignored"), TestContext.Current.CancellationToken);

        Assert.Equal("http://127.0.0.1:5280/v1/gaps?start=2026-10-18T22%3A30%3A15.250000%2B00%3A00&limit=10", stub.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("ingest_down", gaps.Gaps[0].Kind);
    }

    // ---- what goes wrong ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARefusalCarriesTheApisOwnCodeAndTheFieldsItNamed()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.UnprocessableEntity, Json(new ErrorDto("invalid_parameter", ["limit", "cursor"])));
        using var client = Client(stub);

        var failure = await Assert.ThrowsAsync<ApiException>(() => client.GetMarketsAsync(new MarketsQuery(Limit: 0), TestContext.Current.CancellationToken));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid_parameter"), (failure.Status, failure.Code));
        Assert.Equal(["limit", "cursor"], failure.Fields);
        Assert.Contains("limit, cursor", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "unavailable")]
    [InlineData(HttpStatusCode.InternalServerError, "internal_error")]
    public async Task EveryErrorCodeComesThroughUnchanged(HttpStatusCode status, string code)
    {
        var stub = new StubHttpMessageHandler().Respond(status, Json(new ErrorDto(code)));
        using var client = Client(stub);

        var failure = await Assert.ThrowsAsync<ApiException>(() => client.GetMarketAsync("KX-1", TestContext.Current.CancellationToken));

        Assert.Equal(code, failure.Code);
        Assert.Equal(status, failure.Status);
        Assert.Empty(failure.Fields);
    }

    [Theory]
    [InlineData("<html>bad gateway</html>")]
    [InlineData("")]
    [InlineData("{\"something\":\"else\"}")]
    [InlineData("null")]
    public async Task AnErrorBodyThatIsNotTheApisOwnIsReportedByStatusAlone(string body)
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.BadGateway, body);
        using var client = Client(stub);

        var failure = await Assert.ThrowsAsync<ApiException>(() => client.GetStatusAsync(TestContext.Current.CancellationToken));

        Assert.Equal(("http_error", HttpStatusCode.BadGateway), (failure.Code, failure.Status));
    }

    [Theory]
    [InlineData("<html>not json</html>")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    public async Task ASuccessWithoutTheExpectedJsonIsABadResponse(string body)
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, body);
        using var client = Client(stub);

        var failure = await Assert.ThrowsAsync<ApiException>(() => client.GetStatusAsync(TestContext.Current.CancellationToken));

        Assert.Equal("bad_response", failure.Code);
    }

    [Fact]
    public async Task AConnectionThatFailsNamesTheAddress()
    {
        var stub = new StubHttpMessageHandler().Throw(new HttpRequestException("connection refused"));
        using var client = Client(stub);

        var failure = await Assert.ThrowsAsync<ApiUnavailableException>(() => client.GetStatusAsync(TestContext.Current.CancellationToken));

        Assert.Contains("http://127.0.0.1:5280", failure.Message, StringComparison.Ordinal);
        Assert.Contains("connection refused", failure.Message, StringComparison.Ordinal);
        Assert.IsType<HttpRequestException>(failure.InnerException);
    }

    [Fact]
    public async Task ATimeoutIsUnavailableButACancelledCallStaysCancelled()
    {
        var stub = new StubHttpMessageHandler().Throw(new TaskCanceledException("timed out")).Throw(new TaskCanceledException("cancelled"));
        using var client = Client(stub);
        using var cts = new CancellationTokenSource();

        var timedOut = await Assert.ThrowsAsync<ApiUnavailableException>(() => client.GetStatusAsync(TestContext.Current.CancellationToken));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStatusAsync(cts.Token));

        Assert.Contains("did not answer in time", timedOut.Message, StringComparison.Ordinal);
    }

    // ---- construction ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABorrowedHttpClientIsNotDisposedWithTheClient()
    {
        using var http = new HttpClient(new StubHttpMessageHandler().Respond(HttpStatusCode.OK, Json(new HealthDto("ok")))) { BaseAddress = Base };
        var client = new KalTermApiClient(http);

        client.Dispose();

        using var response = await http.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task BadArgumentsAreRefused()
    {
        using var stub = new HttpClient();
        Assert.Throws<ArgumentNullException>(() => new KalTermApiClient(null!));
        Assert.Throws<ArgumentException>(() => new KalTermApiClient(stub));
        Assert.Throws<ArgumentNullException>(() => KalTermApiClient.Create(null!));
        using var client = Client(new StubHttpMessageHandler());
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetMarketsAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetTicksAsync("A", null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetTradesAsync("A", null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetGapsAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetMarketAsync(" ", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheExceptionsHaveTheStandardConstructors()
    {
        var inner = new InvalidOperationException("x");

        Assert.Equal("http_error", new ApiException().Code);
        Assert.Equal("m", new ApiException("m").Message);
        Assert.Same(inner, new ApiException("m", inner).InnerException);
        Assert.Equal("the API refused the request (404 not_found)", new ApiException(HttpStatusCode.NotFound, "not_found").Message);
        Assert.Equal("the API could not be reached", new ApiUnavailableException().Message);
        Assert.Equal("m", new ApiUnavailableException("m").Message);
        Assert.Same(inner, new ApiUnavailableException("m", inner).InnerException);
    }

    [Fact]
    public async Task OrderBooksSendTheDepthOnlyWhenGivenAndEscapeTheTicker()
    {
        var book = new OrderBookDto("A/B", "2026-10-19T12:00:00.000000+00:00", "2026-10-19T12:00:00.000000+00:00", 0, false, false, [], []);
        var stub = new StubHttpMessageHandler()
            .Respond(HttpStatusCode.OK, Json(new OrderBookPage([book])))
            .Respond(HttpStatusCode.OK, Json(new OrderBookPage([])))
            .Respond(HttpStatusCode.OK, Json(book));
        using var client = Client(stub);

        var all = await client.GetOrderBooksAsync(cancellationToken: TestContext.Current.CancellationToken);
        await client.GetOrderBooksAsync(25, TestContext.Current.CancellationToken);
        var one = await client.GetOrderBookAsync("A/B", 3, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["http://127.0.0.1:5280/v1/orderbooks", "http://127.0.0.1:5280/v1/orderbooks?depth=25", "http://127.0.0.1:5280/v1/markets/A%2FB/orderbook?depth=3"],
            stub.Requests.Select(r => r.Uri.AbsoluteUri));
        Assert.All(stub.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Single(all.Books);
        Assert.Equal("A/B", one.Ticker);
    }
}
