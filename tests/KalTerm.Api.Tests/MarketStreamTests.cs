using System.Net;
using System.Threading.Channels;
using KalTerm.Contracts;
using KalTerm.Data.Reading;
using static KalTerm.Api.Tests.Samples;

namespace KalTerm.Api.Tests;

/// <summary>The hub: who watches what, what one poll reads, and what it hands to which stream.</summary>
public sealed class MarketStreamHubTests
{
    private readonly FakeStreamReader _reader = new();
    private readonly FakeBooks _books = new();
    private readonly AutoAdvanceTimeProvider _time = new(autoFire: false, utcStart: Now);

    private MarketStreamHub Hub(MarketStreamOptions? options = null) => new(_reader, _books, _time, options ?? new MarketStreamOptions { MaxPerUser = 2, MaxTotal = 3 });

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

    private static StreamTick Row(string ticker, DateTime ts, long price = 560_000) => new(ticker, Tick(ts, price));

    [Fact]
    public void TheDefaultsAreFivePerUserFiftyInAllFiftyMarketsAndATwoSecondLag()
    {
        var options = new MarketStreamOptions();

        Assert.Equal((5, 50, 50, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)), (options.MaxPerUser, options.MaxTotal, options.MaxMarkets, options.KeepAlive, options.TokenCheck, options.Poll, options.TickLag));
        Assert.Equal((10, 100, TimeSpan.FromMinutes(10)), (options.DefaultDepth, options.MaxDepth, options.MaxReplay));
    }

    [Fact]
    public async Task AUserAndTheServerEachHaveALimitAndAFreedPlaceCanBeTakenAgain()
    {
        using var hub = Hub();

        var a1 = await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct);
        var a2 = await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct);
        var a3 = await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct);   // the user's third
        var b1 = await hub.TryOpenAsync(2, Set("KX-1"), Set(), Ct);   // the server's third
        var c1 = await hub.TryOpenAsync(3, Set("KX-1"), Set(), Ct);   // the server's fourth

        Assert.NotNull(a1);
        Assert.NotNull(a2);
        Assert.Null(a3);
        Assert.NotNull(b1);
        Assert.Null(c1);
        Assert.Equal(3, hub.Open);
        a1.Dispose();
        a1.Dispose();   // twice is the same as once
        Assert.Equal(2, hub.Open);
        Assert.NotNull(await hub.TryOpenAsync(3, Set("KX-1"), Set(), Ct));
    }

    [Fact]
    public async Task WithNoStreamOpenNothingIsRead()
    {
        using var hub = Hub();

        await hub.PollAsync(Ct);

        Assert.Empty(_reader.Reads);
        Assert.Equal(0, _reader.Resolves);
        Assert.Equal(0, _books.ManyReads);
    }

    [Fact]
    public async Task TicksReachOnlyTheStreamsThatWatchThemAndNothingIsSentTwice()
    {
        using var hub = Hub();
        using var one = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;
        using var two = (await hub.TryOpenAsync(2, Set("KX-2"), Set(), Ct))!;
        _reader.Rows.AddRange([Row("KX-1", NowUtc.AddSeconds(-1)), Row("KX-2", NowUtc.AddSeconds(1)), Row("KX-1", NowUtc.AddSeconds(2.5)), Row("KX-1", NowUtc.AddSeconds(4))]);

        _time.Advance(TimeSpan.FromSeconds(5));   // now 23:00:05: rows older than 23:00:03 are due
        await hub.PollAsync(Ct);
        var first = (Drain(one).Cast<TickItem>().ToList(), Drain(two).Cast<TickItem>().ToList());
        _time.Advance(TimeSpan.FromSeconds(2));
        await hub.PollAsync(Ct);
        var second = (Drain(one).Cast<TickItem>().ToList(), Drain(two));

        Assert.Equal([NowUtc.AddSeconds(-1), NowUtc.AddSeconds(2.5)], first.Item1.Select(t => t.Tick.Ts));   // from the moment the stream began (22:59:58), up to 2 s behind now
        Assert.Equal(["KX-2"], first.Item2.Select(t => t.Ticker));
        Assert.Equal([NowUtc.AddSeconds(4)], second.Item1.Select(t => t.Tick.Ts));
        Assert.Empty(second.Item2);
    }

    [Fact]
    public async Task EachPollReadsFromWhereTheLastEndedAndTwoSecondsBehindNow()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;

        _time.Advance(TimeSpan.FromSeconds(5));
        await hub.PollAsync(Ct);
        _time.Advance(TimeSpan.FromSeconds(3));
        await hub.PollAsync(Ct);

        Assert.Equal(NowUtc.AddSeconds(-2), session.JoinCursor);
        Assert.Equal([(NowUtc.AddSeconds(-2), NowUtc.AddSeconds(3)), (NowUtc.AddSeconds(3), NowUtc.AddSeconds(6))], _reader.Reads.Select(r => (r.From, r.Until)));
    }

    [Fact]
    public async Task AStreamOpenedBetweenPollsStartsWhereTheLastPollEnded()
    {
        using var hub = Hub();
        using var early = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;
        _time.Advance(TimeSpan.FromSeconds(5));
        await hub.PollAsync(Ct);

        _time.Advance(TimeSpan.FromSeconds(1));
        using var late = (await hub.TryOpenAsync(2, Set("KX-2"), Set(), Ct))!;

        Assert.Equal(NowUtc.AddSeconds(3), late.JoinCursor);   // the end of the last poll: nothing between it and the next poll is missed
    }

    [Fact]
    public async Task AFailedReadLosesNothing()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;
        _reader.Rows.Add(Row("KX-1", NowUtc.AddSeconds(-1)));
        _time.Advance(TimeSpan.FromSeconds(5));

        _reader.Throw = new InvalidOperationException("database down");
        await hub.PollAsync(Ct);
        Assert.Empty(Drain(session));
        _reader.Throw = null;
        await hub.PollAsync(Ct);

        Assert.Single(Drain(session));
        Assert.Equal(_reader.Reads[0].From, _reader.Reads[^1].From);   // the second read began where the failed one did
    }

    [Fact]
    public async Task AReadThatHitTheRowLimitHoldsBackTheLastMillisecondUntilNextTime()
    {
        using var hub = Hub(new MarketStreamOptions { PollRows = 2 });
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;
        _reader.Rows.AddRange([Row("KX-1", NowUtc.AddSeconds(-1)), Row("KX-1", NowUtc.AddSeconds(0.5)), Row("KX-1", NowUtc.AddSeconds(1))]);
        _time.Advance(TimeSpan.FromSeconds(10));

        var polls = new List<List<DateTime>>();
        for (var i = 0; i < 3; i++)
        {
            await hub.PollAsync(Ct);
            polls.Add([.. Drain(session).Cast<TickItem>().Select(t => t.Tick.Ts)]);
        }

        // a read that filled the limit may have cut a millisecond short, so its last row waits for the next read; each row is delivered once, in order
        Assert.Equal([[NowUtc.AddSeconds(-1)], [NowUtc.AddSeconds(0.5)], [NowUtc.AddSeconds(1)]], polls);
    }

    [Fact]
    public async Task ARowLimitFilledByOneMillisecondPassesWhatItHasAndMovesOn()
    {
        using var hub = Hub(new MarketStreamOptions { PollRows = 2 });
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;
        _reader.Rows.AddRange([Row("KX-1", NowUtc, 1), Row("KX-1", NowUtc, 2), Row("KX-1", NowUtc, 3)]);
        _time.Advance(TimeSpan.FromSeconds(10));

        await hub.PollAsync(Ct);
        await hub.PollAsync(Ct);

        Assert.Equal(2, Drain(session).Count);   // the third row of the same millisecond cannot be told apart from a retry: it is the one place the stream can lose data, and it logs
        Assert.Equal(NowUtc.AddMilliseconds(1), _reader.Reads[1].From);
    }

    [Fact]
    public async Task ABookIsSentWhenItChangedAndNotWhileItStaysTheSame()
    {
        using var hub = Hub();
        using var watching = (await hub.TryOpenAsync(1, Set(), Set("KX-1"), Ct))!;
        using var other = (await hub.TryOpenAsync(2, Set(), Set("KX-2"), Ct))!;
        _books.Books.Add(Book("KX-1"));

        await hub.PollAsync(Ct);
        var first = Drain(watching);
        await hub.PollAsync(Ct);
        var same = Drain(watching);
        _books.Books[0] = _books.Books[0] with { UpdatedAt = NowUtc };
        await hub.PollAsync(Ct);
        var moved = Drain(watching);
        _books.Books[0] = _books.Books[0] with { Stale = true };
        await hub.PollAsync(Ct);
        var stale = Drain(watching);

        Assert.Equal("KX-1", Assert.IsType<BookItem>(Assert.Single(first)).Book.Ticker);
        Assert.Empty(same);
        Assert.Single(moved);
        Assert.True(Assert.IsType<BookItem>(Assert.Single(stale)).Book.Stale);
        Assert.Empty(Drain(other));
    }

    [Fact]
    public async Task ATickerThatIsNotAMarketIsLookedForAgainOnlyAfterTheRecheckTime()
    {
        using var hub = Hub();
        using var session = (await hub.TryOpenAsync(1, Set("KX-9"), Set(), Ct))!;
        _time.Advance(TimeSpan.FromSeconds(5));

        await hub.PollAsync(Ct);
        await hub.PollAsync(Ct);
        Assert.Equal(1, _reader.Resolves);
        _time.Advance(TimeSpan.FromSeconds(31));
        _reader.Markets["KX-9"] = 9;
        _reader.Rows.Add(Row("KX-9", NowUtc.AddSeconds(10)));
        await hub.PollAsync(Ct);

        Assert.Equal(2, _reader.Resolves);
        Assert.Equal("KX-9", Assert.IsType<TickItem>(Assert.Single(Drain(session))).Ticker);
    }

    [Fact]
    public async Task AClientThatFallsTooFarBehindIsCutOffNotAllowedToGrowAQueueWithoutEnd()
    {
        using var hub = Hub(new MarketStreamOptions { QueueCapacity = 2 });
        using var session = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;
        _reader.Rows.AddRange(Enumerable.Range(0, 5).Select(i => Row("KX-1", NowUtc.AddSeconds(-1 + (i * 0.1)))));
        _time.Advance(TimeSpan.FromSeconds(10));

        await hub.PollAsync(Ct);

        Assert.True(session.Overflowed);
        Assert.Equal(2, Drain(session).Count);   // what was queued is still delivered
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await session.Reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task WhenEveryStreamHasEndedTheNextOneStartsFromItsOwnNow()
    {
        using var hub = Hub();
        var first = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;
        await hub.PollAsync(Ct);
        first.Dispose();
        await hub.PollAsync(Ct);   // idle: forgets where it was
        _time.Advance(TimeSpan.FromMinutes(5));

        using var again = (await hub.TryOpenAsync(1, Set("KX-1"), Set(), Ct))!;

        Assert.Equal(NowUtc.AddMinutes(5).AddSeconds(-2), again.JoinCursor);
    }

    [Fact]
    public async Task ADisposedHubGivesBackItsGate()
    {
        var hub = Hub();
        hub.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => hub.PollAsync(Ct));
    }
}

