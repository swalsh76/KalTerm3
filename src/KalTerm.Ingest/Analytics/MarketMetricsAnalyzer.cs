using System.Globalization;
using KalTerm.Data.Metrics;

namespace KalTerm.Ingest.Analytics;

/// <summary>
/// Keeps <c>dbo.market_metrics</c> current: the metrics of every market with a ticker update in the last hour, a trade in the last 24 hours or an hourly candle in the last 25 hours. The set-based reader
/// fetches the sums in batches, <see cref="MetricsCalculator"/> does the arithmetic (the same code as the per-market endpoint), and the store writes only the rows whose figures changed. A market that
/// has dropped out of the universe loses its row, but only after a pass that completed, so a failed pass never empties the table.
/// </summary>
/// <param name="reader">Reads the inputs of all active markets.</param>
/// <param name="store">Writes the result table.</param>
/// <param name="interval">How often it runs; the default is 5 minutes.</param>
public sealed class MarketMetricsAnalyzer(IMetricsBatchReader reader, IMarketMetricsStore store, TimeSpan? interval = null) : IAnalyzer
{
    /// <summary>The analyzer's name.</summary>
    public const string AnalyzerName = "market_metrics";

    /// <summary>How often the analyzer runs unless told otherwise.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    public string Name => AnalyzerName;

    /// <inheritdoc/>
    public TimeSpan Interval { get; } = interval ?? DefaultInterval;

    /// <inheritdoc/>
    public async Task<AnalyzerOutcome> RunAsync(AnalysisContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var seen = new HashSet<int>();
        var written = 0;
        await reader.ReadAsync(
            context.Now,
            async batch =>
            {
                var rows = new List<(int, MarketMetrics)>(batch.Count);
                foreach (var market in batch)
                {
                    rows.Add((market.MarketId, MetricsCalculator.Compute(market.Inputs)));
                    seen.Add(market.MarketId);
                }

                written += await store.UpsertAsync(rows, context.Now, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        var removed = await store.DeleteOthersAsync(seen, cancellationToken).ConfigureAwait(false);
        return new AnalyzerOutcome(seen.Count, written > 0 || removed > 0, string.Create(CultureInfo.InvariantCulture, $"{written} written, {removed} removed"));
    }
}
