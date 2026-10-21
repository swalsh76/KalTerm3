namespace KalTerm.Terminal.Markets;

/// <summary>
/// What a market is called on screen. The event's title comes first ("Philadelphia vs New York"), then the market's own ("Philadelphia wins"), then the yes label, and the ticker when nothing else is known yet (a
/// market whose titles discovery has not stored). The second line says which market of the event it is; the ticker is the last resort and always has its own place.
/// </summary>
internal static class MarketNames
{
    /// <summary>The main name.</summary>
    /// <param name="eventTitle">The event's title, or empty.</param>
    /// <param name="title">The market's title, or empty.</param>
    /// <param name="yesLabel">The yes side's label, or empty.</param>
    /// <param name="ticker">The ticker.</param>
    /// <returns>The first of those that is not empty.</returns>
    public static string Name(string eventTitle, string title, string yesLabel, string ticker) =>
        eventTitle.Length > 0 ? eventTitle : title.Length > 0 ? title : yesLabel.Length > 0 ? yesLabel : ticker;

    /// <summary>
    /// The line under the main name: the market's own title when the main name is the event's, so "Philadelphia vs New York" is followed by "Philadelphia wins". Empty when there is nothing more to say.
    /// </summary>
    /// <param name="eventTitle">The event's title, or empty.</param>
    /// <param name="title">The market's title, or empty.</param>
    /// <returns>The second line, or empty.</returns>
    public static string Under(string eventTitle, string title) =>
        eventTitle.Length > 0 && title.Length > 0 && !string.Equals(eventTitle, title, StringComparison.Ordinal) ? title : string.Empty;
}
