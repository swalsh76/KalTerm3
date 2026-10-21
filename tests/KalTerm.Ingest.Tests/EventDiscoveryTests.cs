using KalTerm.Data.Markets;
using KalTerm.Kalshi.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Ingest.Tests;

internal sealed class FakeEventCatalog : IEventCatalog
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Exception? Failure { get; set; }

    public Task<EventPassResult> RunAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Failure is null ? Task.FromResult(new EventPassResult(0, 0, 0, TimeSpan.Zero)) : Task.FromException<EventPassResult>(Failure);
    }
}

internal sealed class FakeEventStore : IEventStore
{
    public List<(IReadOnlyList<EventRecord> Events, DateTime At)> Upserts { get; } = [];

    public List<string> Missing { get; } = [];

    public List<(int Limit, DateTime Before)> MissingAsked { get; } = [];

    public IEnumerable<EventRecord> Stored => Upserts.SelectMany(u => u.Events);

    public Task UpsertAsync(IReadOnlyList<EventRecord> events, DateTime fetchedAtUtc, CancellationToken cancellationToken = default)
    {
        Upserts.Add(([.. events], fetchedAtUtc));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> MissingAsync(int limit, DateTime askedBeforeUtc, CancellationToken cancellationToken = default)
    {
        MissingAsked.Add((limit, askedBeforeUtc));
        return Task.FromResult<IReadOnlyList<string>>([.. Missing.Take(limit)]);
    }
}

public sealed class EventDiscoveryTests
{
    private readonly FakeEventStore _store = new();
    private readonly AutoAdvanceTimeProvider _time = new(autoFire: false, utcStart: new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly List<string> _asked = [];
    private readonly Dictionary<string, KalshiEvent?> _known = new(StringComparer.Ordinal);
    private List<KalshiEvent> _open = [];
    private int _listings;
    private Exception? _getFails;
    private string? _failOn;
    private bool _breakListing;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KalshiEvent Event(string ticker, string title = "Title", string sub = "Sub") => new(ticker, "SER", title, false, sub);

    private EventDiscovery Make(int batch = 500) =>
        new(
            _store,
            ct => Open(ct),
            (ticker, _) =>
            {
                _asked.Add(ticker);
                if (_getFails is not null && (_failOn is null || _failOn == ticker))
                {
                    return Task.FromException<KalshiEvent?>(_getFails);
                }

                return Task.FromResult(_known.GetValueOrDefault(ticker));
            },
            _time,
            NullLogger.Instance,
            batch);

    private async IAsyncEnumerable<KalshiEvent> Open([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _listings++;
        foreach (var item in _open)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }

        if (_breakListing)
        {
            throw new HttpRequestException("listing broke");
        }
    }

    [Fact]
    public async Task TheFirstPassReadsEveryOpenEventAndStoresThemInBatches()
    {
        _open = [.. Enumerable.Range(1, 5).Select(i => Event("EV-" + i, "Title " + i, "Sub " + i))];

        var result = await Make(batch: 2).RunAsync(Ct);

        Assert.Equal((5, 0, 0), (result.Refreshed, result.LookedUp, result.Unknown));
        Assert.Equal([2, 2, 1], _store.Upserts.Select(u => u.Events.Count));
        var first = _store.Stored.First();
        Assert.Equal(("EV-1", "SER", "Title 1", "Sub 1", true), (first.EventTicker, first.SeriesTicker, first.Title, first.SubTitle, first.Known));
        Assert.Equal(_time.GetUtcNow().UtcDateTime, _store.Upserts[0].At);
    }

    [Fact]
    public async Task TheOpenEventsAreReadAgainOnlyAfterAnHour()
    {
        _open = [Event("EV-1")];
        var discovery = Make();
        await discovery.RunAsync(Ct);

        _time.Advance(TimeSpan.FromMinutes(59));
        var soon = await discovery.RunAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(2));
        var later = await discovery.RunAsync(Ct);

        Assert.Equal((0, 1), (soon.Refreshed, later.Refreshed));
        Assert.Equal(2, _listings);
    }

