using System.Numerics;

namespace KalTerm.Data.Metrics;

/// <summary>What the database knows about one market at one moment, reduced to the sums the metrics are made of. All money is fixed-point (dollars at 10^-6, counts at 10^-2).</summary>
/// <param name="AsOf">The moment the metrics describe (UTC).</param>
/// <param name="LastTime">The time of the latest ticker update; <see langword="null"/> when there is none in the lookback.</param>
/// <param name="LastE6">The last traded price in that update.</param>
/// <param name="YesBidE6">The yes bid in that update.</param>
/// <param name="YesAskE6">The yes ask in that update.</param>
/// <param name="OpenInterestE2">The open interest in that update.</param>
/// <param name="TradeCount">Trades in the 24 hours before <paramref name="AsOf"/>.</param>
/// <param name="VolumeE2">Contracts traded in those 24 hours (sum of counts).</param>
/// <param name="PriceTimesCount">The sum over those trades of yes price (10^-6) times count (10^-2).</param>
/// <param name="ReferenceCloseE6">The close of the last priced hourly candle that ended at or before 24 hours ago; <see langword="null"/> when there is none.</param>
/// <param name="Closes">The closes of the latest priced hourly candles, newest first, at most <see cref="MetricsCalculator.MaxCloses"/>.</param>
/// <param name="Partial">Whether a gap overlaps the 24 hours, or a candle used is flagged partial.</param>
public sealed record MetricsInputs(
    DateTime AsOf,
    DateTime? LastTime,
    long? LastE6,
    long? YesBidE6,
    long? YesAskE6,
    long? OpenInterestE2,
    long TradeCount,
    decimal VolumeE2,
    decimal PriceTimesCount,
    long? ReferenceCloseE6,
    IReadOnlyList<long> Closes,
    bool Partial);

/// <summary>The metrics of one market, still fixed-point; a metric with too little data is <see langword="null"/>.</summary>
/// <param name="AsOf">The moment described (UTC).</param>
/// <param name="LastTime">When the latest ticker update was.</param>
/// <param name="LastE6">The last traded price.</param>
/// <param name="Change24hE6">The last price minus the close 24 hours ago.</param>
/// <param name="VolumeE2">Contracts traded in 24 hours.</param>
/// <param name="TradeCount">Trades in 24 hours.</param>
/// <param name="OpenInterestE2">The open interest.</param>
/// <param name="YesBidE6">The yes bid.</param>
/// <param name="YesAskE6">The yes ask.</param>
/// <param name="SpreadE6">The ask minus the bid.</param>
/// <param name="Vwap24hE6">The volume-weighted average price of 24 hours of trades.</param>
/// <param name="Sma20E6">The mean of the last 20 hourly closes.</param>
/// <param name="Sma50E6">The mean of the last 50 hourly closes.</param>
/// <param name="Volatility24hE6">The sample standard deviation of the hourly close-to-close changes.</param>
/// <param name="Partial">Whether data may be missing (see <see cref="MetricsInputs.Partial"/>).</param>
public sealed record MarketMetrics(
    DateTime AsOf,
    DateTime? LastTime,
    long? LastE6,
    long? Change24hE6,
    long VolumeE2,
    long TradeCount,
    long? OpenInterestE2,
    long? YesBidE6,
    long? YesAskE6,
    long? SpreadE6,
    long? Vwap24hE6,
    long? Sma20E6,
    long? Sma50E6,
    long? Volatility24hE6,
    bool Partial);

/// <summary>
/// The arithmetic of the metrics, with no database and no floating point. Divisions round half up. An average needs its full count of closes (a shorter history gives
/// <see langword="null"/>, not a smaller average), and the volatility needs at least three closes (two changes).
/// </summary>
public static class MetricsCalculator
{
    /// <summary>The most hourly closes any metric uses.</summary>
    public const int MaxCloses = 50;

    /// <summary>How many latest closes the volatility looks at.</summary>
    public const int VolatilityCloses = 24;

