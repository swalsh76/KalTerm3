using System.Globalization;
using KalTerm.Data.Alerts;

namespace KalTerm.Ingest.Analytics;

/// <summary>
/// Judges every active alert against live data and records the ones that fire. It reads the raw ticker and trade rows of the markets that have alerts (not the table the metrics analyzer keeps, which is
/// minutes old), so its cost follows the number of alerts. An alert whose data is missing or overlaps a gap is skipped, not judged. The runner raises the analyzer's change counter when something fired.
/// </summary>
/// <param name="store">Reads the alerts and records what happened.</param>
/// <param name="reader">Reads what can be seen of the markets.</param>
/// <param name="interval">How often it runs; the default is 10 seconds.</param>
public sealed class AlertAnalyzer(IAlertEngineStore store, IAlertDataReader reader, TimeSpan? interval = null) : IAnalyzer
{
    /// <summary>The analyzer's name.</summary>
    public const string AnalyzerName = "alerts";

    /// <summary>How often the analyzer runs unless told otherwise.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public string Name => AnalyzerName;

    /// <inheritdoc/>
    public TimeSpan Interval { get; } = interval ?? DefaultInterval;

    /// <inheritdoc/>
    public async Task<AnalyzerOutcome> RunAsync(AnalysisContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var alerts = await store.ActiveAsync(cancellationToken).ConfigureAwait(false);
        if (alerts.Count == 0)
        {
            return new AnalyzerOutcome(0, false);
        }

        var seen = await reader.ReadAsync([.. alerts.Select(a => a.MarketId).Distinct()], context.Now, cancellationToken).ConfigureAwait(false);
        var updates = new List<AlertUpdate>();
        var skipped = 0;
        foreach (var alert in alerts)
        {
            var verdict = seen.TryGetValue(alert.MarketId, out var observation)
                ? AlertRules.Judge(alert.Kind, alert.Threshold, observation)
                : new AlertVerdict(AlertCondition.Unknown, 0);
            if (verdict.Condition == AlertCondition.Unknown)
            {
                skipped++;
                continue;
            }

            var step = AlertRules.Step(alert.State, verdict, context.Now, alert.CooldownSeconds);
            updates.Add(new AlertUpdate(alert, step.State, step.Fire ? verdict.Value : null));
        }

        var fired = await store.ApplyAsync(updates, context.Now, cancellationToken).ConfigureAwait(false);
        return new AnalyzerOutcome(alerts.Count, fired > 0, string.Create(CultureInfo.InvariantCulture, $"{updates.Count} judged, {skipped} skipped, {fired} fired"));
    }
}
