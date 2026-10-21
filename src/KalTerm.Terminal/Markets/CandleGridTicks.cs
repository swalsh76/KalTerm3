using ScottPlot;
using ScottPlot.TickGenerators;

namespace KalTerm.Terminal.Markets;

/// <summary>
/// The time axis of a candle chart: the usual readable labels (and their stronger lines), plus a fainter vertical line at the start of every candle, so a 1-hour chart has a line for every hour and not only for the
/// hours the labels happen to fall on. The candle lines are left out when the candles are closer than <see cref="MinimumSpacing"/> pixels, where they would only be a grey wall.
/// </summary>
/// <param name="origin">Where a candle starts (any one of them), as the plot's time number.</param>
/// <param name="step">How long a candle is, in days.</param>
internal sealed class CandleGridTicks(double origin, double step) : ITickGenerator
{
    /// <summary>The least distance between two candles, in pixels, for a line to be drawn at each.</summary>
    public const double MinimumSpacing = 5;

    private readonly DateTimeAutomatic _labels = new();

    public Tick[] Ticks { get; set; } = [];

    public int MaxTickCount { get; set; } = 10_000;

    public void Regenerate(CoordinateRange range, Edge edge, PixelLength size, Paint paint, LabelStyle labelStyle)
    {
        _labels.Regenerate(range, edge, size, paint, labelStyle);
        var ticks = new List<Tick>(_labels.Ticks);
        var visible = range.Span / step;
        if (visible > 0 && size.Length / visible >= MinimumSpacing && visible <= MaxTickCount)
        {
            var first = Math.Ceiling((range.Min - origin) / step);
            var last = Math.Floor((range.Max - origin) / step);
            var near = step * 0.01;
            for (var i = first; i <= last; i++)
            {
                var position = origin + (i * step);
                if (!ticks.Exists(t => Math.Abs(t.Position - position) < near))
                {
                    ticks.Add(Tick.Minor(position));
                }
            }
        }

        Ticks = [.. ticks];
    }
}