    /// <summary>Computes the metrics.</summary>
    /// <param name="inputs">What the database holds.</param>
    /// <returns>The metrics.</returns>
    public static MarketMetrics Compute(MetricsInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return new MarketMetrics(
            inputs.AsOf,
            inputs.LastTime,
            inputs.LastE6,
            inputs.LastE6 is { } last && inputs.ReferenceCloseE6 is { } reference ? last - reference : null,
            (long)inputs.VolumeE2,
            inputs.TradeCount,
            inputs.OpenInterestE2,
            inputs.YesBidE6,
            inputs.YesAskE6,
            inputs.YesBidE6 is { } bid && inputs.YesAskE6 is { } ask ? ask - bid : null,
            Vwap(inputs.PriceTimesCount, inputs.VolumeE2),
            Sma(inputs.Closes, 20),
            Sma(inputs.Closes, 50),
            Volatility(inputs.Closes),
            inputs.Partial);
    }

    /// <summary>The volume-weighted average price: sum of price times count over the sum of counts, rounded half up; <see langword="null"/> without volume.</summary>
    /// <param name="priceTimesCount">Sum of price (10^-6) times count (10^-2).</param>
    /// <param name="volumeE2">Sum of counts (10^-2).</param>
    /// <returns>The price (10^-6).</returns>
    public static long? Vwap(decimal priceTimesCount, decimal volumeE2) =>
        volumeE2 <= 0 ? null : (long)DivideRounded(new BigInteger(priceTimesCount), new BigInteger(volumeE2));

    /// <summary>The mean of the newest <paramref name="count"/> closes, rounded half up; <see langword="null"/> when there are fewer.</summary>
    /// <param name="closesNewestFirst">Closes (10^-6), newest first.</param>
    /// <param name="count">How many to average.</param>
    /// <returns>The mean (10^-6).</returns>
    public static long? Sma(IReadOnlyList<long> closesNewestFirst, int count)
    {
        ArgumentNullException.ThrowIfNull(closesNewestFirst);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (closesNewestFirst.Count < count)
        {
            return null;
        }

        var sum = BigInteger.Zero;
        for (var i = 0; i < count; i++)
        {
            sum += closesNewestFirst[i];
        }

        return (long)DivideRounded(sum, count);
    }

    /// <summary>
    /// The sample standard deviation of the changes between consecutive closes among the newest <see cref="VolatilityCloses"/>, in dollars (10^-6), rounded half up;
    /// <see langword="null"/> with fewer than three closes.
    /// </summary>
    /// <param name="closesNewestFirst">Closes (10^-6), newest first.</param>
    /// <returns>The deviation (10^-6).</returns>
    public static long? Volatility(IReadOnlyList<long> closesNewestFirst)
    {
        ArgumentNullException.ThrowIfNull(closesNewestFirst);
        var closes = closesNewestFirst.Take(VolatilityCloses).ToList();
        if (closes.Count < 3)
        {
            return null;
        }

        BigInteger sum = 0;
        BigInteger squares = 0;
        var n = closes.Count - 1;
        for (var i = 0; i < n; i++)
        {
            BigInteger change = closes[i] - closes[i + 1];
            sum += change;
            squares += change * change;
        }

        // variance = (n * sum(d^2) - sum(d)^2) / (n * (n - 1)); the deviation is its square root.
        var numerator = (n * squares) - (sum * sum);
        var denominator = new BigInteger(n) * (n - 1);
        var root = IntegerSqrt(numerator / denominator);
        // Round half up without a float: the square root is above root + 1/2 exactly when variance > root^2 + root + 1/4.
        return (long)(4 * numerator > denominator * ((4 * root * root) + (4 * root) + 1) ? root + 1 : root);
    }

    private static BigInteger DivideRounded(BigInteger numerator, BigInteger denominator) =>
        ((2 * numerator) + denominator) / (2 * denominator);

    /// <summary>The largest integer whose square is not above the value (Newton's method).</summary>
    private static BigInteger IntegerSqrt(BigInteger value)
    {
        if (value < 2)
        {
            return value < 0 ? BigInteger.Zero : value;
        }

        var x = BigInteger.One << (int)((value.GetBitLength() + 1) / 2);
        while (true)
        {
            var next = (x + (value / x)) >> 1;
            if (next >= x)
            {
                return x;
            }

            x = next;
        }
    }
}
