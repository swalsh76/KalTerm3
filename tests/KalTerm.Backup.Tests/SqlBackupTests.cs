using KalTerm.Backup;
using KalTerm.Data;
using KalTerm.Data.State;

namespace KalTerm.Backup.Tests;

/// <summary>
/// The real thing: SQL Server writes, verifies, restores and checks backups of a real database with the real tables. Skipped where no SQL Server is available (run on kaldb).
/// </summary>
public sealed class SqlBackupTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>, IDisposable
{
    private static readonly DateTime Day = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
    private readonly List<string> _dirs = [];

    private SqlBackupDatabase Database() => new(new DataSource(db.Options), db.Options.Database);

    private static string UniqueName() => "KalTermBackupTest_" + Guid.NewGuid().ToString("N")[..12];

    private async Task SeedAsync(int ticks, int trades, int dayOffset)
    {
        var at = Day.AddDays(dayOffset);
        var tickRows = string.Join(", ", Enumerable.Range(0, ticks).Select(i => FormattableString.Invariant($"('{at.AddSeconds(i):yyyy-MM-dd'T'HH:mm:ss.fff}', 7, {400_000 + i}, {399_000 + i}, {i})")));
        await db.ExecuteAsync("INSERT dbo.ticker (ts, market_id, price_e6, yes_bid_e6, volume_e2) VALUES " + tickRows);
        var tradeRows = string.Join(", ", Enumerable.Range(0, trades).Select(i => FormattableString.Invariant($"('{at.AddSeconds(i):yyyy-MM-dd'T'HH:mm:ss.fff}', 7, 'bk-{dayOffset}-{i}', 400000, 600000, 300, 1, 0)")));
        await db.ExecuteAsync("INSERT dbo.trade (ts, market_id, trade_id, yes_price_e6, no_price_e6, count_e2, taker_side, is_block) VALUES " + tradeRows);
    }

    private async Task<string> StagingFileAsync(string name)
    {
        var dir = await Database().DefaultBackupDirectoryAsync(TestContext.Current.CancellationToken);
        return Path.Combine(dir, name);
    }

