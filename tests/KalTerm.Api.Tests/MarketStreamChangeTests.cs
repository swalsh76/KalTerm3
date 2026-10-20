using System.Net;
using KalTerm.Contracts;
using KalTerm.Data.Reading;
using static KalTerm.Api.Tests.Samples;

namespace KalTerm.Api.Tests;

/// <summary>Changing what an open stream watches: who may, how it is counted, where a new market's ticks begin, and what the stream is told.</summary>
public sealed class MarketStreamChangeHubTests
{
    private readonly FakeStreamReader _reader = new();
    private readonly FakeBooks _books = new();
    private readonly AutoAdvanceTimeProvider _time = new(autoFire: false, utcStart: Now);

    private MarketStreamHub Hub(MarketStreamOptions? options = null) => new(_reader, _books, _time, options ?? new MarketStreamOptions { MaxPerUser = 2, MaxTotal = 3, MaxMarkets = 3 });

    private static HashSet<string> Set(params string[] tickers) => new(tickers, StringComparer.Ordinal);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<StreamItem> Drain(MarketStreamSession session)
    {
        var items = new List<StreamItem>();
        while (session.Reader.TryRead(out var item))
        {
            items.Add(item);
        }

        return items;
    }

    private static StreamTick Row(string ticker, DateTime ts) => new(ticker, Tick(ts));

    [Fact]
    public async Task ANewTicksMarketBeginsWhereTheLastPollEndedAndNothingBeforeItIsSent()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;
        _time.Advance(TimeSpan.FromSeconds(5));
        await hub.PollAsync(Ct);
        Drain(session);
        _time.Advance(TimeSpan.FromSeconds(1));

        var outcome = await hub.ChangeAsync(1, session.Id, books: false, "KX-2", add: true, Ct);
        _reader.Rows.AddRange([Row("KX-2", NowUtc.AddSeconds(2.9)), Row("KX-2", NowUtc.AddSeconds(3.5))]);
        _time.Advance(TimeSpan.FromSeconds(3));
        await hub.PollAsync(Ct);
        var items = Drain(session);

        Assert.Equal(StreamChange.Done, outcome);
        var confirmation = Assert.IsType<SubscriptionItem>(items[0]);
        Assert.Equal(new SubscriptionItem(true, false, "KX-2", NowUtc.AddSeconds(3), false, false), confirmation);   // the end of the last poll
        Assert.Equal([NowUtc.AddSeconds(3.5)], items.Skip(1).Cast<TickItem>().Select(t => t.Tick.Ts));   // the tick at 2.9 is before it: the client's to load over REST
        Assert.Equal(["KX-1", "KX-2"], session.Ticks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AddingAMarketAlreadyWatchedChangesNothingAndHasNoStartingPoint()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;

        var outcome = await hub.ChangeAsync(1, session.Id, books: false, "KX-1", add: true, Ct);

        Assert.Equal(StreamChange.Done, outcome);
        Assert.Equal(new SubscriptionItem(true, false, "KX-1", null, false, false), Assert.Single(Drain(session)));
        Assert.Single(session.Ticks);
    }

    [Fact]
    public async Task ATickerThatIsNotAMarketIsWatchedAnywayAndSaysSo()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;

        await hub.ChangeAsync(1, session.Id, books: false, "KX-9", add: true, Ct);

