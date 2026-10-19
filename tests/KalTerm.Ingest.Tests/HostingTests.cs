using KalTerm.Ingest.Hosting;
using KalTerm.Kalshi.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Ingest.Tests;

public class IngestHostOptionsTests
{
    [Fact]
    public void TheDefaultsUseSharedMemoryAndTheStandardDatabaseAndNeverProvision()
    {
        var options = new IngestHostOptions();

        options.Validate();

        var connection = options.ToConnectionOptions();
        Assert.Equal("lpc:.", connection.Server);
        Assert.Equal("KalTerm", connection.Database);
        Assert.Equal("KalTerm.Ingest", connection.ApplicationName);
        Assert.Null(options.ProvisionMaxSizeGb);
        Assert.Equal(7, options.PartitionDaysAhead);
    }

    [Theory]
    [InlineData("", "KalTerm", null, 7)]
    [InlineData("lpc:.", " ", null, 7)]
    [InlineData("lpc:.", "KalTerm", 0, 7)]
    [InlineData("lpc:.", "KalTerm", -5, 7)]
    [InlineData("lpc:.", "KalTerm", null, 0)]
    public void SettingsThatCannotWorkAreRefused(string server, string database, int? provisionGb, int daysAhead)
    {
        var options = new IngestHostOptions { Server = server, Database = database, ProvisionMaxSizeGb = provisionGb, PartitionDaysAhead = daysAhead };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }


    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(90, true)]
    public void RetentionMustBeMoreThanTheDaysAlwaysKept(int days, bool valid)
    {
        var options = new IngestHostOptions { RetentionDays = days };

        if (valid)
        {
            options.Validate();
        }
        else
        {
            Assert.Throws<InvalidOperationException>(options.Validate);
        }
    }

    [Fact]
    public void BackupsAreOffByDefaultAndASensibleTargetTurnsThemOn()
    {
        Assert.Null(new IngestHostOptions().ToBackupOptions());

        var options = new IngestHostOptions { BackupTarget = @"\\nas\backups\KalTerm", BackupAt = "03:30", BackupWeekly = 5 };
        options.Validate();
        var backup = options.ToBackupOptions()!;

        Assert.Equal((@"\\nas\backups\KalTerm", new TimeOnly(3, 30), 5, 7, 12), (backup.TargetPath, backup.RunAt, backup.Retention.Weekly, backup.Retention.Daily, backup.Retention.Monthly));
    }

    [Theory]
    [InlineData("25:00", 7)]
    [InlineData("08:00", 0)]
    public void ABackupSettingThatCannotWorkIsRefusedAtStartUp(string at, int daily)
    {
        var options = new IngestHostOptions { BackupTarget = "x", BackupAt = at, BackupDaily = daily };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void TheTradeBackfillIsOnByDefault()
    {
        Assert.True(new IngestHostOptions().BackfillEnabled);
    }

    [Fact]
    public void ADefaultOptionsKeepsThirtyDaysOfRawData()
    {
        Assert.Equal(30, new IngestHostOptions().RetentionDays);
    }

    [Fact]
    public void ADefaultOptionsHasNoWatchListAndANormalOneIsAccepted()
    {
        Assert.Empty(new IngestHostOptions().WatchList);
        new IngestHostOptions { WatchList = ["KXBTC-26DEC31-T100000", "A.B_C-1"] }.Validate();
        new IngestHostOptions { WatchList = [.. Enumerable.Range(0, IngestHostOptions.MaxWatchList).Select(i => "M-" + i)] }.Validate();
    }

    [Fact]
    public void ABadWatchListIsRefused()
    {
        Assert.Throws<InvalidOperationException>(new IngestHostOptions { WatchList = [.. Enumerable.Range(0, IngestHostOptions.MaxWatchList + 1).Select(i => "M-" + i)] }.Validate);
        Assert.Throws<InvalidOperationException>(new IngestHostOptions { WatchList = ["A", "A"] }.Validate);
        Assert.Throws<InvalidOperationException>(new IngestHostOptions { WatchList = ["has space"] }.Validate);
        Assert.Throws<InvalidOperationException>(new IngestHostOptions { WatchList = [""] }.Validate);
        Assert.Throws<InvalidOperationException>(new IngestHostOptions { WatchList = ["A", null!] }.Validate);
    }
}

public sealed class IngestWorkerTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public int StopCalls { get; private set; }

        public void StopApplication() => StopCalls++;
    }

    private static IngestWorker Worker(FakeLifetime lifetime, Func<CancellationToken, Task> run, List<int> exitCodes) =>
        new(new IngestHostOptions(), KalshiSettings.Default, NullLoggerFactory.Instance, lifetime, run, exitCodes.Add);

    [Fact]
    public async Task AFailureIsLoggedSetsTheExitCodeAndStopsTheHost()
    {
        var lifetime = new FakeLifetime();
        var exitCodes = new List<int>();
        using var worker = Worker(lifetime, _ => Task.FromException(new InvalidOperationException("database is gone")), exitCodes);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.ExecuteTask!;

        Assert.Equal([1], exitCodes);
        Assert.Equal(1, lifetime.StopCalls);
    }

    [Fact]
    public async Task AProcessThatEndsOnItsOwnStopsTheHostWithoutAFailureCode()
    {
        var lifetime = new FakeLifetime();
        var exitCodes = new List<int>();
        using var worker = Worker(lifetime, _ => Task.CompletedTask, exitCodes);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.ExecuteTask!;

        Assert.Empty(exitCodes);
        Assert.Equal(1, lifetime.StopCalls);
    }

    [Fact]
    public async Task ARequestedStopEndsTheWorkerQuietly()
    {
        var lifetime = new FakeLifetime();
        var exitCodes = new List<int>();
        using var worker = Worker(lifetime, async token => await Task.Delay(Timeout.Infinite, token), exitCodes);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Empty(exitCodes);
        Assert.Equal(0, lifetime.StopCalls);
    }

    [Fact]
    public void MissingPartsAreRefused()
    {
        var lifetime = new FakeLifetime();
        Assert.Throws<ArgumentNullException>(() => new IngestWorker(null!, KalshiSettings.Default, NullLoggerFactory.Instance, lifetime));
        Assert.Throws<ArgumentNullException>(() => new IngestWorker(new IngestHostOptions(), null!, NullLoggerFactory.Instance, lifetime));
        Assert.Throws<ArgumentNullException>(() => new IngestWorker(new IngestHostOptions(), KalshiSettings.Default, null!, lifetime));
        Assert.Throws<ArgumentNullException>(() => new IngestWorker(new IngestHostOptions(), KalshiSettings.Default, NullLoggerFactory.Instance, null!));
    }
}
