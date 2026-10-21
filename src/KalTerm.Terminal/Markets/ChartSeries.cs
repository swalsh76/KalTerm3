using System.Globalization;
using KalTerm.Contracts;

namespace KalTerm.Terminal.Markets;

/// <summary>
/// One candle ready to draw. The coordinates are <see cref="double"/> because every plotting library draws with them; they are made here, at the drawing boundary, from the API's exact decimal text and used for nothing but
/// drawing. Everything a person reads (the hover line, the headings) comes from <see cref="Source"/>, the exact text.
/// </summary>
/// <param name="Start">The start of the bucket (UTC).</param>
/// <param name="Open">The first trade's yes price, or <see langword="null"/> when nothing traded in the bucket.</param>
/// <param name="High">The highest trade price.</param>
/// <param name="Low">The lowest trade price.</param>
/// <param name="Close">The last trade's price.</param>
/// <param name="Volume">Contracts traded.</param>
/// <param name="Source">The candle as the API sent it.</param>
internal sealed record ChartCandle(DateTime Start, double? Open, double? High, double? Low, double? Close, double Volume, CandleDto Source)
{
    /// <summary>Whether the bucket has a trade price to draw (a bucket with ticks but no trades has none and is not filled forward).</summary>
    public bool HasPrice => Open is not null && High is not null && Low is not null && Close is not null;

    /// <summary>Whether data may be missing from the bucket (a gap overlaps it).</summary>
    public bool Partial => Source.Partial;

    /// <summary>Reads a candle.</summary>
    /// <param name="candle">The candle as the API sent it.</param>
    /// <returns>The candle ready to draw.</returns>
    public static ChartCandle From(CandleDto candle)
    {
        ArgumentNullException.ThrowIfNull(candle);
        return new ChartCandle(
            DateTimeOffset.Parse(candle.Time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime,
            Number(candle.Open),
            Number(candle.High),
            Number(candle.Low),
            Number(candle.Close),
            Number(candle.Volume) ?? 0,
            candle);
    }

    private static double? Number(string? text) =>
        text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>The candles of one chart, oldest first, all of one interval.</summary>
/// <param name="IntervalSeconds">The bucket length.</param>
/// <param name="Candles">The candles.</param>
internal sealed record ChartSeries(int IntervalSeconds, IReadOnlyList<ChartCandle> Candles)
{
    /// <summary>Nothing to draw.</summary>
    public static ChartSeries Empty { get; } = new(3600, []);

    /// <summary>The candle whose bucket contains a moment, or <see langword="null"/> when none does.</summary>
    /// <param name="moment">The moment (UTC).</param>
    /// <returns>The candle.</returns>
    public ChartCandle? At(DateTime moment) =>
        Candles.FirstOrDefault(c => moment >= c.Start && moment < c.Start.AddSeconds(IntervalSeconds));
}
