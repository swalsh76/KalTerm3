using KalTerm.Data;
using KalTerm.Data.Gaps;
using KalTerm.Data.State;
using KalTerm.Ingest.Hosting;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using static KalTerm.Ingest.Tests.IngestTestKit;
using static KalTerm.Ingest.Tests.StreamTestKit;

namespace KalTerm.Ingest.Tests;

/// <summary>Database preparation and the whole composition against a real database of their own, with the Kalshi feed replaced by a script.</summary>
public sealed class IngestCompositionTests : IAsyncLifetime
{
    private readonly string _database = LocalDbServer.NewDatabaseName();
    private readonly List<string> _files = [];
    private string? _unavailable;

    public async ValueTask InitializeAsync() => _unavailable = await LocalDbServer.ProbeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_unavailable is null)
        {
            await LocalDbServer.DropAsync(_database);
        }

        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private IngestHostOptions Options(int? provisionGb = 1) =>
        new() { Server = LocalDbServer.Server, Database = _database, ProvisionMaxSizeGb = provisionGb, PartitionDaysAhead = 2 };

    private DataSource Source() => new(Options().ToConnectionOptions());

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await Source().OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static IngestOverrides Overrides(IStreamSource source, KalTerm.Ingest.Backfill.ITradeSource? trades = null, KalTerm.Ingest.Backfill.BackfillOptions? backfill = null, TimeSpan? backupCheck = null) => new(
        source,
        _ => List([Open("E-1"), Open("E-2")]),
        (_, _) => List([]),
        new IngestRunnerOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            StreamRetryBase = TimeSpan.FromMilliseconds(10),
            StreamRetryMax = TimeSpan.FromMilliseconds(40),
            Schedule = now => (now.AddHours(1), PassKind.Full),
            BackfillInterval = TimeSpan.FromMilliseconds(50),
            BackupCheckInterval = backupCheck ?? TimeSpan.FromMinutes(1),
        },
        Trades: trades ?? new FakeTradeSource(),
        Backfill: backfill);

    private string KeyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "kalterm-test-" + Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(path, VectorFile.Load("ed25519.json").GetProperty("rawSign")[3].GetProperty("pkcs8Pem").GetString()!);
        _files.Add(path);
        return path;
    }

    [Fact]
    public async Task AMissingDatabaseIsAnErrorUnlessASizeWasGivenToCreateIt()
    {
        await Requirement.RequireLocalDbAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabasePreparer.PrepareAsync(Options(provisionGb: null), NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Contains("ProvisionMaxSizeGb", failure.Message, StringComparison.Ordinal);
        Assert.Null(await DatabaseProvisioner.ReadSettingsAsync(Options().ToConnectionOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreparingCreatesAndMigratesTheDatabaseAndRunningItAgainChangesNothing()
    {
        await Requirement.RequireLocalDbAsync();

        await DatabasePreparer.PrepareAsync(Options(), NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = 'ticker'"));
        var settings = await DatabaseProvisioner.ReadSettingsAsync(Options().ToConnectionOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(1024L * 1024 * 1024, settings!.MaxSizeBytes);

        await DatabasePreparer.PrepareAsync(Options(), NullLogger.Instance, TestContext.Current.CancellationToken);
        await DatabasePreparer.PrepareAsync(Options(provisionGb: null), NullLogger.Instance, TestContext.Current.CancellationToken);   // exists now: migrate only
    }

    [Fact]
    public async Task BadOptionsAndMissingPartsAreRefusedBeforeAnyWork()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => DatabasePreparer.PrepareAsync(null!, NullLogger.Instance, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => DatabasePreparer.PrepareAsync(new IngestHostOptions(), null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabasePreparer.PrepareAsync(new IngestHostOptions { Database = "" }, NullLogger.Instance, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => IngestComposition.RunAsync(null!, KalshiSettings.Default, NullLoggerFactory.Instance, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => IngestComposition.RunAsync(new IngestHostOptions(), null!, NullLoggerFactory.Instance, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => IngestComposition.RunAsync(new IngestHostOptions(), KalshiSettings.Default, null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithoutCredentialsTheRealStreamCannotBeBuiltAndTheErrorSaysWhich()
    {
        await Requirement.RequireLocalDbAsync();

        var failure = await Assert.ThrowsAsync<AuthException>(
            () => IngestComposition.RunAsync(Options(), KalshiSettings.Default, NullLoggerFactory.Instance, TestContext.Current.CancellationToken));

        Assert.Contains(KalshiSettings.KeyIdVariable, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunStoresWhatTheFeedSendsAndASecondRunLogsTheTimeBetween()
    {
        await Requirement.RequireLocalDbAsync();
        var settings = KalshiSettings.Create(KalshiEnvironment.Demo, "test-key", KeyFile());

        // Run 1: provisions and migrates the database, discovers the two markets, stores a ticker and a trade, and stops cleanly.
        var sessions = new List<ScriptedSession>();
        var source = new ScriptedSource(_ =>
        {
            var session = new ScriptedSession();
            lock (sessions)
            {
                sessions.Add(session);
            }

            return Task.FromResult<IStreamSession>(session);
        });
        using var stop = new CancellationTokenSource();
        var first = IngestComposition.RunAsync(Options(), settings, NullLoggerFactory.Instance, Overrides(source), stop.Token);
        await Wait.UntilAsync(() => source.Opens >= 1, "the first stream to open");
        ScriptedSession session1;
        lock (sessions)
        {
            session1 = sessions[0];
        }

        session1.Push(Message("ticker", """{"market_ticker":"E-1","price_dollars":"0.5600","volume_fp":"12.00"}""", DateTimeOffset.UtcNow));
        session1.Push(Trade("E-2", "trade-1", DateTimeOffset.UtcNow));
        await Wait.UntilAsync(() => ScalarAsync<int>("SELECT (SELECT COUNT(*) FROM dbo.ticker) + (SELECT COUNT(*) FROM dbo.trade)").GetAwaiter().GetResult() >= 2, "both rows to be stored");
        await stop.CancelAsync();
        await first;

        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.market WHERE ticker IN ('E-1', 'E-2')"));
        Assert.Equal(560_000L, await ScalarAsync<long>("SELECT price_e6 FROM dbo.ticker"));
        Assert.Equal("trade-1", await ScalarAsync<string>("SELECT trade_id FROM dbo.trade"));
        var state = await new IngestStateStore(Source()).GetAsync(IngestStateKeys.Run, TestContext.Current.CancellationToken);
        Assert.Equal(IngestStateKeys.Stopped, state!.Value);
        var governed = await new IngestStateStore(Source()).GetAsync(KalTerm.Data.Governor.GovernorStateKeys.Last, TestContext.Current.CancellationToken);
        Assert.StartsWith("ok ", governed!.Value, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.ingest_gap WHERE kind = '{GapKinds.IngestDown}'"));

        // Run 2: the time since the clean stop of run 1 is recorded as downtime.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var source2 = new ScriptedSource(_ => Task.FromResult<IStreamSession>(new ScriptedSession()));
        using var stop2 = new CancellationTokenSource();
        var second = IngestComposition.RunAsync(Options(), settings, NullLoggerFactory.Instance, Overrides(source2), stop2.Token);
        await Wait.UntilAsync(() => source2.Opens >= 1, "the second run's stream to open");
        await stop2.CancelAsync();
        await second;

        Assert.Equal(1, await ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.ingest_gap WHERE kind = '{GapKinds.IngestDown}' AND detail LIKE '%stopped cleanly%'"));
    }

    [Fact]
    public async Task AWatchListPublishesBuiltBooksAndMarksAMarketWithNoSnapshotYetAsStale()
    {
        await Requirement.RequireLocalDbAsync();
        var settings = KalshiSettings.Create(KalshiEnvironment.Demo, "test-key", KeyFile());
        var session = new ScriptedSession { OrderBookSid = 7 };
        var source = new ScriptedSource(_ => Task.FromResult<IStreamSession>(session));
        var overrides = Overrides(source) with
        {
            Runner = new IngestRunnerOptions
            {
                HeartbeatInterval = TimeSpan.FromMilliseconds(50),
                BookPublishInterval = TimeSpan.FromMilliseconds(50),
                Schedule = now => (now.AddHours(1), PassKind.Full),
            },
        };
        var options = Options();
        options.WatchList = ["E-1", "E-2"];
        using var stop = new CancellationTokenSource();

        var run = IngestComposition.RunAsync(options, settings, NullLoggerFactory.Instance, overrides, stop.Token);
        await Wait.UntilAsync(() => source.Opens >= 1, "the stream to open");
        session.Push(Message("orderbook_snapshot", """{"market_ticker":"E-1","yes_dollars_fp":[["0.4000","5.00"],["0.4100","1.50"]],"no_dollars_fp":[["0.5500","2.00"]]}""", DateTimeOffset.UtcNow, sid: 7, seq: 1));
        await Wait.UntilAsync(() => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.orderbook_state").GetAwaiter().GetResult() >= 2, "both books to be published");
        await Wait.UntilAsync(() => ScalarAsync<string>("SELECT yes_levels FROM dbo.orderbook_state s JOIN dbo.market m ON m.market_id = s.market_id WHERE m.ticker = 'E-1'").GetAwaiter().GetResult().Length > 0, "the snapshot to be published");
        await stop.CancelAsync();
        await run;

        Assert.Equal("410000:150;400000:500", await ScalarAsync<string>("SELECT yes_levels FROM dbo.orderbook_state s JOIN dbo.market m ON m.market_id = s.market_id WHERE m.ticker = 'E-1'"));
        Assert.Equal("550000:200", await ScalarAsync<string>("SELECT no_levels FROM dbo.orderbook_state s JOIN dbo.market m ON m.market_id = s.market_id WHERE m.ticker = 'E-1'"));
        Assert.False(await ScalarAsync<bool>("SELECT is_stale FROM dbo.orderbook_state s JOIN dbo.market m ON m.market_id = s.market_id WHERE m.ticker = 'E-1'"));
        Assert.True(await ScalarAsync<bool>("SELECT is_stale FROM dbo.orderbook_state s JOIN dbo.market m ON m.market_id = s.market_id WHERE m.ticker = 'E-2'"));
    }

    [Fact]
    public async Task AClosedGapHasItsMissingTradesRecoveredAndAlreadyStoredOnesAreNotDuplicated()
    {
        await Requirement.RequireLocalDbAsync();
        var settings = KalshiSettings.Create(KalshiEnvironment.Demo, "test-key", KeyFile());
        await DatabasePreparer.PrepareAsync(Options(), NullLogger.Instance, TestContext.Current.CancellationToken);
        var gapStart = DateTime.UtcNow.AddMinutes(-20);
        var gapEnd = gapStart.AddMinutes(5);
        await new GapStore(Source()).AppendAsync([new GapRecord(GapKinds.IngestDown, gapStart, gapEnd, "test gap")], TestContext.Current.CancellationToken);
        var trades = new FakeTradeSource();
        trades.Trades.AddRange(
        [
            new Kalshi.Models.Trade("rec-1", "E-1", Core.Money.Dollars.Parse("0.40"), Core.Money.Dollars.Parse("0.60"), Core.Money.Contracts.Parse("2.00"), new DateTimeOffset(gapStart.AddMinutes(1)), "yes"),
            new Kalshi.Models.Trade("rec-2", "E-2", Core.Money.Dollars.Parse("0.30"), Core.Money.Dollars.Parse("0.70"), Core.Money.Contracts.Parse("1.00"), new DateTimeOffset(gapStart.AddMinutes(2)), "no"),
            new Kalshi.Models.Trade("rec-3", "KXMVEX-1", Core.Money.Dollars.Parse("0.30"), Core.Money.Dollars.Parse("0.70"), Core.Money.Contracts.Parse("1.00"), new DateTimeOffset(gapStart.AddMinutes(2)), "no"),
        ]);
        var source = new ScriptedSource(_ => Task.FromResult<IStreamSession>(new ScriptedSession()));
        using var stop = new CancellationTokenSource();

        var run = IngestComposition.RunAsync(
            Options(),
            settings,
            NullLoggerFactory.Instance,
            Overrides(source, trades, new KalTerm.Ingest.Backfill.BackfillOptions { Settle = TimeSpan.Zero, PageDelay = TimeSpan.Zero }),
            stop.Token);
        await Wait.UntilAsync(() => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ingest_gap WHERE backfill_status = 'done'").GetAwaiter().GetResult() >= 1, "the gap to be recovered");
        await stop.CancelAsync();
        await run;

        Assert.Equal(["rec-1", "rec-2"], await TradeIdsAsync());
        Assert.Equal(3, await ScalarAsync<int>("SELECT backfill_found FROM dbo.ingest_gap WHERE kind = 'ingest_down' AND detail = 'test gap'"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT backfill_added FROM dbo.ingest_gap WHERE kind = 'ingest_down' AND detail = 'test gap'"));
    }

    private async Task<List<string>> TradeIdsAsync()
    {
        await using var connection = await Source().OpenAsync();
        await using var command = new SqlCommand("SELECT trade_id FROM dbo.trade ORDER BY trade_id", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    [Fact]
    public async Task WithABackupTargetTheHostTakesAFullBackupOfItsOwnDatabaseAndProvesItByRestoring()
    {
        await Requirement.RequireLocalDbAsync();
        var settings = KalshiSettings.Create(KalshiEnvironment.Demo, "test-key", KeyFile());
        var target = Path.Combine(Path.GetTempPath(), "kalterm-composition-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            await new KalTerm.Backup.BackupTarget(target).InitAsync(TestContext.Current.CancellationToken);
            var options = Options();
            options.BackupTarget = target;
            options.BackupCredentialPath = Path.Combine(target, "no-credential-needed.cred");
            var source = new ScriptedSource(_ => Task.FromResult<IStreamSession>(new ScriptedSession()));
            using var stop = new CancellationTokenSource();

            var run = IngestComposition.RunAsync(options, settings, NullLoggerFactory.Instance, Overrides(source, backupCheck: TimeSpan.FromMilliseconds(100)), stop.Token);
            await Wait.UntilAsync(() => source.Opens >= 1, "the stream to open (the database exists by then)");
            await Wait.UntilAsync(() => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ingest_state WHERE name = 'backup.last_verify' OR (name = 'backup.last' AND value LIKE 'error%')").GetAwaiter().GetResult() >= 1, "the backup and its proof (or its failure)");
            await stop.CancelAsync();
            await run;

            Assert.StartsWith("ok full", await ScalarAsync<string>("SELECT value FROM dbo.ingest_state WHERE name = 'backup.last'"), StringComparison.Ordinal);
            Assert.StartsWith("ok ", await ScalarAsync<string>("SELECT value FROM dbo.ingest_state WHERE name = 'backup.last_verify'"), StringComparison.Ordinal);
            Assert.Equal(target, await ScalarAsync<string>("SELECT value FROM dbo.ingest_state WHERE name = 'backup.configured'"));
            Assert.Single(await new KalTerm.Backup.BackupTarget(target).ListAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
