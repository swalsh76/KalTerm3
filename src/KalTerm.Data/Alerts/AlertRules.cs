namespace KalTerm.Data.Alerts;

/// <summary>What an alert watches for.</summary>
public enum AlertKind
{
    /// <summary>The last traded price is at or above the threshold (dollars).</summary>
    PriceAbove,

    /// <summary>The last traded price is at or below the threshold (dollars).</summary>
    PriceBelow,

    /// <summary>Ask minus bid is at or above the threshold (dollars).</summary>
    SpreadAbove,

    /// <summary>The last 15 minutes of volume are at least the threshold times the average 15 minutes of the 24 hours before.</summary>
    VolumeSpike,

    /// <summary>Open interest moved by at least the threshold (contracts), up or down, over the last hour.</summary>
    OiChange,
}

/// <summary>The names alert kinds have on the wire and in the database, and what thresholds make sense.</summary>
public static class AlertKinds
{
    /// <summary>The length of the recent window of a volume spike.</summary>
    public static readonly TimeSpan SpikeWindow = TimeSpan.FromMinutes(15);

    /// <summary>The length of the baseline of a volume spike (the 24 hours before the recent window), in recent windows.</summary>
    public const int SpikeBaselineWindows = 96;

    /// <summary>The window over which open interest is compared.</summary>
    public static readonly TimeSpan OiWindow = TimeSpan.FromHours(1);

    /// <summary>The shortest and longest cooldown, in seconds.</summary>
    public const int MinCooldownSeconds = 10;

    /// <summary>The longest cooldown, in seconds (a day).</summary>
    public const int MaxCooldownSeconds = 86_400;

    /// <summary>The cooldown an alert gets when none is given: a minute.</summary>
    public const int DefaultCooldownSeconds = 60;

