using KalTerm.Data.Candles;
using KalTerm.Data.Reading;
using static KalTerm.Api.Tests.Samples;

namespace KalTerm.Api.Tests;

/// <summary>Stored candles served as they are, rolled up from a shorter stored interval, or computed from raw rows, with paging.</summary>
public sealed class CandleServiceTests
{
    private static readonly DateTime Day = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeCandleStore _store = new();
    private readonly FakeBooks _books = new();
    private readonly CandleService _service;

    public CandleServiceTests()
    {
        _store.Rules = [new CandleRule(86400, CandleScope.All), new CandleRule(3600, CandleScope.All), new CandleRule(60, CandleScope.Watched)];
        _store.Markets["KX-1"] = 7;
        _service = new CandleService(_store, _store, _books, new AutoAdvanceTimeProvider(autoFire: false, utcStart: Now));
    }

    private static StoredCandle Candle(DateTime start, int seconds, long open, long high, long low, long close, long volume = 100, bool partial = false) =>
        new(start, seconds, open, high, low, close, volume, 1, 400_000, 410_000, 5_000, partial, false, partial ? "ingest_down" : null, partial ? 30 : null, 7);

    private static CandleQuery Query(int seconds, DateTime? start = null, int limit = 100) => new("KX-1", seconds, start, Day.AddDays(1), limit);