/// <summary>The market stream over a test server: what a client reads, in what order, and when it is turned away or cut off.</summary>
public sealed class MarketStreamEndpointTests : IAsyncLifetime
{
    private ApiTestHost _api = null!;

    private FakeReaders Fakes => _api.Fakes;

    public async ValueTask InitializeAsync() => _api = await ApiTestHost.StartAsUserAsync(42);

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private static async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenAsync(ApiTestHost host, string url, string? lastEventId = null, string? authorization = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        if (lastEventId is not null)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        return (response, new StreamReader(stream));
    }

    /// <summary>The next event as its lines (without the blank line that ends it); comments count as events here.</summary>
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

    private async Task PollAsync(double advanceSeconds = 0)
    {
        Fakes.StreamTime.Advance(TimeSpan.FromSeconds(advanceSeconds));
        await Fakes.StreamHub.PollAsync(TestContext.Current.CancellationToken);
    }

    private static string At(double secondsFromNow) => NowUtc.AddSeconds(secondsFromNow).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheStreamStartsWithAReadyEventSayingWhatWasAcceptedAndWhereLiveDataBegins()
    {
        Fakes.Books.Books.Add(Book("KX-1"));

        var (response, reader) = await OpenAsync(_api, "/v1/stream?ticks=KX-1,KX-9&books=KX-1,KX-2");
        var ready = await NextAsync(reader);

        Assert.Equal(("text/event-stream", "no-cache"), (response.Content.Headers.ContentType!.MediaType, response.Headers.CacheControl!.ToString()));
        Assert.Equal($"id: {At(-2)}", ready[0]);
        Assert.Equal("event: ready", ready[1]);
        var dto = Data<StreamReadyDto>(ready);
        Assert.Equal(16, dto.StreamId.Length);
        Assert.Equal((At(-2), 10), (dto.ResumeFrom, dto.Depth));
        Assert.Equal(["KX-1"], dto.Ticks);
        Assert.Equal(["KX-1", "KX-2"], dto.Books);
        Assert.Equal(["KX-9"], dto.Unknown);
        Assert.Equal(["KX-2"], dto.NoBook);
        response.Dispose();
    }