    [Fact]
    public async Task EventsAMarketMentionsAreLookedUpOneByOneAndStoredUnderTheTickerAsked()
    {
        _store.Missing.AddRange(["EV-A", "EV-B"]);
        _known["EV-A"] = Event("EV-A", "Philadelphia vs New York", "PHI vs NYK (Oct 20)");
        _known["EV-B"] = Event("EV-B-OTHER", "Zarate vs Ribero");

        var result = await Make().RunAsync(Ct);

        Assert.Equal((2, 0), (result.LookedUp, result.Unknown));
        Assert.Equal(["EV-A", "EV-B"], _asked);
        var stored = _store.Upserts[^1].Events;
        Assert.Equal(["EV-A", "EV-B"], stored.Select(e => e.EventTicker));   // keyed by what was asked
        Assert.Equal("Zarate vs Ribero", stored[1].Title);
    }

    [Fact]
    public async Task AnEventKalshiHasNoRecordOfIsRememberedAsUnknown()
    {
        _store.Missing.Add("EV-GONE");

        var result = await Make().RunAsync(Ct);

        Assert.Equal((1, 1), (result.LookedUp, result.Unknown));
        Assert.Equal(new EventRecord("EV-GONE", string.Empty, string.Empty, string.Empty, Known: false), _store.Upserts[^1].Events.Single());
    }

    [Fact]
    public async Task EachPassAsksForAtMostAHundredAndLeavesUnknownEventsAloneForAWeek()
    {
        _store.Missing.AddRange(Enumerable.Range(0, 250).Select(i => "EV-" + i));
        foreach (var ticker in _store.Missing)
        {
            _known[ticker] = Event(ticker);
        }

        var result = await Make().RunAsync(Ct);

        Assert.Equal(EventDiscovery.LookupsPerPass, result.LookedUp);
        Assert.Equal(100, _asked.Count);
        var (limit, before) = _store.MissingAsked.Single();
        Assert.Equal((100, _time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(7)), (limit, before));
    }

    [Fact]
    public async Task AFailedLookupStopsThePassKeepsWhatWasReadAndTellsTheCaller()
    {
        _store.Missing.AddRange(["EV-1", "EV-2", "EV-3"]);
        _known["EV-1"] = Event("EV-1");
        _getFails = new HttpRequestException("rate limited");
        _failOn = "EV-2";

        await Assert.ThrowsAsync<HttpRequestException>(() => Make().RunAsync(Ct));

        Assert.Equal(["EV-1", "EV-2"], _asked);
        Assert.Equal(["EV-1"], _store.Upserts[^1].Events.Select(e => e.EventTicker));   // the one that worked is stored
    }

    [Fact]
    public async Task AFailedListingStoresWhatCameBeforeItAndTheSamePassIsTriedAgainNextTime()
    {
        _open = [Event("EV-1"), Event("EV-2"), Event("EV-3")];
        _breakListing = true;
        var discovery = Make(batch: 2);

        await Assert.ThrowsAsync<HttpRequestException>(() => discovery.RunAsync(Ct));
        Assert.Equal(2, _store.Stored.Count());   // a full batch was stored before the listing broke

        _breakListing = false;
        var again = await discovery.RunAsync(Ct);

        Assert.Equal(3, again.Refreshed);   // the failed pass did not count as a refresh: the very next one reads everything
        Assert.Equal(2, _listings);
    }

    [Fact]
    public async Task NothingToDoIsAQuietPassAndMissingArgumentsAreRefused()
    {
        var result = await Make().RunAsync(Ct);

        Assert.Equal((0, 0, 0), (result.Refreshed, result.LookedUp, result.Unknown));
        Assert.Empty(_store.Upserts);
        Assert.Throws<ArgumentNullException>(() => new EventDiscovery(null!, _ => Open(_), (_, _) => Task.FromResult<KalshiEvent?>(null)));
        Assert.Throws<ArgumentNullException>(() => new EventDiscovery(_store, null!, (_, _) => Task.FromResult<KalshiEvent?>(null)));
        Assert.Throws<ArgumentNullException>(() => new EventDiscovery(_store, _ => Open(_), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventDiscovery(_store, _ => Open(_), (_, _) => Task.FromResult<KalshiEvent?>(null), batchSize: 0));
    }

    [Fact]
    public void TheMarketMapperCarriesTheTitle()
    {
        var market = new Market("KXA-1", "EV-1", "binary", "active", Title: "Philadelphia wins");

        Assert.Equal("Philadelphia wins", MarketMapper.ToRecord(market).Title);
    }
}