    [Fact]
    public async Task AnUnknownMarketHasNoAnswer()
    {
        _store.Markets.Clear();

        Assert.Null(await _service.GetAsync(Query(3600), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredIntervalIsServedAsItIsAndTheLatestAreTheLastOnes()
    {
        _store.Rows[3600] = [.. Enumerable.Range(0, 10).Select(i => Candle(Day.AddHours(i), 3600, 400_000 + i, 500_000, 300_000, 450_000))];

        var result = (await _service.GetAsync(Query(3600, limit: 3), TestContext.Current.CancellationToken))!;

        Assert.Equal("stored 1h", result.Source);
        Assert.Equal([Day.AddHours(7), Day.AddHours(8), Day.AddHours(9)], result.Candles.Select(c => c.BucketStart));
        Assert.Null(result.Next);
    }

    [Fact]
    public async Task APageFromAStartContinuesWhereTheLimitCutIt()
    {
        _store.Rows[3600] = [.. Enumerable.Range(0, 10).Select(i => Candle(Day.AddHours(i), 3600, 400_000, 500_000, 300_000, 450_000))];

        var result = (await _service.GetAsync(Query(3600, Day, 4), TestContext.Current.CancellationToken))!;

        Assert.Equal(4, result.Candles.Count);
        Assert.Equal(Day.AddHours(4), result.Next);
        Assert.Equal(Day, _store.Reads.Single().Start);
    }

    [Fact]
    public async Task AnIntervalThatDividesAStoredOneIsRolledUpExactly()
    {
        _store.Rows[3600] =
        [
            Candle(Day, 3600, 400_000, 450_000, 390_000, 440_000, 100),
            Candle(Day.AddHours(1), 3600, 441_000, 520_000, 430_000, 500_000, 200, partial: true),
            Candle(Day.AddHours(2), 3600, 500_000, 510_000, 480_000, 490_000, 50),
            Candle(Day.AddHours(3), 3600, 490_000, 495_000, 300_000, 310_000, 70),
        ];

        var result = (await _service.GetAsync(Query(14400, Day), TestContext.Current.CancellationToken))!;

        Assert.Equal("rolled up from stored 1h", result.Source);
        var candle = Assert.Single(result.Candles);
        Assert.Equal((400_000L, 520_000L, 300_000L, 310_000L, 420L), (candle.OpenE6, candle.HighE6, candle.LowE6, candle.CloseE6, candle.VolumeE2));
        Assert.True(candle.Partial);
    }

    [Fact]
    public async Task AMinuteScopedToTheWatchListIsOnlyUsedForAWatchedMarket()
    {
        _store.Rows[60] = [Candle(Day, 60, 400_000, 410_000, 395_000, 405_000)];
        _store.Rows[3600] = [Candle(Day, 3600, 400_000, 500_000, 300_000, 450_000)];

        var unwatched = (await _service.GetAsync(Query(300, Day), TestContext.Current.CancellationToken))!;
        _books.Books.Add(Book("KX-1"));
        var watched = (await _service.GetAsync(Query(300, Day), TestContext.Current.CancellationToken))!;

        Assert.Equal("computed from raw rows (not stored)", unwatched.Source);
        Assert.Equal("rolled up from stored 1m", watched.Source);
    }

    [Fact]
    public async Task AnUnstoredIntervalIsComputedFromRawRowsAndRolledUp()
    {
        _store.Computed = [Candle(Day, 60, 400_000, 410_000, 395_000, 405_000, 10), Candle(Day.AddMinutes(7), 60, 405_000, 420_000, 400_000, 415_000, 20)];

        var result = (await _service.GetAsync(new CandleQuery("KX-1", 300, Day, Day.AddHours(1), 100), TestContext.Current.CancellationToken))!;

        Assert.Equal([Day, Day.AddMinutes(5)], result.Candles.Select(c => c.BucketStart));
        Assert.Equal((Day, Day.AddHours(1)), (_store.ComputeFrom, _store.ComputeUntil));
        Assert.Equal([7], _store.ComputeMarkets);
    }

    [Fact]
    public async Task AComputedRangeLongerThanADayIsRefused()
    {
        await Assert.ThrowsAsync<CandleRangeException>(() => _service.GetAsync(new CandleQuery("KX-1", 300, Day, Day.AddHours(25), 100), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithoutAStartAComputedAnswerLooksBackFromTheEnd()
    {
        await _service.GetAsync(new CandleQuery("KX-1", 300, null, Day.AddHours(10), 12), TestContext.Current.CancellationToken);

        Assert.Equal(Day.AddHours(9), _store.ComputeFrom);
        Assert.Equal(Day.AddHours(10), _store.ComputeUntil);
    }

    [Fact]
    public async Task ARangeEndingBetweenBucketsIncludesTheBucketItEndsIn()
    {
        _store.Rows[3600] = [Candle(Day, 3600, 400_000, 500_000, 300_000, 450_000), Candle(Day.AddHours(1), 3600, 400_000, 500_000, 300_000, 450_000)];

        await _service.GetAsync(new CandleQuery("KX-1", 3600, Day, Day.AddMinutes(90), 10), TestContext.Current.CancellationToken);

        Assert.Equal(Day.AddHours(2), _store.Reads.Single().Until);
    }
}

internal sealed class FakeCandleStore : ICandleReader, ICandleComputer
{
    public IReadOnlyList<CandleRule> Rules { get; set; } = [];

    public Dictionary<string, int> Markets { get; } = [];

    public Dictionary<int, IReadOnlyList<StoredCandle>> Rows { get; } = [];

    public List<(int Interval, DateTime? Start, DateTime Until, int Limit)> Reads { get; } = [];

    public IReadOnlyList<StoredCandle> Computed { get; set; } = [];

    public DateTime ComputeFrom { get; private set; }

    public DateTime ComputeUntil { get; private set; }

    public IReadOnlyCollection<int>? ComputeMarkets { get; private set; }

    public Task<IReadOnlyList<CandleRule>> RulesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Rules);

    public Task<int?> MarketIdAsync(string ticker, CancellationToken cancellationToken = default) => Task.FromResult(Markets.TryGetValue(ticker, out var id) ? (int?)id : null);

    public Task<IReadOnlyList<StoredCandle>> ReadAsync(int marketId, int intervalSeconds, DateTime? start, DateTime until, int limit, CancellationToken cancellationToken = default)
    {
        Reads.Add((intervalSeconds, start, until, limit));
        var rows = Rows.TryGetValue(intervalSeconds, out var all) ? all.Where(c => c.BucketStart < until && (start is null || c.BucketStart >= start)) : [];
        return Task.FromResult<IReadOnlyList<StoredCandle>>([.. start is null ? rows.TakeLast(limit) : rows.Take(limit)]);
    }

    public Task<DateOnly?> OldestRawDayAsync(CancellationToken cancellationToken = default) => Task.FromResult<DateOnly?>(null);

    public Task<IReadOnlyList<StoredCandle>> ComputeAsync(int intervalSeconds, DateTime from, DateTime until, IReadOnlyCollection<int> marketIds, DateTime now, CancellationToken cancellationToken = default)
    {
        ComputeFrom = from;
        ComputeUntil = until;
        ComputeMarkets = marketIds;
        return Task.FromResult(Computed);
    }
}
