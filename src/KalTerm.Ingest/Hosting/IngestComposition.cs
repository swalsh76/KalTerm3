using KalTerm.Backup;
using KalTerm.Data;
using KalTerm.Data.Books;
using KalTerm.Data.Gaps;
using KalTerm.Data.Governor;
using KalTerm.Data.Markets;
using KalTerm.Data.State;
using KalTerm.Data.Writing;
using KalTerm.Ingest.Analytics;
using KalTerm.Ingest.Backfill;
using KalTerm.Ingest.Books;
using KalTerm.Ingest.Candles;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;
using Microsoft.Extensions.Logging;

namespace KalTerm.Ingest.Hosting;

/// <summary>Pieces a test replaces so the whole composition runs without Kalshi.</summary>
/// <param name="Source">The stream source; the real one needs signing credentials.</param>
/// <param name="ListOpenMarkets">The full market listing.</param>
/// <param name="ListUpdatedSince">The incremental market listing.</param>
/// <param name="Runner">Runner tuning.</param>
/// <param name="Time">The clock.</param>
/// <param name="OrderBookRest">The REST order book fallback.</param>
/// <param name="Keeper">Tuning of the order book keeper.</param>
internal sealed record IngestOverrides(
    IStreamSource? Source = null,
    Func<CancellationToken, IAsyncEnumerable<Market>>? ListOpenMarkets = null,
    Func<DateTimeOffset, CancellationToken, IAsyncEnumerable<Market>>? ListUpdatedSince = null,
    IngestRunnerOptions? Runner = null,
    TimeProvider? Time = null,
    IOrderBookRest? OrderBookRest = null,
    OrderBookKeeperOptions? Keeper = null,
    ITradeSource? Trades = null,
    BackfillOptions? Backfill = null,
    IBackupJob? Backup = null,
    ICandleJob? Candles = null,
    IReadOnlyList<IAnalyzerRunner>? Analyzers = null);

