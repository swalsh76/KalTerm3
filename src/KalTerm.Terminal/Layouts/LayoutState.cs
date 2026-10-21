using System.Text.Json;
using System.Text.Json.Serialization;

namespace KalTerm.Terminal.Layouts;

/// <summary>Where the main window is. Pixels in device-independent units, as WPF has them; they are not money.</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <param name="Maximized">Whether the window is maximised (the other numbers are then where it returns to).</param>
internal sealed record WindowLayout(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>One column of the Markets grid.</summary>
/// <param name="Key">The column's header text.</param>
/// <param name="Width">Its width.</param>
/// <param name="Order">Its place among the columns, from 0.</param>
internal sealed record ColumnLayout(string Key, double Width, int Order);

/// <summary>What the Markets page remembers: the filters and the ranking (not the search text, which is a one-off question) and the grid's columns.</summary>
/// <param name="Status">The status filter: the status, or <c>any</c>.</param>
/// <param name="MinVolume">The minimum volume as typed.</param>
/// <param name="List">The watchlist chosen, or <see langword="null"/> for all markets.</param>
/// <param name="Sort">The ranking's key.</param>
/// <param name="Descending">Largest first.</param>
/// <param name="Columns">The grid's columns.</param>
internal sealed record MarketsLayout(string? Status, string? MinVolume, string? List, string? Sort, bool? Descending, IReadOnlyList<ColumnLayout>? Columns);

/// <summary>What a market's chart and book start with.</summary>
/// <param name="Interval">The candle interval, for example <c>1h</c>.</param>
/// <param name="Range">The range, for example <c>1 week</c>.</param>
/// <param name="BookDepth">How many levels of the book each side shows.</param>
internal sealed record ChartLayout(string? Interval, string? Range, int? BookDepth);

/// <summary>
/// A saved layout: how one user likes the terminal laid out. The server stores it as it is and never reads it, so this record can grow without a server change; <see cref="Version"/> is how a terminal tells a layout it
/// understands from one a newer terminal saved. A part that is absent is left as it is.
/// </summary>
/// <param name="Version">The layout's format; <see cref="Current"/> for what this terminal writes.</param>
/// <param name="Window">The main window.</param>
/// <param name="Page">The page open, by its title.</param>
/// <param name="Markets">The Markets page.</param>
/// <param name="Chart">The chart and book defaults.</param>
/// <param name="Sound">Whether alert toasts make a sound.</param>
internal sealed record LayoutState(int Version, WindowLayout? Window, string? Page, MarketsLayout? Markets, ChartLayout? Chart, bool? Sound)
{
    /// <summary>The format this terminal writes and understands.</summary>
    public const int Current = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The layout as the JSON the server stores.</summary>
    /// <returns>A JSON object.</returns>
    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, Options);

    /// <summary>Reads a layout back.</summary>
    /// <param name="body">The JSON the server returned.</param>
    /// <returns>The layout, or <see langword="null"/> when it is not one this terminal can read.</returns>
    public static LayoutState? FromJson(JsonElement body)
    {
        try
        {
            return body.ValueKind == JsonValueKind.Object ? body.Deserialize<LayoutState>(Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