        var item = Assert.IsType<SubscriptionItem>(Assert.Single(Drain(session)));
        Assert.True(item.Unknown);
        Assert.Contains("KX-9", session.Ticks);   // it is looked for again later, as at the start
    }

    [Fact]
    public async Task AddingABookSendsTheCurrentBookRightAfterTheConfirmation()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;
        _books.Books.Add(Book("KX-2"));

        await hub.ChangeAsync(1, session.Id, books: true, "KX-2", add: true, Ct);

        var items = Drain(session);
        Assert.Equal(new SubscriptionItem(true, true, "KX-2", null, false, false), items[0]);
        Assert.Equal("KX-2", Assert.IsType<BookItem>(items[1]).Book.Ticker);
        Assert.Equal(2, items.Count);
        Assert.Contains("KX-2", session.Books);
    }

    [Fact]
    public async Task AMarketWithNoPublishedBookSaysSoAndSendsNone()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;

        await hub.ChangeAsync(1, session.Id, books: true, "KX-3", add: true, Ct);

        var item = Assert.IsType<SubscriptionItem>(Assert.Single(Drain(session)));
        Assert.True(item.NoBook);
        Assert.False(item.Unknown);
    }

    [Fact]
    public async Task ARemovedMarketIsConfirmedAfterWhatWasAlreadyQueuedAndNothingFollowsIt()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1", "KX-2"), Set(), Ct))!;
        _reader.Rows.AddRange([Row("KX-1", NowUtc.AddSeconds(-1)), Row("KX-2", NowUtc.AddSeconds(-0.5))]);
        _time.Advance(TimeSpan.FromSeconds(5));
        await hub.PollAsync(Ct);

        var outcome = await hub.ChangeAsync(1, session.Id, books: false, "KX-1", add: false, Ct);
        _reader.Rows.AddRange([Row("KX-1", NowUtc.AddSeconds(4)), Row("KX-2", NowUtc.AddSeconds(4))]);
        _time.Advance(TimeSpan.FromSeconds(3));
        await hub.PollAsync(Ct);
        var items = Drain(session);

        Assert.Equal(StreamChange.Done, outcome);
        Assert.Equal(["KX-1", "KX-2"], items.Take(2).Cast<TickItem>().Select(t => t.Ticker));
        Assert.Equal(new SubscriptionItem(false, false, "KX-1", null, false, false), items[2]);
        Assert.Equal(["KX-2"], items.Skip(3).Cast<TickItem>().Select(t => t.Ticker));   // nothing for KX-1 after its confirmation
    }

    [Fact]
    public async Task RemovingAMarketThatIsNotWatchedIsConfirmedAllTheSame()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;

        var outcome = await hub.ChangeAsync(1, session.Id, books: true, "KX-2", add: false, Ct);

        Assert.Equal(StreamChange.Done, outcome);
        Assert.Equal(new SubscriptionItem(false, true, "KX-2", null, false, false), Assert.Single(Drain(session)));
    }

    [Fact]
    public async Task AStreamCannotWatchMoreMarketsThanTheLimitAndAMarketWatchedTwiceCountsOnce()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1", "KX-2"), Set("KX-3"), Ct))!;

        var over = await hub.ChangeAsync(1, session.Id, books: false, "KX-4", add: true, Ct);
        var overBooks = await hub.ChangeAsync(1, session.Id, books: true, "KX-4", add: true, Ct);
        var same = await hub.ChangeAsync(1, session.Id, books: false, "KX-3", add: true, Ct);   // already counted through its book
        await hub.ChangeAsync(1, session.Id, books: false, "KX-1", add: false, Ct);
        var room = await hub.ChangeAsync(1, session.Id, books: false, "KX-4", add: true, Ct);

        Assert.Equal((StreamChange.TooManyMarkets, StreamChange.TooManyMarkets, StreamChange.Done, StreamChange.Done), (over, overBooks, same, room));
        Assert.DoesNotContain(Drain(session), i => i is SubscriptionItem { Ticker: "KX-4", Added: true } s && s.Books);
        Assert.Equal(["KX-2", "KX-3", "KX-4"], session.Ticks.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OnlyTheStreamsOwnUserCanChangeItAndAnEndedStreamCannotBeChanged()
    {
        using var hub = Hub();
        var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;

        var stranger = await hub.ChangeAsync(2, session.Id, books: false, "KX-2", add: true, Ct);
        var unknown = await hub.ChangeAsync(1, "nosuchstream", books: false, "KX-2", add: true, Ct);
        session.Dispose();
        var ended = await hub.ChangeAsync(1, session.Id, books: false, "KX-2", add: true, Ct);

        Assert.Equal([StreamChange.Gone, StreamChange.Gone, StreamChange.Gone], [stranger, unknown, ended]);
        Assert.Equal(["KX-1"], session.Ticks);
    }

    [Fact]
    public async Task AFailedLookupChangesNothing()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;
        _reader.Throw = new InvalidOperationException("database down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.ChangeAsync(1, session.Id, books: false, "KX-2", add: true, Ct));

        Assert.Equal(["KX-1"], session.Ticks);
        Assert.Empty(Drain(session));
    }

    [Fact]
    public async Task AnArgumentThatIsMissingIsRefused()
    {
        using var hub = Hub();

        await Assert.ThrowsAsync<ArgumentNullException>(() => hub.ChangeAsync(1, null!, books: false, "KX-1", add: true, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => hub.ChangeAsync(1, "x", books: false, null!, add: true, Ct));
    }
}

/// <summary>Changing what an open stream watches over a test server: the routes, their refusals, and what the stream says back.</summary>
public sealed class MarketStreamChangeEndpointTests : IAsyncLifetime
{
    private ApiTestHost _api = null!;

    private FakeReaders Fakes => _api.Fakes;

    public async ValueTask InitializeAsync() => _api = await ApiTestHost.StartAsUserAsync(42);

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private static async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenAsync(ApiTestHost host, string url, string? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        return (response, new StreamReader(stream));
    }

    private static async Task<List<string>> NextAsync(StreamReader reader)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        while (await reader.ReadLineAsync(guard.Token) is { } line)
        {
            if (line.Length == 0)
            {
                return lines;
            }

            lines.Add(line);
        }

        return lines;
    }

    private static T Data<T>(List<string> lines)
        where T : class => ApiJson.Deserialize<T>(lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..])!;

    private static string At(double secondsFromNow) => NowUtc.AddSeconds(secondsFromNow).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static Task<HttpResponseMessage> SendAsync(ApiTestHost host, HttpMethod method, string url, string? authorization = null)
    {
        var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return host.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<(HttpResponseMessage Response, StreamReader Reader, string Id)> OpenReadyAsync(string url)
    {
        var (response, reader) = await OpenAsync(_api, url);
        return (response, reader, Data<StreamReadyDto>(await NextAsync(reader)).StreamId);
    }

    [Fact]
    public async Task AddingATicksMarketIsAnsweredAtOnceAndConfirmedOnTheStreamWithWhereItsTicksBegin()
    {
        var (response, reader, id) = await OpenReadyAsync("/v1/stream?ticks=KX-1");

        var put = await SendAsync(_api, HttpMethod.Put, $"/v1/stream/{id}/ticks/KX-2");
        var confirmation = await NextAsync(reader);

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("event: subscribed", confirmation[0]);
        Assert.Equal(new StreamSubscriptionDto("ticks", "KX-2", At(-2), false, false), Data<StreamSubscriptionDto>(confirmation));
        Fakes.StreamReader.Rows.Add(new StreamTick("KX-2", Tick(NowUtc.AddSeconds(-1), 575_000)));
        Fakes.StreamTime.Advance(TimeSpan.FromSeconds(5));
        await Fakes.StreamHub.PollAsync(TestContext.Current.CancellationToken);
        var tick = Data<MarketTickDto>(await NextAsync(reader));
        Assert.Equal(("KX-2", "0.575000"), (tick.Ticker, tick.Tick.Price));
        response.Dispose();
    }

    [Fact]
    public async Task AddingABookSendsItAfterTheConfirmationAndRemovingStopsIt()
    {
        Fakes.Books.Books.Add(Book("KX-2"));
        var (response, reader, id) = await OpenReadyAsync("/v1/stream?ticks=KX-1");

        await SendAsync(_api, HttpMethod.Put, $"/v1/stream/{id}/books/KX-2");
        var confirmation = await NextAsync(reader);
        var book = await NextAsync(reader);
        var delete = await SendAsync(_api, HttpMethod.Delete, $"/v1/stream/{id}/books/KX-2");
        var removal = await NextAsync(reader);
        Fakes.Books.Books[0] = Fakes.Books.Books[0] with { UpdatedAt = NowUtc.AddSeconds(5) };
        await Fakes.StreamHub.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("event: subscribed", "event: book"), (confirmation[0], book[0]));
        Assert.Equal("KX-2", Data<OrderBookDto>(book).Ticker);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(("event: unsubscribed", "books"), (removal[0], Data<StreamSubscriptionDto>(removal).Kind));
        Assert.Equal([": keep-alive"], await NextAsync(reader));   // the changed book is not pushed any more
        response.Dispose();
    }

    [Fact]
    public async Task ABookSubscribedAgainIsSentAfresh()
    {
        Fakes.Books.Books.Add(Book("KX-2"));
        var (response, reader, id) = await OpenReadyAsync("/v1/stream?books=KX-2");
        Assert.Equal("event: book", (await NextAsync(reader))[0]);

        await SendAsync(_api, HttpMethod.Delete, $"/v1/stream/{id}/books/KX-2");
        Assert.Equal("event: unsubscribed", (await NextAsync(reader))[0]);
        await SendAsync(_api, HttpMethod.Put, $"/v1/stream/{id}/books/KX-2");

        Assert.Equal("event: subscribed", (await NextAsync(reader))[0]);
        Assert.Equal("event: book", (await NextAsync(reader))[0]);
        response.Dispose();
    }

    [Fact]
    public async Task ARemovedTicksMarketSendsNothingMore()
    {
        var (response, reader, id) = await OpenReadyAsync("/v1/stream?ticks=KX-1,KX-2");

        await SendAsync(_api, HttpMethod.Delete, $"/v1/stream/{id}/ticks/KX-1");
        Assert.Equal("event: unsubscribed", (await NextAsync(reader))[0]);
        Fakes.StreamReader.Rows.AddRange([new StreamTick("KX-1", Tick(NowUtc.AddSeconds(-1))), new StreamTick("KX-2", Tick(NowUtc.AddSeconds(-1)))]);
        Fakes.StreamTime.Advance(TimeSpan.FromSeconds(5));
        await Fakes.StreamHub.PollAsync(TestContext.Current.CancellationToken);

        Assert.Equal("KX-2", Data<MarketTickDto>(await NextAsync(reader)).Ticker);
        Assert.Equal([": keep-alive"], await NextAsync(reader));
        response.Dispose();
    }

    [Fact]
    public async Task AnUnknownTickerIsAcceptedAndTheStreamSaysItIsNotAMarket()
    {
        var (response, reader, id) = await OpenReadyAsync("/v1/stream?ticks=KX-1");

        var put = await SendAsync(_api, HttpMethod.Put, $"/v1/stream/{id}/ticks/KX-9");

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.True(Data<StreamSubscriptionDto>(await NextAsync(reader)).Unknown);
        response.Dispose();
    }

    [Theory]
    [InlineData("PUT", "ticks")]
    [InlineData("DELETE", "ticks")]
    [InlineData("PUT", "books")]
    [InlineData("DELETE", "books")]
    public async Task AStreamThatDoesNotExistIsNotFoundOnEveryRoute(string method, string kind)
    {
        var response = await SendAsync(_api, new HttpMethod(method), $"/v1/stream/0123456789abcdef/{kind}/KX-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ABadTickerIsRefused()
    {
        var (response, _, id) = await OpenReadyAsync("/v1/stream?ticks=KX-1");

        var put = await SendAsync(_api, HttpMethod.Put, $"/v1/stream/{id}/ticks/bad%20ticker");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
        response.Dispose();
    }

    [Fact]
    public async Task AStreamAtItsLimitRefusesAnotherMarketWithAConflict()
    {
        var (response, _, id) = await OpenReadyAsync("/v1/stream?ticks=KX-1,KX-2,KX-3");

        var put = await SendAsync(_api, HttpMethod.Put, $"/v1/stream/{id}/ticks/KX-4");

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Contains("too_many_markets", await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        response.Dispose();
    }

    [Fact]
    public async Task AnotherUsersStreamCannotBeChangedAndWithoutATokenThereIsNoChange()
    {
        var users = new FakeUsers();
        var ann = users.Add("ann");
        var bob = users.Add("bob");
        await using var host = await ApiTestHost.StartWithAuthAsync(users, new AutoAdvanceTimeProvider(autoFire: false, utcStart: Now));
        var (response, reader) = await OpenAsync(host, "/v1/stream?ticks=KX-1", "Bearer " + ann);
        var id = Data<StreamReadyDto>(await NextAsync(reader)).StreamId;

        var strangers = await SendAsync(host, HttpMethod.Put, $"/v1/stream/{id}/ticks/KX-2", "Bearer " + bob);
        var anonymous = await SendAsync(host, HttpMethod.Put, $"/v1/stream/{id}/ticks/KX-2");
        var own = await SendAsync(host, HttpMethod.Put, $"/v1/stream/{id}/ticks/KX-2", "Bearer " + ann);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.Unauthorized, HttpStatusCode.NoContent), (strangers.StatusCode, anonymous.StatusCode, own.StatusCode));
        response.Dispose();
    }
}