    [Fact]
    public async Task EachWatchedBookIsSentAtOnceAtTheDepthAskedFor()
    {
        Fakes.Books.Books.Add(Book("KX-1", levels: 5));

        var (response, reader) = await OpenAsync(_api, "/v1/stream?books=KX-1&depth=2");
        Assert.Equal("event: ready", (await NextAsync(reader))[1]);
        var book = await NextAsync(reader);

        Assert.Equal("event: book", book[0]);   // no id: a book never moves the resume point
        var dto = Data<OrderBookDto>(book);
        Assert.Equal(("KX-1", 2, 2), (dto.Ticker, dto.YesBids.Count, dto.NoBids.Count));
        response.Dispose();
    }

    [Fact]
    public async Task ALiveTickArrivesOnceWithItsTimeAsItsIdAndABookChangeArrivesWhole()
    {
        Fakes.Books.Books.Add(Book("KX-1"));
        var (response, reader) = await OpenAsync(_api, "/v1/stream?ticks=KX-1&books=KX-1");
        await NextAsync(reader);
        await NextAsync(reader);   // the opening book
        Fakes.StreamReader.Rows.Add(new StreamTick("KX-1", Tick(NowUtc.AddSeconds(-1), 570_000)));

        await PollAsync(5);
        var tick = await NextAsync(reader);

        Assert.Equal([$"id: {At(-1)}", "event: tick"], tick.Take(2));
        var dto = Data<MarketTickDto>(tick);
        Assert.Equal(("KX-1", "0.570000"), (dto.Ticker, dto.Tick.Price));
        Assert.Equal([": keep-alive"], await NextAsync(reader));   // sent once, not again

        Fakes.Books.Books[0] = Fakes.Books.Books[0] with { UpdatedAt = NowUtc.AddSeconds(5) };
        await PollAsync(1);
        var book = await NextAsync(reader);
        Assert.Equal("event: book", book[0]);
        Assert.Equal(NowUtc.AddSeconds(5).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", System.Globalization.CultureInfo.InvariantCulture), Data<OrderBookDto>(book).UpdatedAt);
        response.Dispose();
    }

    [Fact]
    public async Task OnlyTheMarketsAskedForAreEverPushed()
    {
        var (response, reader) = await OpenAsync(_api, "/v1/stream?ticks=KX-1&books=KX-2");
        await NextAsync(reader);
        Fakes.StreamReader.Rows.AddRange([new StreamTick("KX-1", Tick(NowUtc.AddSeconds(-1))), new StreamTick("KX-2", Tick(NowUtc.AddSeconds(-1))), new StreamTick("KX-3", Tick(NowUtc.AddSeconds(-1)))]);

        await PollAsync(5);

        Assert.Equal("KX-1", Data<MarketTickDto>(await NextAsync(reader)).Ticker);
        Assert.Equal([": keep-alive"], await NextAsync(reader));
        response.Dispose();
    }

    [Theory]
    [InlineData("after")]
    [InlineData("header")]
    public async Task AResumeReplaysTheTicksSinceThenGoesLive(string how)
    {
        Fakes.StreamReader.Rows.AddRange(
        [
            new StreamTick("KX-1", Tick(NowUtc.AddSeconds(-10))),
            new StreamTick("KX-1", Tick(NowUtc.AddSeconds(-5), 551_000)),
            new StreamTick("KX-2", Tick(NowUtc.AddSeconds(-4))),
            new StreamTick("KX-1", Tick(NowUtc.AddSeconds(-1), 552_000)),
        ]);

        var (response, reader) = how == "after"
            ? await OpenAsync(_api, $"/v1/stream?ticks=KX-1&after={At(-5)}")
            : await OpenAsync(_api, "/v1/stream?ticks=KX-1", lastEventId: At(-5));
        Assert.Equal("event: ready", (await NextAsync(reader))[1]);
        var replayed = await NextAsync(reader);

        Assert.Equal([$"id: {At(-5)}", "event: tick"], replayed.Take(2));   // inclusive: the tick at the resume moment comes again, and the client drops the repeat
        Assert.Equal("0.551000", Data<MarketTickDto>(replayed).Tick.Price);
        Assert.Equal([": keep-alive"], await NextAsync(reader));   // KX-2 was not asked for; the tick at -1 is after the join point so it comes live
        await PollAsync(5);
        Assert.Equal($"id: {At(-1)}", (await NextAsync(reader))[0]);
        response.Dispose();
    }

    [Fact]
    public async Task AResumePointTooFarBackIsAnsweredWithAResetAndTheStreamEnds()
    {
        var (response, reader) = await OpenAsync(_api, $"/v1/stream?ticks=KX-1&after={At(-1200)}");
        await NextAsync(reader);

        var reset = await NextAsync(reader);

        Assert.Equal("event: reset", reset[0]);
        Assert.Equal("too_far_behind", Data<StreamResetDto>(reset).Reason);
        Assert.Null(await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        response.Dispose();
    }

    [Fact]
    public async Task AReplayLargerThanTheLimitIsAResetToo()
    {
        await using var small = await ApiTestHost.StartAsUserAsync(42, new MarketStreamOptions { ReplayRows = 2, KeepAlive = TimeSpan.FromMilliseconds(150) });
        small.Fakes.StreamReader.Rows.AddRange(Enumerable.Range(0, 3).Select(i => new StreamTick("KX-1", Tick(NowUtc.AddSeconds(-30 + i)))));

        var (response, reader) = await OpenAsync(small, $"/v1/stream?ticks=KX-1&after={At(-60)}");
        await NextAsync(reader);

        Assert.Equal("too_far_behind", Data<StreamResetDto>(await NextAsync(reader)).Reason);
        response.Dispose();
    }

    [Theory]
    [InlineData("/v1/stream")]
    [InlineData("/v1/stream?depth=5")]
    [InlineData("/v1/stream?ticks=KX-1,bad%20ticker")]
    [InlineData("/v1/stream?ticks=KX-1,KX-2&books=KX-3,KX-4")]
    [InlineData("/v1/stream?ticks=KX-1&after=yesterday")]
    [InlineData("/v1/stream?ticks=KX-1&depth=0")]
    [InlineData("/v1/stream?ticks=KX-1&depth=101")]
    [InlineData("/v1/stream?ticks=KX-1&ticks=KX-2")]
    public async Task AnUnusableRequestIsRefusedBeforeAnyStreamOpens(string url)
    {
        var (response, _) = await OpenAsync(_api, url);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, Fakes.StreamHub.Open);
    }

    [Fact]
    public async Task AMarketAskedForTwiceCountsOnce()
    {
        var (response, reader) = await OpenAsync(_api, "/v1/stream?ticks=KX-1,KX-2,KX-3&books=KX-1,KX-2,KX-3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, Data<StreamReadyDto>(await NextAsync(reader)).Ticks.Count);
        response.Dispose();
    }

    [Fact]
    public async Task AUserOverTheLimitIsTurnedAwayWithATryAgainLaterAndGetsInOnceAStreamEnds()
    {
        var (first, firstReader) = await OpenAsync(_api, "/v1/stream?ticks=KX-1");
        var (second, secondReader) = await OpenAsync(_api, "/v1/stream?ticks=KX-1");
        await NextAsync(firstReader);
        await NextAsync(secondReader);

        var (third, _) = await OpenAsync(_api, "/v1/stream?ticks=KX-1");
        var body = await third.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("30", third.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("too_many_streams", ApiJson.Deserialize<ErrorDto>(body)!.Error);
        first.Dispose();
        await Task.Delay(300, TestContext.Current.CancellationToken);   // the server notices the client went away at its next keep-alive
        var (again, againReader) = await OpenAsync(_api, "/v1/stream?ticks=KX-1");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("event: ready", (await NextAsync(againReader))[1]);
        second.Dispose();
        again.Dispose();
    }

    [Fact]
    public async Task WithoutAUserThereIsNoStream()
    {
        await using var anonymous = await ApiTestHost.StartAsync();

        var (response, _) = await OpenAsync(anonymous, "/v1/stream?ticks=KX-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, anonymous.Fakes.StreamHub.Open);
    }

    [Fact]
    public async Task AClientTooSlowToKeepUpIsToldToReloadAndCutOff()
    {
        await using var slow = await ApiTestHost.StartAsUserAsync(42, new MarketStreamOptions { QueueCapacity = 2, KeepAlive = TimeSpan.FromMilliseconds(150) });
        var (response, reader) = await OpenAsync(slow, "/v1/stream?ticks=KX-1");
        await NextAsync(reader);
        slow.Fakes.StreamReader.Rows.AddRange(Enumerable.Range(0, 6).Select(i => new StreamTick("KX-1", Tick(NowUtc.AddSeconds(-1 + (i * 0.1))))));

        slow.Fakes.StreamTime.Advance(TimeSpan.FromSeconds(10));
        await slow.Fakes.StreamHub.PollAsync(TestContext.Current.CancellationToken);
        var events = new List<List<string>>();
        while (true)
        {
            var next = await NextAsync(reader);
            if (next.Count == 0)
            {
                break;
            }

            events.Add(next);
        }

        Assert.Equal("event: reset", events[^1][0]);
        Assert.Equal("slow_consumer", Data<StreamResetDto>(events[^1]).Reason);
        Assert.All(events.Take(events.Count - 1), e => Assert.Equal("event: tick", e[1]));
        response.Dispose();
    }

    [Fact]
    public async Task ATokenRevokedWhileStreamingEndsTheStreamAtItsNextTokenCheck()
    {
        var users = new FakeUsers();
        var token = users.Add("ann");
        var time = new AutoAdvanceTimeProvider(autoFire: false, utcStart: Now);
        await using var host = await ApiTestHost.StartWithAuthAsync(users, time);
        var (response, reader) = await OpenAsync(host, "/v1/stream?ticks=KX-1", authorization: "Bearer " + token);
        Assert.Equal("event: ready", (await NextAsync(reader))[1]);

        users.Tokens.Remove(token);
        time.Advance(TimeSpan.FromSeconds(61));   // past the token cache and the stream's own check interval
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line;
        do
        {
            line = await reader.ReadLineAsync(guard.Token);
        }
        while (line is not null);

        Assert.Null(line);   // the server ended the stream
        response.Dispose();
    }

    [Fact]
    public async Task TheStreamNeedsABearerToken()
    {
        var users = new FakeUsers();
        users.Add("ann");
        await using var host = await ApiTestHost.StartWithAuthAsync(users, new AutoAdvanceTimeProvider(autoFire: false, utcStart: Now));

        var (response, _) = await OpenAsync(host, "/v1/stream?ticks=KX-1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
