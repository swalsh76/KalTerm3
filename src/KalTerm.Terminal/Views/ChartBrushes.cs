using System.Windows.Media;
using KalTerm.Terminal.Markets;

namespace KalTerm.Terminal.Views;

/// <summary>The chart's colors (<see cref="ChartColors"/>, the same ones the chart is drawn with) as brushes the legend can use.</summary>
internal static class ChartBrushes
{
    public static Brush Rising { get; } = Make(ChartColors.Rising);

    public static Brush Falling { get; } = Make(ChartColors.Falling);

    public static Brush Gap { get; } = Make(ChartColors.Gap);

    public static Brush Volume { get; } = Make(ChartColors.Volume);

    public static Brush LastPrice { get; } = Make(ChartColors.LastPrice);

    private static SolidColorBrush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