    public void Dispose()
    {
        // Databases and files a failed test left behind are removed on the next run of the fixture's server; nothing here may throw.
        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ServerFactsAreAvailableAndNamesAreValidated()
    {
        db.Require();
        var database = Database();

        Assert.NotEmpty(await database.DefaultBackupDirectoryAsync(TestContext.Current.CancellationToken));
        Assert.Contains(".", await database.VersionAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("[KalTerm_1]", SqlBackupDatabase.Quote("KalTerm_1"));
        Assert.Null(SqlBackupDatabase.Quote("bad name"));
        Assert.Null(SqlBackupDatabase.Quote("x]; DROP DATABASE y;--"));
        Assert.Null(SqlBackupDatabase.Quote(null));
        Assert.Throws<ArgumentException>(() => new SqlBackupDatabase(new DataSource(db.Options), "bad name"));
        Assert.True(await database.ExistsAsync(db.Options.Database, TestContext.Current.CancellationToken));
        Assert.False(await database.ExistsAsync("NoSuchDatabase_" + Guid.NewGuid().ToString("N")[..8], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFullAndADifferentialBackupRestoreIntoANewDatabaseThatPassesTheChecksAndMatchesTheLiveCounts()
    {
        db.Require();
        await SeedAsync(200, 50, 0);
        var database = Database();
        var full = await StagingFileAsync(UniqueName() + "-full.bak");
        var diff = await StagingFileAsync(UniqueName() + "-diff.bak");
        var copy = UniqueName();
        try
        {
            await database.BackupAsync(BackupKind.Full, full, TestContext.Current.CancellationToken);
            await database.VerifyAsync(full, TestContext.Current.CancellationToken);
            await SeedAsync(30, 10, 1);
            await database.BackupAsync(BackupKind.Differential, diff, TestContext.Current.CancellationToken);
            await database.VerifyAsync(diff, TestContext.Current.CancellationToken);
            var live = await database.CountsAsync(null, TestContext.Current.CancellationToken);

            await database.RestoreAsync(copy, full, diff, TestContext.Current.CancellationToken);
            await database.CheckAsync(copy, physicalOnly: false, TestContext.Current.CancellationToken);
            var restored = await database.CountsAsync(copy, TestContext.Current.CancellationToken);

            Assert.Equal(live.Ticker, restored.Ticker);
            Assert.Equal(live.Trade, restored.Trade);
            Assert.True(live.Ticker.GetValueOrDefault("2026-03-11") >= 30);
            Assert.True(await database.ExistsAsync(copy, TestContext.Current.CancellationToken));
        }
        finally
        {
            await database.DropAsync(copy, TestContext.Current.CancellationToken);
            File.Delete(full);
            File.Delete(diff);
        }

        Assert.False(await database.ExistsAsync(copy, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFullBackupAloneRestoresAndARestoreNeverTouchesTheLiveDatabaseOrAnExistingName()
    {
        db.Require();
        await SeedAsync(20, 5, 2);
        var database = Database();
        var full = await StagingFileAsync(UniqueName() + "-full.bak");
        var copy = UniqueName();
        try
        {
            await database.BackupAsync(BackupKind.Full, full, TestContext.Current.CancellationToken);
            await database.RestoreAsync(copy, full, null, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() => database.RestoreAsync(copy, full, null, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.RestoreAsync(db.Options.Database, full, null, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.DropAsync(db.Options.Database, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => database.RestoreAsync("bad name", full, null, TestContext.Current.CancellationToken));
            await database.CheckAsync(copy, physicalOnly: true, TestContext.Current.CancellationToken);
            Assert.True(await database.ExistsAsync(db.Options.Database, TestContext.Current.CancellationToken));
        }
        finally
        {
            await database.DropAsync(copy, TestContext.Current.CancellationToken);
            File.Delete(full);
        }
    }

    [Fact]
    public async Task AFileThatIsNotAGoodBackupFailsVerification()
    {
        db.Require();
        var database = Database();
        var path = await StagingFileAsync(UniqueName() + "-junk.bak");

        try
        {
            await database.BackupAsync(BackupKind.Full, path, TestContext.Current.CancellationToken);
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            for (var i = bytes.Length / 2; i < bytes.Length / 2 + 4096 && i < bytes.Length; i++)
            {
                bytes[i] ^= 0xFF;   // damage the middle of the file
            }

            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<Exception>(() => database.VerifyAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TheWholeServiceBacksUpToAFolderRetainsAndProvesTheBackupByRestoringIt()
    {
        db.Require();
        await SeedAsync(100, 20, 3);
        var target = Path.Combine(Path.GetTempPath(), "kalterm-sql-backup-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(target);
        await new BackupTarget(target).InitAsync(TestContext.Current.CancellationToken);
        var state = new IngestStateStore(new DataSource(db.Options));
        var options = new BackupOptions { TargetPath = target };
        var database = Database();

        var first = await new BackupService(database, state, options).RunAsync(null, TestContext.Current.CancellationToken);
        await SeedAsync(10, 2, 4);
        var second = await new BackupService(database, state, options).RunAsync(BackupKind.Differential, TestContext.Current.CancellationToken);
        var verified = await new RestoreService(database, state, options).VerifyAsync(physicalOnly: false, TestContext.Current.CancellationToken);

        Assert.True(first.Success, first.Problem);
        Assert.True(second.Success, second.Problem);
        Assert.Equal((BackupKind.Full, BackupKind.Differential), (first.Kind, second.Kind));
        Assert.True(verified);
        Assert.StartsWith("ok ", (await state.GetAsync(BackupStateKeys.LastVerify, TestContext.Current.CancellationToken))!.Value, StringComparison.Ordinal);
        Assert.Equal(second.Name, (await state.GetAsync(BackupStateKeys.LastGood, TestContext.Current.CancellationToken))!.Value);
        Assert.Equal(2, (await new BackupTarget(target).ListAsync(TestContext.Current.CancellationToken)).Count);
        Assert.Empty(Directory.EnumerateFiles(await database.DefaultBackupDirectoryAsync(TestContext.Current.CancellationToken), "kalterm-*.partial"));
        Assert.Empty(Directory.EnumerateFiles(target, "*.partial"));
    }
}
