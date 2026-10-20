using KalTerm.Data.Candles;
using KalTerm.Data.Reading;

namespace KalTerm.Api;

/// <summary>What a client asked of the candle endpoint.</summary>
/// <param name="Ticker">The market.</param>
/// <param name="IntervalSeconds">The bucket length wanted (a whole number of minutes up to a day, or a week).</param>
/// <param name="Start">The first bucket wanted, or <see langword="null"/> for the latest ones.</param>
/// <param name="End">The end of the range (exclusive).</param>
/// <param name="Limit">The most candles.</param>
public sealed record CandleQuery(string Ticker, int IntervalSeconds, DateTime? Start, DateTime End, int Limit);

/// <summary>What the candle service found.</summary>
/// <param name="Candles">The candles, oldest first.</param>
/// <param name="Source">How they were obtained: stored, rolled up from a stored shorter interval, or computed from the raw rows.</param>
/// <param name="Next">Where the next page starts (a bucket start), or <see langword="null"/> when there is no more.</param>
public sealed record CandleResult(IReadOnlyList<StoredCandle> Candles, string Source, DateTime? Next);

/// <summary>The range asked for cannot be answered from the raw rows because it is longer than the computing fallback allows.</summary>
public sealed class CandleRangeException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CandleRangeException()
        : base("the range is too long to compute from raw rows")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    public CandleRangeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The cause.</param>
    public CandleRangeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Answers candle requests.</summary>
public interface ICandleService
{
    /// <summary>Finds the candles of a request.</summary>
    /// <param name="query">The request.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The candles, or <see langword="null"/> when there is no such market.</returns>
    /// <exception cref="CandleRangeException">No stored interval fits and the range is too long to compute from raw rows.</exception>
    Task<CandleResult?> GetAsync(CandleQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Picks the cheapest exact way to answer: the interval itself if it is stored; else a stored shorter interval that divides it (rolled up exactly: open first, close last, high max, low min,
/// volume sum); else computed from the raw rows of that one market, which only reaches back as far as the retention keeps them and is limited to a day. A stored interval whose scope is the
/// watch list is only used for a watched market. Only closed buckets are stored, so the newest candle can lack the part of its period that has not closed yet.
/// </summary>
/// <param name="reader">Stored candles.</param>
/// <param name="computer">Computes candles from raw rows.</param>
/// <param name="books">Tells which markets are on the watch list.</param>
/// <param name="time">The clock.</param>
public sealed class CandleService(ICandleReader reader, ICandleComputer computer, IBookReader books, TimeProvider time) : ICandleService
{
    /// <summary>The longest range computed from raw rows.</summary>
    public static readonly TimeSpan MaxComputedRange = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public async Task<CandleResult?> GetAsync(CandleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await reader.MarketIdAsync(query.Ticker, cancellationToken).ConfigureAwait(false) is not { } marketId)
        {
            return null;
        }

        var rules = await reader.RulesAsync(cancellationToken).ConfigureAwait(false);
        var watched = rules.Any(r => r.Scope == CandleScope.Watched) && await books.GetAsync(query.Ticker, cancellationToken).ConfigureAwait(false) is not null;
        var baseRule = rules
            .Where(r => r.IntervalSeconds <= query.IntervalSeconds && query.IntervalSeconds % r.IntervalSeconds == 0 && (r.Scope == CandleScope.All || watched))
            .OrderByDescending(r => r.IntervalSeconds)
            .FirstOrDefault();
        return baseRule is not null
            ? await FromStoredAsync(marketId, baseRule.IntervalSeconds, query, cancellationToken).ConfigureAwait(false)
            : await ComputeAsync(marketId, query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CandleResult> FromStoredAsync(int marketId, int baseSeconds, CandleQuery query, CancellationToken cancellationToken)
    {
        var target = query.IntervalSeconds;
        var ratio = target / baseSeconds;
        var until = Ceiling(query.End, target);
        var from = query.Start is { } start ? CandleRules.BucketStart(start, target) : (DateTime?)null;
        var fetch = (int)Math.Min(CandleReader.MaxRows, ((long)query.Limit + 1) * ratio);
        var rows = await reader.ReadAsync(marketId, baseSeconds, from, until, fetch, cancellationToken).ConfigureAwait(false);
        var rolled = baseSeconds == target ? rows : CandleRollup.Roll(rows, target);
        var truncated = rows.Count >= fetch;
        DateTime? next = null;
        if (from is null)
        {
            if (truncated && rolled.Count > 0)
            {
                rolled = [.. rolled.Skip(1)];   // the newest rows were cut mid-way through the oldest bucket, which would be incomplete
            }

            rolled = [.. rolled.Skip(Math.Max(0, rolled.Count - query.Limit))];
        }
        else if (rolled.Count > query.Limit)
        {
            next = rolled[query.Limit].BucketStart;
            rolled = [.. rolled.Take(query.Limit)];
        }
        else if (truncated && rolled.Count > 0)
        {
            next = rolled[^1].BucketStart.AddSeconds(target);
            rolled = [.. rolled.Take(rolled.Count - 1)];   // the last bucket may be incomplete: it is the first of the next page
            next = rolled.Count > 0 ? next : null;
        }

        var text = CandleRules.IntervalText(baseSeconds);
        return new CandleResult(rolled, baseSeconds == target ? $"stored {text}" : $"rolled up from stored {text}", next);
    }

    private async Task<CandleResult> ComputeAsync(int marketId, CandleQuery query, CancellationToken cancellationToken)
    {
        var target = query.IntervalSeconds;
        var end = query.End;
        var start = query.Start ?? end - TimeSpan.FromSeconds(Math.Min(MaxComputedRange.TotalSeconds, (double)query.Limit * target));
        if (end - start > MaxComputedRange)
        {
            throw new CandleRangeException($"an interval that is not stored is computed from raw rows for at most {MaxComputedRange.TotalHours:0} hours at a time");
        }

        var from = CandleRules.BucketStart(start, target);
        var until = Ceiling(end, target);
        var baseCandles = await computer.ComputeAsync(60, CandleRules.BucketStart(from, 60), Ceiling(until, 60), [marketId], time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
        var rolled = CandleRollup.Roll(baseCandles.Where(c => c.BucketStart >= from), target);
        DateTime? next = null;
        if (query.Start is not null && rolled.Count > query.Limit)
        {
            next = rolled[query.Limit].BucketStart;
        }

        return new CandleResult(query.Start is null ? [.. rolled.Skip(Math.Max(0, rolled.Count - query.Limit))] : [.. rolled.Take(query.Limit)], "computed from raw rows (not stored)", next);
    }

    private static DateTime Ceiling(DateTime moment, int seconds)
    {
        var floor = CandleRules.BucketStart(moment, seconds);
        return floor == moment ? moment : floor.AddSeconds(seconds);
    }
}
