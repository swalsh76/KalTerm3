using System.Globalization;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Alerts;

/// <summary>Alerts in words: what a kind is called and how a threshold or an observed value reads in the unit of its kind (dollars, a factor, contracts).</summary>
internal static class AlertText
{
    /// <summary>The kind in words.</summary>
    /// <param name="kind">The wire name.</param>
    /// <returns>For example <c>price above</c>; an unknown kind is returned as it is.</returns>
    public static string Kind(string kind) => kind switch
    {
        "price_above" => "price above",
        "price_below" => "price below",
        "spread_above" => "spread above",
        "volume_spike" => "volume spike",
        "oi_change" => "open interest change",
        _ => kind,
    };

    /// <summary>A threshold or a value in the unit of the kind.</summary>
    /// <param name="kind">The wire name.</param>
    /// <param name="amount">The exact decimal text the server sent.</param>
    /// <returns>Dollars (<c>0.60</c>), a factor (<c>×5</c>) or contracts (<c>1,500</c>).</returns>
    public static string Amount(string kind, string amount) => kind switch
    {
        "volume_spike" => decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var factor) ? "×" + factor.ToString("0.##", CultureInfo.InvariantCulture) : amount,
        "oi_change" => Formatting.Count(amount),
        _ => Formatting.Dollars(amount),
    };

    /// <summary>The condition: <c>price above 0.60</c>.</summary>
    /// <param name="kind">The wire name.</param>
    /// <param name="threshold">The threshold text.</param>
    /// <returns>The words.</returns>
    public static string Condition(string kind, string threshold) => $"{Kind(kind)} {Amount(kind, threshold)}";
}