/// <summary>Builds the real ingest process from its settings and runs it: database ready, then the <see cref="IngestRunner"/> over real storage and the Kalshi feed.</summary>
public static class IngestComposition
{
    /// <summary>Prepares the database, then runs until <paramref name="cancellationToken"/> is cancelled and shuts down in order.</summary>
    /// <param name="options">The host settings.</param>
    /// <param name="settings">Kalshi environment (demo by default) and credentials (needed for the WebSocket).</param>
    /// <param name="loggerFactory">Creates the loggers.</param>
    /// <param name="cancellationToken">Asks the process to stop.</param>
    /// <returns>A task that completes when the shutdown is done.</returns>
    public static Task RunAsync(IngestHostOptions options, KalshiSettings settings, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        RunAsync(options, settings, loggerFactory, null, cancellationToken);

    internal static async Task RunAsync(IngestHostOptions options, KalshiSettings settings, ILoggerFactory loggerFactory, IngestOverrides? overrides, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        await DatabasePreparer.PrepareAsync(options, loggerFactory.CreateLogger(typeof(DatabasePreparer).FullName!), cancellationToken).ConfigureAwait(false);

        var time = overrides?.Time ?? TimeProvider.System;
        var data = new DataSource(options.ToConnectionOptions());
        var store = new MarketStore(data);
        var ids = new MarketIdMap();
        KalshiSigner? signer = null;
        KalshiRestClient? rest = null;
        try
        {
            if (settings.KeyId is not null && settings.PrivateKeyPath is not null)
            {
                signer = KalshiSigner.FromSettings(settings);
            }

            var listOpen = overrides?.ListOpenMarkets;
            var listUpdated = overrides?.ListUpdatedSince;
            var keepBooks = options.WatchList.Length > 0;
            if (listOpen is null || listUpdated is null || (keepBooks && overrides?.OrderBookRest is null) || (options.BackfillEnabled && overrides?.Trades is null))
            {
                rest = new KalshiRestClient(settings, new KalshiRestClientOptions { Signer = signer, Logger = loggerFactory.CreateLogger<KalshiRestClient>() });
                var client = rest;
                listOpen ??= ct => client.IterateOpenMarketsAsync(ct);
                listUpdated ??= (since, ct) => client.IterateMarketsUpdatedSinceAsync(since, ct);
            }

            OrderBookKeeper? keeper = keepBooks
                ? new OrderBookKeeper(options.WatchList, overrides?.OrderBookRest ?? new RestOrderBook(rest!), new BookStateStore(data), ids, overrides?.Keeper, time, loggerFactory.CreateLogger<OrderBookKeeper>())
                : null;

            var source = overrides?.Source
                ?? new KalshiStreamSource(
                    settings,
                    signer ?? throw new AuthException($"{KalshiSettings.KeyIdVariable} and {KalshiSettings.PrivateKeyPathVariable} must both be set to read the WebSocket feed"),
                    new Kalshi.WebSocket.KalshiWebSocketOptions { Logger = loggerFactory.CreateLogger<Kalshi.WebSocket.KalshiWebSocket>() },
                    options.WatchList);

            var state = new IngestStateStore(data);
            var writerLogger = loggerFactory.CreateLogger(typeof(BulkWriter<>).Namespace!);
            var tradeWriter = Writers.Trade(data, null, time, writerLogger);
            var backfiller = options.BackfillEnabled
                ? new TradeBackfiller(new BackfillStore(data), overrides?.Trades ?? new RestTradeSource(rest!), tradeWriter, ids, overrides?.Backfill, time, loggerFactory.CreateLogger<TradeBackfiller>())
                : null;
            var backup = overrides?.Backup ?? CreateBackupJob(options, data, state, time, loggerFactory);
            var candleRules = options.ToCandleRules();
            var candles = overrides?.Candles ?? (candleRules.Count == 0
                ? null
                : new CandleJob(new Data.Candles.CandleBuilder(data), new Data.Candles.CandleJobStore(data), candleRules, CandleJob.WatchedIds(options.WatchList, ids), null, time, loggerFactory.CreateLogger<CandleJob>()));
            var analyzers = overrides?.Analyzers ?? (options.Analytics
                ? new IAnalyzerRunner[]
                {
                    // Two runners, each in a loop of its own: the alerts must not wait for the minutes-long refresh of the metrics table.
                    new AnalyzerRunner([new AlertAnalyzer(new Data.Alerts.AlertEngineStore(data), new Data.Alerts.AlertDataReader(data))], new AnalyzerStore(data), null, time, loggerFactory.CreateLogger<AnalyzerRunner>()),
                    new AnalyzerRunner([new MarketMetricsAnalyzer(new Data.Metrics.MetricsBatchReader(data), new Data.Metrics.MarketMetricsStore(data))], new AnalyzerStore(data), null, time, loggerFactory.CreateLogger<AnalyzerRunner>()),
                }
                : null);
            var runner = new IngestRunner(
                store,
                ids,
                new MarketDiscovery(store, ids, listOpen, time, loggerFactory.CreateLogger<MarketDiscovery>(), listUpdatedSince: listUpdated),
                source,
                Writers.Ticker(data, null, time, writerLogger),
                tradeWriter,
                new GapStore(data),
                state,
                new PartitionMaintenance(data),
                overrides?.Runner ?? new IngestRunnerOptions { PartitionDaysAhead = options.PartitionDaysAhead },
                null,
                time,
                loggerFactory.CreateLogger<IngestRunner>(),
                keeper,
                new StorageGovernor(new GovernorStore(data), state, new GovernorOptions { RawRetentionDays = options.RetentionDays }, time, loggerFactory.CreateLogger<StorageGovernor>()),
                backfiller,
                backup,
                candles,
                analyzers);
            try
            {
                await runner.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                keeper?.Dispose();
            }
        }
        finally
        {
            rest?.Dispose();
            signer?.Dispose();
        }
    }

    /// <summary>The daily backup job, or <see langword="null"/> when no backup target is configured.</summary>
    private static BackupSchedule? CreateBackupJob(IngestHostOptions options, DataSource data, IIngestState state, TimeProvider time, ILoggerFactory loggerFactory)
    {
        if (options.ToBackupOptions() is not { } backupOptions)
        {
            return null;
        }

        var database = new SqlBackupDatabase(data, options.Database);
        var credentials = new ShareCredentialStore(options.BackupCredentialPath);
        var connector = new Win32ShareConnector();
        return new BackupSchedule(
            new BackupService(database, state, backupOptions, connector, credentials.Load, time, loggerFactory.CreateLogger<BackupService>()),
            new RestoreService(database, state, backupOptions, connector, credentials.Load, time, loggerFactory.CreateLogger<RestoreService>()),
            state,
            backupOptions,
            time,
            loggerFactory.CreateLogger<BackupSchedule>());
    }
}
