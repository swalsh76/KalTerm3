using System.Globalization;
using System.Text.RegularExpressions;

namespace KalTerm.Terminal.Alerts;

/// <summary>A kind of alert the person can make: its wire name, its words, the unit its threshold is in, and what a usable threshold looks like (the server judges again; this saves a round trip).</summary>
internal sealed partial class AlertKindChoice
{
    private readonly Func<string, bool> _usable;

    private AlertKindChoice(string key, string unit, string hint, Func<string, bool> usable)
    {
        Key = key;
        Text = AlertText.Kind(key);
        Unit = unit;
        Hint = hint;
        _usable = usable;
    }

    /// <summary>Every kind, in the order the box shows them.</summary>
    public static IReadOnlyList<AlertKindChoice> All { get; } =
    [
        new("price_above", "dollars", "A price above 0 and below 1 dollar, for example 0.60. Fires when the last trade is at or above it.", Price),
        new("price_below", "dollars", "A price above 0 and below 1 dollar, for example 0.40. Fires when the last trade is at or below it.", Price),
        new("spread_above", "dollars", "A spread of up to 1 dollar, for example 0.05. Fires when ask minus bid is at or above it.", Spread),
        new("volume_spike", "times", "A whole factor from 2 to 1000, for example 5. Fires when the last 15 minutes trade at least that many times the average 15 minutes of the day before.", Factor),
        new("oi_change", "contracts", "Contracts, at least 1, for example 500. Fires when open interest moved by that much, up or down, over an hour.", Contracts),
    ];

    /// <summary>The wire name.</summary>
    public string Key { get; }

    /// <summary>The kind in words.</summary>
    public string Text { get; }

    /// <summary>The unit of the threshold.</summary>
    public string Unit { get; }

    /// <summary>What a usable threshold is, in a sentence.</summary>
    public string Hint { get; }

    /// <summary>What is wrong with a threshold, or <see langword="null"/> when it will do.</summary>
    /// <param name="text">The threshold as typed.</param>
    /// <returns>A sentence, or <see langword="null"/>.</returns>
    public string? Problem(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? "Type a threshold." : _usable(trimmed) ? null : Hint;
    }

    private static bool Price(string text) => Decimal(text, 6) is { } value && value > 0 && value < 1;

    private static bool Spread(string text) => Decimal(text, 6) is { } value && value > 0 && value <= 1;

    private static bool Factor(string text) => WholeNumber().IsMatch(text) && int.Parse(text, CultureInfo.InvariantCulture) is >= 2 and <= 1000;

    private static bool Contracts(string text) => Decimal(text, 2) is { } value && value >= 1 && value < 1_000_000_000m;

    private static decimal? Decimal(string text, int places) =>
        DecimalShape().Match(text) is { Success: true } match && match.Groups["fraction"].Length <= places && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;

    [GeneratedRegex(@"^[0-9]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeNumber();

    [GeneratedRegex(@"^(?<whole>[0-9]{0,10})(\.(?<fraction>[0-9]{1,6}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalShape();
}
