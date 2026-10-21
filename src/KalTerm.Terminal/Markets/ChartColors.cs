namespace KalTerm.Terminal.Markets;

/// <summary>The colors of the candle chart, in one place: <see cref="CandlePlotBuilder"/> draws with them and the legend under the chart shows the same ones, so the two cannot drift apart.</summary>
internal static class ChartColors
{
    /// <summary>A candle that closed at or above its open.</summary>
    public const string Rising = "#2E9E5B";

    /// <summary>A candle that closed below its open.</summary>
    public const string Falling = "#D64545";

    /// <summary>A candle a data gap overlaps (its body is this color, pale).</summary>
    public const string Gap = "#D49A1B";

    /// <summary>How pale a gap candle's body is (0 to 1).</summary>
    public const double GapOpacity = 0.3;

    /// <summary>The volume bars.</summary>
    public const string Volume = "#7C93B8";

    /// <summary>How pale the volume bars are (0 to 1).</summary>
    public const double VolumeOpacity = 0.55;

    /// <summary>The dashed last-price line.</summary>
    public const string LastPrice = "#6B6B6B";
}
