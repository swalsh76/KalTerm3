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

    private static IngestOverrides Overrides(IStreamSource source) => new(
        source,
        _ => List([Open("E-1"), Open("E-2")]),
        (_, _) => List([]),
        new IngestRunnerOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            StreamRetryBase = TimeSpan.FromMilliseconds(10),
            StreamRetryMax = TimeSpan.FromMilliseconds(40),
            Schedule = now => (now.AddHours(1), PassKind.Full),
        });

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
}
