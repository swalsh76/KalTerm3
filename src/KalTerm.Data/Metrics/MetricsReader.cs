using Dapper;
using KalTerm.Data.Candles;
using KalTerm.Data.Reading;

namespace KalTerm.Data.Metrics;

/// <summary>Reads what the metrics of a market are made of.</summary>
public interface IMetricsReader
{
    /// <summary>Gathers the inputs of one market's metrics at a moment.</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="asOf">The moment (UTC); trades and candles after it are ignored.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The inputs, or <see langword="null"/> when there is no such market.</returns>
    Task<MetricsInputs?> ReadAsync(string ticker, DateTime asOf, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IMetricsReader"/> over the ticker, trade, candle and gap tables. The latest ticker update is looked for within the reader lookback (7 days). The 24 hours of trades come from the raw
/// trade rows of that one market (a seek on market and time), so the volume and VWAP do not wait for the candle job; the averages and volatility come from the stored hourly candles.
/// </summary>
/// <param name="source">Opens connections.</param>
public sealed class MetricsReader(DataSource source) : IMetricsReader
{
    private static readonly TimeSpan HourlyLookback = TimeSpan.FromDays(30);

    private readonly CandleReader _candles = new(source);

    /// <inheritdoc/>
    public async Task<MetricsInputs?> ReadAsync(string ticker, DateTime asOf, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        var options = new ReaderOptions();
        var from = asOf - MetricsInputsBuilder.Window;
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await ReaderSupport.MarketIdAsync(connection, ticker, options, cancellationToken).ConfigureAwait(false) is not { } marketId)
        {
            return null;
        }

        var tick = await connection.QueryFirstOrDefaultAsync<TickRow>(ReaderSupport.Command(
            "SELECT TOP (1) ts AS Ts, price_e6 AS PriceE6, yes_bid_e6 AS YesBidE6, yes_ask_e6 AS YesAskE6, open_interest_e2 AS OpenInterestE2 FROM dbo.ticker WHERE market_id = @marketId AND ts >= @floor AND ts <= @asOf ORDER BY ts DESC",
            new { marketId, floor = asOf - options.LatestLookback, asOf },
            options,
            cancellationToken)).ConfigureAwait(false);
        var trades = await connection.QuerySingleAsync<TradeSums>(ReaderSupport.Command(
            "SELECT COUNT_BIG(*) AS Count, ISNULL(SUM(CAST(count_e2 AS decimal(38, 0))), 0) AS Volume, ISNULL(SUM(CAST(yes_price_e6 AS decimal(38, 0)) * count_e2), 0) AS PriceTimesCount FROM dbo.trade WHERE market_id = @marketId AND ts > @from AND ts <= @asOf",
            new { marketId, from, asOf },
            options,
            cancellationToken)).ConfigureAwait(false);
        var gapsInWindow = await connection.ExecuteScalarAsync<int>(ReaderSupport.Command(
            "SELECT COUNT(*) FROM dbo.ingest_gap WHERE started_at < @asOf AND (ended_at IS NULL OR ended_at > @from) AND (market_id IS NULL OR market_id = @marketId)",
            new { marketId, from, asOf },
            options,
            cancellationToken)).ConfigureAwait(false);

        var hourly = await _candles.ReadAsync(marketId, MetricsInputsBuilder.HourSeconds, asOf - HourlyLookback, asOf, int.Min(CandleReader.MaxRows, (int)(HourlyLookback.TotalHours + 1)), cancellationToken).ConfigureAwait(false);
        return MetricsInputsBuilder.Build(
            asOf,
            tick is null ? null : new MetricsTick(tick.Ts, tick.PriceE6, tick.YesBidE6, tick.YesAskE6, tick.OpenInterestE2),
            new MetricsTradeSums(trades.Count, trades.Volume, trades.PriceTimesCount),
            gapsInWindow > 0,
            hourly.Select(c => new HourlyClose(c.BucketStart, c.CloseE6, c.Partial)));
    }

    private sealed record TickRow(DateTime Ts, long? PriceE6, long? YesBidE6, long? YesAskE6, long? OpenInterestE2);

    private sealed record TradeSums(long Count, decimal Volume, decimal PriceTimesCount);
}
