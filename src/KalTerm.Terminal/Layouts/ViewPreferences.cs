namespace KalTerm.Terminal.Layouts;

/// <summary>
/// What a market's chart and book start with, shared by every market opened: the last interval and range chosen on a chart and the last depth chosen on a book. A layout restores them; choosing them in a chart or a book
/// changes them, so the next market opens the way the last one was left.
/// </summary>
internal sealed class ViewPreferences
{
    /// <summary>The preferences changed.</summary>
    public event Action? Changed;

    /// <summary>The chart's interval text (<c>1h</c>), or <see langword="null"/> for the chart's own default.</summary>
    public string? ChartInterval { get; private set; }

    /// <summary>The chart's range text (<c>1 week</c>), or <see langword="null"/> for the interval's own default.</summary>
    public string? ChartRange { get; private set; }

    /// <summary>The book's depth, or <see langword="null"/> for the book's own default.</summary>
    public int? BookDepth { get; private set; }

    /// <summary>Remembers the chart's choices.</summary>
    /// <param name="interval">The interval text.</param>
    /// <param name="range">The range text.</param>
    public void SetChart(string? interval, string? range)
    {
        if (ChartInterval == interval && ChartRange == range)
        {
            return;
        }

        ChartInterval = interval;
        ChartRange = range;
        Changed?.Invoke();
    }

    /// <summary>Remembers the book's depth.</summary>
    /// <param name="depth">The depth.</param>
    public void SetBookDepth(int? depth)
    {
        if (BookDepth == depth)
        {
            return;
        }

        BookDepth = depth;
        Changed?.Invoke();
    }

    /// <summary>Takes what a layout holds, without announcing it as a change.</summary>
    /// <param name="layout">The chart part of a layout.</param>
    public void Apply(ChartLayout? layout)
    {
        if (layout is null)
        {
            return;
        }

        ChartInterval = layout.Interval;
        ChartRange = layout.Range;
        BookDepth = layout.BookDepth;
    }

    /// <summary>What a layout would hold.</summary>
    /// <returns>The chart part.</returns>
    public ChartLayout Capture() => new(ChartInterval, ChartRange, BookDepth);
}
