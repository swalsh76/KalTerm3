using System.Globalization;
using ScottPlot;

namespace KalTerm.Terminal.Markets;

/// <summary>
/// Draws a <see cref="ChartSeries"/> onto a plot: the candles (green when the close is at or above the open, red below), the candles a data gap overlaps pale amber, the volume as bars along the bottom on a
/// second axis, and the last price as a dashed line. It holds no state and touches no window, so a test can draw into a plot and look at what is in it, or render it to a picture.
/// </summary>
internal static class CandlePlotBuilder
{
    private static readonly Color Rising = Color.FromHex(ChartColors.Rising);
    private static readonly Color Falling = Color.FromHex(ChartColors.Falling);
    private static readonly Color Gap = Color.FromHex(ChartColors.Gap);
    private static readonly Color Volume = Color.FromHex(ChartColors.Volume);
    private static readonly Color LastLine = Color.FromHex(ChartColors.LastPrice);

    /// <summary>The share of the plot's height the tallest volume bar takes.</summary>
    private const double VolumeShare = 0.22;

    /// <summary>Clears the plot and draws the series on it.</summary>
    /// <param name="plot">The plot.</param>
    /// <param name="series">The candles.</param>
    /// <param name="zone">The time zone the time axis is shown in.</param>
    /// <param name="lastPrice">The last traded price (a dollar amount as text), drawn as a line; <see langword="null"/> for none.</param>
    public static void Draw(Plot plot, ChartSeries series, TimeZoneInfo zone, string? lastPrice)
    {
        ArgumentNullException.ThrowIfNull(plot);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(zone);
        plot.Clear();
        plot.Axes.DateTimeTicksBottom();
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = value => value.ToString("0.00##", CultureInfo.InvariantCulture) };
        plot.Axes.Right.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = value => value.ToString("N0", CultureInfo.InvariantCulture) };
        if (series.Candles.Count == 0)
        {
            plot.Axes.SetLimits(0, 1, 0, 1);
            return;
        }

        var span = TimeSpan.FromSeconds(series.IntervalSeconds);
        List<OHLC> whole = [];
        List<OHLC> partial = [];
        List<Bar> bars = [];
        foreach (var candle in series.Candles)
        {
            var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(candle.Start, DateTimeKind.Utc), zone);
            if (candle.HasPrice)
            {
                (candle.Partial ? partial : whole).Add(new OHLC(candle.Open!.Value, candle.High!.Value, candle.Low!.Value, candle.Close!.Value, start, span));
            }

            if (candle.Volume > 0)
            {
                bars.Add(new Bar { Position = start.ToOADate() + (span.TotalDays / 2), Value = candle.Volume, Size = span.TotalDays * 0.7, FillColor = Volume.WithAlpha(ChartColors.VolumeOpacity), LineColor = Colors.Transparent });
            }
        }

        if (bars.Count > 0)
        {
            var volume = plot.Add.Bars(bars);
            volume.Axes.YAxis = plot.Axes.Right;
        }

        if (whole.Count > 0)
        {
            var candles = plot.Add.Candlestick(whole);
            candles.RisingColor = Rising;
            candles.FallingColor = Falling;
        }

        if (partial.Count > 0)
        {
            var gaps = plot.Add.Candlestick(partial);
            gaps.RisingColor = Gap;
            gaps.FallingColor = Gap;
            gaps.RisingFillStyle.Color = Gap.WithAlpha(ChartColors.GapOpacity);   // the package draws no outline on a body, so "hollow" is a pale fill
            gaps.FallingFillStyle.Color = Gap.WithAlpha(ChartColors.GapOpacity);
            gaps.RisingLineStyle.Color = Gap;
            gaps.FallingLineStyle.Color = Gap;
            gaps.RisingLineStyle.Width = 1.5f;
            gaps.FallingLineStyle.Width = 1.5f;
        }

        if (lastPrice is not null && double.TryParse(lastPrice, NumberStyles.Float, CultureInfo.InvariantCulture, out var last))
        {
            plot.Add.HorizontalLine(last, 1, LastLine, LinePattern.Dashed);
        }

        SetLimits(plot, series, zone, span);
    }

    /// <summary>The view a chart starts with (and returns to): every candle, a little room around them, the price axis kept to the 0 to 1 dollars a yes price can take, and the volume bars kept to the bottom.</summary>
    private static void SetLimits(Plot plot, ChartSeries series, TimeZoneInfo zone, TimeSpan span)
    {
        var first = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(series.Candles[0].Start, DateTimeKind.Utc), zone).ToOADate();
        var last = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(series.Candles[^1].Start, DateTimeKind.Utc), zone).ToOADate() + span.TotalDays;
        var pad = span.TotalDays;
        var priced = series.Candles.Where(c => c.HasPrice).ToList();
        var low = priced.Count > 0 ? priced.Min(c => c.Low!.Value) : 0;
        var high = priced.Count > 0 ? priced.Max(c => c.High!.Value) : 1;
        var room = Math.Max(0.01, (high - low) * 0.08);
        var biggest = series.Candles.Max(c => c.Volume);
        plot.Axes.SetLimitsX(first - pad, last + pad);
        plot.Axes.SetLimitsY(Math.Max(0, low - room), Math.Min(1, high + room), plot.Axes.Left);
        plot.Axes.SetLimitsY(0, biggest > 0 ? biggest / VolumeShare : 1, plot.Axes.Right);
    }
}