    /// <summary>The wire name of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The name.</returns>
    public static string Name(AlertKind kind) => kind switch
    {
        AlertKind.PriceAbove => "price_above",
        AlertKind.PriceBelow => "price_below",
        AlertKind.SpreadAbove => "spread_above",
        AlertKind.VolumeSpike => "volume_spike",
        AlertKind.OiChange => "oi_change",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Reads a wire name.</summary>
    /// <param name="name">The name.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether the name is a kind.</returns>
    public static bool TryParse(string? name, out AlertKind kind)
    {
        foreach (var candidate in Enum.GetValues<AlertKind>())
        {
            if (string.Equals(name, Name(candidate), StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>Whether a threshold is usable for a kind: a price strictly between 0 and 1 dollar, a spread up to 1 dollar, a spike factor of 2 to 1,000, an open-interest move of at least a contract.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="threshold">The threshold in the kind's unit (dollars at 10^-6, a whole factor, or contracts at 10^-2).</param>
    /// <returns>Whether it is usable.</returns>
    public static bool IsValidThreshold(AlertKind kind, long threshold) => kind switch
    {
        AlertKind.PriceAbove or AlertKind.PriceBelow => threshold is >= 1 and <= 999_999,
        AlertKind.SpreadAbove => threshold is >= 1 and <= 1_000_000,
        AlertKind.VolumeSpike => threshold is >= 2 and <= 1_000,
        AlertKind.OiChange => threshold is >= 100 and <= 1_000_000_000_000,
        _ => false,
    };
}

/// <summary>What was seen of one market at one moment, for the alert kinds that need it.</summary>
/// <param name="LastE6">The last traded price.</param>
/// <param name="SpreadE6">Ask minus bid at the latest ticker update.</param>
/// <param name="RecentVolumeE2">Contracts traded in the last 15 minutes.</param>
/// <param name="BaselineVolumeE2">Contracts traded in the 24 hours before those 15 minutes.</param>
/// <param name="OpenInterestE2">The open interest now.</param>
/// <param name="OpenInterestHourAgoE2">The open interest an hour ago.</param>
/// <param name="PricePartial">A gap overlaps the last minute, so the price and the spread are not to be trusted.</param>
/// <param name="VolumePartial">A gap overlaps the recent window.</param>
/// <param name="OiPartial">A gap overlaps the last hour.</param>
public sealed record AlertObservation(
    long? LastE6,
    long? SpreadE6,
    long RecentVolumeE2,
    long BaselineVolumeE2,
    long? OpenInterestE2,
    long? OpenInterestHourAgoE2,
    bool PricePartial,
    bool VolumePartial,
    bool OiPartial);

/// <summary>Whether an alert's condition holds.</summary>
public enum AlertCondition
{
    /// <summary>The data to judge it is missing or incomplete; nothing is decided.</summary>
    Unknown,

    /// <summary>The condition does not hold.</summary>
    NotMet,

    /// <summary>The condition holds.</summary>
    Met,
}

/// <summary>The outcome of judging an alert.</summary>
/// <param name="Condition">Whether it holds.</param>
/// <param name="Value">What was observed, in the unit of the kind (for a spike, the whole factor).</param>
public readonly record struct AlertVerdict(AlertCondition Condition, long Value);

/// <summary>An alert's memory between evaluations.</summary>
/// <param name="Armed">Not yet evaluated (<see langword="null"/>), ready to fire (true) or fired and waiting for the condition to clear (false).</param>
/// <param name="LastFiredAt">When it last fired.</param>
public readonly record struct AlertState(bool? Armed, DateTime? LastFiredAt);

/// <summary>What an evaluation does to an alert.</summary>
/// <param name="Fire">Whether to record a fire now.</param>
/// <param name="State">The new state.</param>
public readonly record struct AlertStep(bool Fire, AlertState State);

/// <summary>The judgment of alerts and the rule for when one fires, with no database.</summary>
public static class AlertRules
{
    /// <summary>Judges one alert against what was seen. Data that is missing or incomplete decides nothing.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="threshold">The threshold.</param>
    /// <param name="seen">What was seen.</param>
    /// <returns>The verdict.</returns>
    public static AlertVerdict Judge(AlertKind kind, long threshold, AlertObservation seen)
    {
        ArgumentNullException.ThrowIfNull(seen);
        switch (kind)
        {
            case AlertKind.PriceAbove or AlertKind.PriceBelow:
                if (seen.PricePartial || seen.LastE6 is not { } last)
                {
                    return new AlertVerdict(AlertCondition.Unknown, 0);
                }

                return Verdict(kind == AlertKind.PriceAbove ? last >= threshold : last <= threshold, last);
            case AlertKind.SpreadAbove:
                if (seen.PricePartial || seen.SpreadE6 is not { } spread)
                {
                    return new AlertVerdict(AlertCondition.Unknown, 0);
                }

                return Verdict(spread >= threshold, spread);
            case AlertKind.VolumeSpike:
                if (seen.VolumePartial || seen.BaselineVolumeE2 <= 0)
                {
                    return new AlertVerdict(AlertCondition.Unknown, 0);
                }

                // recent * 96 is the recent volume as if it lasted a whole day of 15-minute windows; the baseline is that day's total.
                var scaled = (decimal)seen.RecentVolumeE2 * AlertKinds.SpikeBaselineWindows;
                return Verdict(scaled >= (decimal)threshold * seen.BaselineVolumeE2, (long)Math.Floor(scaled / seen.BaselineVolumeE2));
            case AlertKind.OiChange:
                if (seen.OiPartial || seen.OpenInterestE2 is not { } now || seen.OpenInterestHourAgoE2 is not { } before)
                {
                    return new AlertVerdict(AlertCondition.Unknown, 0);
                }

                return Verdict(Math.Abs(now - before) >= threshold, now - before);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>
    /// Decides what an evaluation does. The first evaluation of an alert only records whether its condition already holds (so nothing fires for what was true when it was made). After that an alert
    /// fires when its condition holds while it is ready, no sooner than its cooldown after the last fire, and is ready again once the condition has cleared. An unknown verdict changes nothing.
    /// </summary>
    /// <param name="state">The alert's memory.</param>
    /// <param name="verdict">The verdict of this evaluation.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cooldownSeconds">The cooldown.</param>
    /// <returns>What to do and the new memory.</returns>
    public static AlertStep Step(AlertState state, AlertVerdict verdict, DateTime now, int cooldownSeconds)
    {
        if (verdict.Condition == AlertCondition.Unknown)
        {
            return new AlertStep(false, state);
        }

        var met = verdict.Condition == AlertCondition.Met;
        if (state.Armed is null)
        {
            return new AlertStep(false, state with { Armed = !met });
        }

        if (state.Armed == false)
        {
            return new AlertStep(false, met ? state : state with { Armed = true });
        }

        if (!met)
        {
            return new AlertStep(false, state);
        }

        var cooled = state.LastFiredAt is not { } last || now - last >= TimeSpan.FromSeconds(cooldownSeconds);
        return cooled ? new AlertStep(true, new AlertState(false, now)) : new AlertStep(false, state);
    }

    private static AlertVerdict Verdict(bool met, long value) => new(met ? AlertCondition.Met : AlertCondition.NotMet, value);
}
