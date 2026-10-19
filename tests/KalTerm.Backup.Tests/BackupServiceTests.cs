using KalTerm.Backup;
using KalTerm.Data.State;

namespace KalTerm.Backup.Tests;

internal sealed class FakeState : IIngestState
{
    public Dictionary<string, IngestStateEntry> Values { get; } = new(StringComparer.Ordinal);

    public Task<IngestStateEntry?> GetAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Values.TryGetValue(name, out var entry) ? entry : null);

    public Task SetAsync(string name, string value, DateTime at, CancellationToken cancellationToken = default)
    {
        Values[name] = new IngestStateEntry(value, at);
        return Task.CompletedTask;
    }
}

internal sealed class FakeTime(DateTime now) : TimeProvider
{
    public DateTime Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

/// <summary>A database that writes small files for backups and records everything it is asked.</summary>
internal sealed class FakeBackupDatabase(string staging) : IBackupDatabase
{
    public string Name => "KalTerm";

    public List<(BackupKind Kind, string Path)> Backups { get; } = [];

    public List<string> Verified { get; } = [];

    public List<(string Database, string Full, string? Diff)> Restores { get; } = [];

    public List<(string Database, bool PhysicalOnly)> Checks { get; } = [];

    public List<string> Dropped { get; } = [];

    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DayCounts Live { get; set; } = Tk.Counts(("2026-10-19", 10, 2));

    public DayCounts Restored { get; set; } = Tk.Counts(("2026-10-19", 10, 2));

    public Exception? BackupFails { get; set; }

    public Exception? VerifyFails { get; set; }

    public Exception? CheckFails { get; set; }

    public Exception? DropFails { get; set; }

    public byte[] Content { get; set; } = [1, 2, 3, 4, 5];

    public Task<string> DefaultBackupDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(staging);

    public Task<string> VersionAsync(CancellationToken cancellationToken = default) => Task.FromResult("17.0 Developer");

    public Task<DayCounts> CountsAsync(string? database = null, CancellationToken cancellationToken = default) => Task.FromResult(database is null ? Live : Restored);

    public async Task BackupAsync(BackupKind kind, string path, CancellationToken cancellationToken = default)
    {
        if (BackupFails is not null)
        {
            throw BackupFails;
        }

        Backups.Add((kind, path));
        await File.WriteAllBytesAsync(path, Content, cancellationToken);
    }

    public Task VerifyAsync(string path, CancellationToken cancellationToken = default)
    {
        Verified.Add(path);
        return VerifyFails is null ? Task.CompletedTask : Task.FromException(VerifyFails);
    }

    public Task<bool> ExistsAsync(string database, CancellationToken cancellationToken = default) => Task.FromResult(Existing.Contains(database));

    public Task RestoreAsync(string database, string fullPath, string? differentialPath, CancellationToken cancellationToken = default)
    {
        Restores.Add((database, fullPath, differentialPath));
        Existing.Add(database);
        return Task.CompletedTask;
    }

    public Task CheckAsync(string database, bool physicalOnly, CancellationToken cancellationToken = default)
    {
        Checks.Add((database, physicalOnly));
        return CheckFails is null ? Task.CompletedTask : Task.FromException(CheckFails);
    }

    public Task DropAsync(string database, CancellationToken cancellationToken = default)
    {
        Dropped.Add(database);
        Existing.Remove(database);
        return DropFails is null ? Task.CompletedTask : Task.FromException(DropFails);
    }
}

internal sealed class FakeConnector : IShareConnector
{
    public List<(string Path, string User)> Connections { get; } = [];

    public int Disposed { get; private set; }

    public IDisposable Connect(string uncPath, ShareCredential credential)
    {
        Connections.Add((uncPath, credential.UserName));
        return new Handle(this);
    }

    private sealed class Handle(FakeConnector owner) : IDisposable
    {
        public void Dispose() => owner.Disposed++;
    }
}

public abstract class ServiceTestBase : IDisposable
{
    protected ServiceTestBase()
    {
        Root = Tk.TempDir();
        Staging = Path.Combine(Root, "staging");
        TargetDir = Path.Combine(Root, "target");
        Directory.CreateDirectory(Staging);
        Db = new FakeBackupDatabase(Staging);
        Time = new FakeTime(Tk.T(2026, 10, 19, 8));   // a Monday
    }

    protected string Root { get; }

    protected string Staging { get; }

    protected string TargetDir { get; }

    internal FakeBackupDatabase Db { get; }

    internal FakeState State { get; } = new();

    internal FakeTime Time { get; }

    protected BackupOptions Options(string? target = null) => new() { TargetPath = target ?? TargetDir, StagingPath = Staging };

    internal BackupService Backups(BackupOptions? options = null, IShareConnector? connector = null, Func<ShareCredential?>? credential = null) =>
        new(Db, State, options ?? Options(), connector, credential, Time);

    protected async Task InitTargetAsync() => await new BackupTarget(TargetDir).InitAsync(TestContext.Current.CancellationToken);

    protected IEnumerable<string> TargetFiles() => Directory.Exists(TargetDir)
        ? Directory.EnumerateFiles(TargetDir).Select(Path.GetFileName).Where(n => n != BackupTarget.MarkerName).Order(StringComparer.Ordinal).Select(n => n!)
        : [];

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
        GC.SuppressFinalize(this);
    }
}

public sealed class BackupServiceTests : ServiceTestBase
{
    [Fact]
    public async Task TheFirstBackupIsAFullOneWithAManifestAndNothingIsLeftInStaging()
    {
        await InitTargetAsync();
        Db.Live = Tk.Counts(("2026-10-19", 10, 2));

        var result = await Backups().RunAsync(null, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Problem);
        Assert.Equal((BackupKind.Full, "kalterm-full-20261019-080000.bak", 5L), (result.Kind, result.Name, result.SizeBytes));
        Assert.Equal(["kalterm-full-20261019-080000.bak", "kalterm-full-20261019-080000.bak.json"], TargetFiles());
        Assert.Empty(Directory.GetFiles(Staging));
        var manifest = BackupManifest.FromJson(await File.ReadAllTextAsync(Path.Combine(TargetDir, "kalterm-full-20261019-080000.bak.json"), TestContext.Current.CancellationToken));
        Assert.Equal((5L, "74F81FE167D99B4CB41D6D0CCDA82278CAEE9F3E2F25D5E5A4B3B2E5D6D0B6E7".Length, "KalTerm"), (manifest.SizeBytes, manifest.Sha256.Length, manifest.Database));
        Assert.Equal(10L, manifest.Before.Ticker["2026-10-19"]);
        Assert.Single(Db.Verified);
        Assert.EndsWith(".bak.partial", Db.Backups[0].Path, StringComparison.Ordinal);
        Assert.StartsWith("ok full kalterm-full-20261019-080000.bak", State.Values[BackupStateKeys.Last].Value, StringComparison.Ordinal);
        Assert.Equal("kalterm-full-20261019-080000.bak", State.Values[BackupStateKeys.LastGood].Value);
    }

    [Fact]
    public async Task TheManifestHoldsTheCountsFromBeforeAndAfterTheBackup()
    {
        await InitTargetAsync();
        var service = Backups();
        var calls = 0;
        var db = new CountingDatabase(Staging, () => calls++ == 0 ? Tk.Counts(("2026-10-19", 10, 2)) : Tk.Counts(("2026-10-19", 14, 3)));

        var result = await new BackupService(db, State, Options(), null, null, Time).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var manifest = BackupManifest.FromJson(await File.ReadAllTextAsync(Path.Combine(TargetDir, result.Name + ".json"), TestContext.Current.CancellationToken));
        Assert.Equal((10L, 14L, 2L, 3L), (manifest.Before.Ticker["2026-10-19"], manifest.After.Ticker["2026-10-19"], manifest.Before.Trade["2026-10-19"], manifest.After.Trade["2026-10-19"]));
        Assert.NotNull(service);
    }

    private sealed class CountingDatabase(string staging, Func<DayCounts> counts) : IBackupDatabase
    {
        private readonly FakeBackupDatabase _inner = new(staging);

        public string Name => _inner.Name;

        public Task<string> DefaultBackupDirectoryAsync(CancellationToken cancellationToken = default) => _inner.DefaultBackupDirectoryAsync(cancellationToken);

        public Task<string> VersionAsync(CancellationToken cancellationToken = default) => _inner.VersionAsync(cancellationToken);

        public Task<DayCounts> CountsAsync(string? database = null, CancellationToken cancellationToken = default) => Task.FromResult(counts());

        public Task BackupAsync(BackupKind kind, string path, CancellationToken cancellationToken = default) => _inner.BackupAsync(kind, path, cancellationToken);

        public Task VerifyAsync(string path, CancellationToken cancellationToken = default) => _inner.VerifyAsync(path, cancellationToken);

        public Task<bool> ExistsAsync(string database, CancellationToken cancellationToken = default) => _inner.ExistsAsync(database, cancellationToken);

        public Task RestoreAsync(string database, string fullPath, string? differentialPath, CancellationToken cancellationToken = default) => _inner.RestoreAsync(database, fullPath, differentialPath, cancellationToken);

        public Task CheckAsync(string database, bool physicalOnly, CancellationToken cancellationToken = default) => _inner.CheckAsync(database, physicalOnly, cancellationToken);

        public Task DropAsync(string database, CancellationToken cancellationToken = default) => _inner.DropAsync(database, cancellationToken);
    }

    [Fact]
    public async Task AfterAFullBackupTheNextDaysAreDifferentialsUntilSundayOrAWeekHasPassed()
    {
        await InitTargetAsync();
        var service = Backups();
        await service.RunAsync(null, TestContext.Current.CancellationToken);   // Monday: full (none yet)

        var kinds = new List<BackupKind?>();
        foreach (var day in new[] { 20, 21, 22, 23, 24, 25 })
        {
            Time.Now = Tk.T(2026, 10, day, 8);
            kinds.Add((await service.RunAsync(null, TestContext.Current.CancellationToken)).Kind);
        }

        Time.Now = Tk.T(2026, 10, 26, 8);
        var monday = await service.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal([BackupKind.Differential, BackupKind.Differential, BackupKind.Differential, BackupKind.Differential, BackupKind.Differential, BackupKind.Full], kinds);
        Assert.Equal(BackupKind.Differential, monday.Kind);
        var manifests = await new BackupTarget(TargetDir).ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("kalterm-full-20261025-080000.bak", manifests.Single(m => m.Name == "kalterm-diff-20261026-080000.bak").BaseFull);
    }

    [Fact]
    public async Task AFullBackupIsTakenWhenTheLastOneIsAWeekOldEvenOffSunday()
    {
        await InitTargetAsync();
        var service = Backups(Options() with { FullOn = DayOfWeek.Wednesday });   // Monday 19th: first full
        await service.RunAsync(null, TestContext.Current.CancellationToken);
        Time.Now = Tk.T(2026, 10, 26, 8);   // a Monday, seven days later

        var week = await service.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(BackupKind.Full, week.Kind);
    }

    [Fact]
    public async Task ADifferentialCannotBeForcedWithoutAFullBackup()
    {
        await InitTargetAsync();

        var result = await Backups().RunAsync(BackupKind.Differential, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("needs a full backup", result.Problem, StringComparison.Ordinal);
        Assert.Empty(Db.Backups);
    }

    [Fact]
    public async Task ATargetWithoutItsMarkerStopsTheRunBeforeSqlServerIsAskedToDoAnything()
    {
        Directory.CreateDirectory(TargetDir);

        var result = await Backups().RunAsync(null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("no marker file", result.Problem, StringComparison.Ordinal);
        Assert.Empty(Db.Backups);
        Assert.StartsWith("error ", State.Values[BackupStateKeys.Last].Value, StringComparison.Ordinal);
        Assert.False(State.Values.ContainsKey(BackupStateKeys.LastGood));
        Assert.Empty(TargetFiles());
    }

    [Fact]
    public async Task AFailedBackupLeavesNothingOnTheTargetOrInStagingAndKeepsTheLastGoodRecord()
    {
        await InitTargetAsync();
        var service = Backups();
        await service.RunAsync(null, TestContext.Current.CancellationToken);
        var good = State.Values[BackupStateKeys.LastGood];
        Time.Now = Tk.T(2026, 10, 20, 8);
        Db.VerifyFails = new InvalidDataException("checksum mismatch");

        var result = await service.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("checksum mismatch", result.Problem);
        Assert.Equal(["kalterm-full-20261019-080000.bak", "kalterm-full-20261019-080000.bak.json"], TargetFiles());
        Assert.Empty(Directory.GetFiles(Staging));
        Assert.Equal(good, State.Values[BackupStateKeys.LastGood]);
        Assert.Equal("error checksum mismatch", State.Values[BackupStateKeys.Last].Value);
    }

    [Fact]
    public async Task AFailedSqlBackupIsReportedAndLeavesNothingBehind()
    {
        await InitTargetAsync();
        Db.BackupFails = new InvalidOperationException("disk full");

        var result = await Backups().RunAsync(null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("disk full", result.Problem);
        Assert.Empty(TargetFiles());
    }

    [Fact]
    public async Task StalePartialFilesOnTheTargetAreClearedBeforeTheNextBackup()
    {
        await InitTargetAsync();
        await File.WriteAllTextAsync(Path.Combine(TargetDir, "kalterm-full-20261012-080000.bak.partial"), "left by a crash", TestContext.Current.CancellationToken);

        var result = await Backups().RunAsync(null, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.DoesNotContain(TargetFiles(), f => f.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetentionRunsAfterABackupAndDeletesOnlyWhatGfsDoesNotKeep()
    {
        await InitTargetAsync();
        var target = new BackupTarget(TargetDir);
        for (var week = 0; week < 6; week++)
        {
            var at = Tk.T(2026, 9, 6).AddDays(7 * week);
            var manifest = Tk.Manifest(BackupNaming.FileName(BackupKind.Full, at), BackupKind.Full, at);
            await File.WriteAllBytesAsync(target.FilePath(manifest.Name), [1], TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(target.FilePath(BackupNaming.ManifestName(manifest.Name)), manifest.ToJson(), TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(target.FilePath("notes.txt"), "mine", TestContext.Current.CancellationToken);
        Time.Now = Tk.T(2026, 10, 19, 8);

        var result = await Backups(Options() with { Retention = new GfsOptions { Weekly = 2, Monthly = 1 } }).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.Deleted > 0);
        Assert.True(File.Exists(target.FilePath("notes.txt")));
        var left = (await target.ListAsync(TestContext.Current.CancellationToken)).Select(m => m.Name).ToList();
        Assert.Contains("kalterm-full-20261019-080000.bak", left);
        Assert.DoesNotContain("kalterm-full-20260906-080000.bak", left);
    }

    [Fact]
    public async Task ANetworkTargetIsConnectedWithTheStoredCredentialForTheWholeRunAndReleasedAfter()
    {
        var connector = new FakeConnector();
        var unc = @"\\127.0.0.1\kalterm-no-such-share\KalTerm";
        var service = Backups(Options(unc), connector, () => new ShareCredential("nas\\admin", "secret"));

        var result = await service.RunAsync(null, TestContext.Current.CancellationToken);

        Assert.False(result.Success);   // there is no such share in the test, so the target check fails after connecting
        Assert.Equal([(unc, "nas\\admin")], connector.Connections);
        Assert.Equal(1, connector.Disposed);
    }

    [Fact]
    public async Task ALocalTargetNeverUsesTheConnectorAndMissingPartsAreRefused()
    {
        await InitTargetAsync();
        var connector = new FakeConnector();

        var result = await Backups(null, connector, () => new ShareCredential("x", "y")).RunAsync(null, TestContext.Current.CancellationToken);
        var noCredential = await Backups(Options(@"\\127.0.0.1\kalterm-no-such-share\KalTerm"), connector, () => null).RunAsync(null, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(connector.Connections);
        Assert.False(noCredential.Success);
        Assert.Throws<ArgumentNullException>(() => new BackupService(null!, State, Options()));
        Assert.Throws<ArgumentNullException>(() => new BackupService(Db, null!, Options()));
        Assert.Throws<ArgumentNullException>(() => new BackupService(Db, State, null!));
        Assert.Throws<ArgumentException>(() => new BackupService(Db, State, new BackupOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupService(Db, State, Options() with { FreeSpaceFactor = 0.5 }));
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        await InitTargetAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Backups().RunAsync(null, cts.Token));
    }
}

public sealed class RestoreServiceTests : ServiceTestBase
{
    private async Task<(BackupService Backup, RestoreService Restore)> ServicesAsync(Func<ShareCredential?>? credential = null, IShareConnector? connector = null, string? target = null)
    {
        await InitTargetAsync();
        return (Backups(Options(target)), new RestoreService(Db, State, Options(target), connector, credential, Time));
    }

    [Fact]
    public async Task ARestoreOfTheNewestBackupCreatesANewDatabaseChecksItAndDropsItAgain()
    {
        var (backup, restore) = await ServicesAsync();
        await backup.RunAsync(null, TestContext.Current.CancellationToken);

        var result = await restore.RestoreAsync("KalTerm_copy", null, keep: false, physicalOnly: true, TestContext.Current.CancellationToken);

        Assert.True(result.Comparison.Matches);
        Assert.Equal(["kalterm-full-20261019-080000.bak"], result.Backups);
        Assert.Equal([("KalTerm_copy", true)], Db.Checks);
        Assert.Equal(["KalTerm_copy"], Db.Dropped);
        Assert.Empty(Directory.GetFiles(Staging));
        Assert.Contains(Db.Restores, r => r.Database == "KalTerm_copy" && r.Diff is null);
    }

    [Fact]
    public async Task ADifferentialIsRestoredOnTopOfItsFullBackupAndAKeptCopyIsNotDropped()
    {
        var (backup, restore) = await ServicesAsync();
        await backup.RunAsync(null, TestContext.Current.CancellationToken);
        Time.Now = Tk.T(2026, 10, 20, 8);
        await backup.RunAsync(null, TestContext.Current.CancellationToken);

        var result = await restore.RestoreAsync("KalTerm_restored", null, keep: true, physicalOnly: false, TestContext.Current.CancellationToken);

        Assert.Equal(["kalterm-full-20261019-080000.bak", "kalterm-diff-20261020-080000.bak"], result.Backups);
        Assert.True(result.Kept);
        Assert.Empty(Db.Dropped);
        var restoreCall = Db.Restores.Single();
        Assert.EndsWith("restore-kalterm-full-20261019-080000.bak", restoreCall.Full, StringComparison.Ordinal);
        Assert.EndsWith("restore-kalterm-diff-20261020-080000.bak", restoreCall.Diff, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task ANamedBackupCanBeRestoredAndAMissingOneIsReported()
    {
        var (backup, restore) = await ServicesAsync();
        await backup.RunAsync(null, TestContext.Current.CancellationToken);

        var named = await restore.RestoreAsync("KalTerm_named", "kalterm-full-20261019-080000.bak", keep: false, physicalOnly: true, TestContext.Current.CancellationToken);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RestoreAsync("KalTerm_x", "kalterm-full-20250101-080000.bak", keep: false, physicalOnly: true, TestContext.Current.CancellationToken));

        Assert.True(named.Comparison.Matches);
        Assert.Contains("no backup named", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestoreNeverOverwritesAnExistingDatabaseOrTheLiveOne()
    {
        var (backup, restore) = await ServicesAsync();
        await backup.RunAsync(null, TestContext.Current.CancellationToken);
        Db.Existing.Add("KalTerm_taken");

        await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RestoreAsync("KalTerm_taken", null, false, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RestoreAsync("kalterm", null, false, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => restore.RestoreAsync("bad name; drop", null, false, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => restore.RestoreAsync(" ", null, false, true, TestContext.Current.CancellationToken));

        Assert.Empty(Db.Restores);
    }

    [Fact]
    public async Task AnEmptyTargetAndADifferentialWithoutItsFullBackupAreReported()
    {
        var (_, restore) = await ServicesAsync();
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RestoreAsync("KalTerm_a", null, false, true, TestContext.Current.CancellationToken));
        var diff = Tk.Manifest("kalterm-diff-20261020-080000.bak", BackupKind.Differential, Tk.T(2026, 10, 20), baseFull: "kalterm-full-20261019-080000.bak");
        await File.WriteAllBytesAsync(Path.Combine(TargetDir, diff.Name), [1], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(TargetDir, diff.Name + ".json"), diff.ToJson(), TestContext.Current.CancellationToken);

        var orphan = await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RestoreAsync("KalTerm_b", null, false, true, TestContext.Current.CancellationToken));

        Assert.Contains("no backup on the target", none.Message, StringComparison.Ordinal);
        Assert.Contains("is not on the target", orphan.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADamagedFileOnTheTargetIsCaughtByItsHashBeforeSqlServerSeesIt()
    {
        var (backup, restore) = await ServicesAsync();
        await backup.RunAsync(null, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(TargetDir, "kalterm-full-20261019-080000.bak"), [9, 9, 9], TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => restore.RestoreAsync("KalTerm_c", null, false, true, TestContext.Current.CancellationToken));

        Assert.Contains("hash differs", failure.Message, StringComparison.Ordinal);
        Assert.Empty(Db.Restores);
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task ACopyThatFailsItsCheckIsDroppedAndTheFailureEscapes()
    {
        var (backup, restore) = await ServicesAsync();
        await backup.RunAsync(null, TestContext.Current.CancellationToken);
        Db.CheckFails = new InvalidDataException("DBCC found errors");

        await Assert.ThrowsAsync<InvalidDataException>(() => restore.RestoreAsync("KalTerm_d", null, false, true, TestContext.Current.CancellationToken));

        Assert.Equal(["KalTerm_d"], Db.Dropped);
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task VerifyRecordsGoodWhenTheCopyAgreesAndErrorWhenItDoesNotOrCannotBeRestored()
    {
        var (backup, restore) = await ServicesAsync();
        await backup.RunAsync(null, TestContext.Current.CancellationToken);

        var good = await restore.VerifyAsync(true, TestContext.Current.CancellationToken);
        var goodText = State.Values[BackupStateKeys.LastVerify].Value;
        Db.Restored = Tk.Counts(("2026-10-19", 1, 2));
        var disagrees = await restore.VerifyAsync(true, TestContext.Current.CancellationToken);
        var disagreesText = State.Values[BackupStateKeys.LastVerify].Value;
        Db.Restored = Db.Live;
        Db.CheckFails = new InvalidDataException("bad pages");
        var broken = await restore.VerifyAsync(true, TestContext.Current.CancellationToken);

        Assert.True(good);
        Assert.StartsWith("ok kalterm-full-20261019-080000.bak restored and checked", goodText, StringComparison.Ordinal);
        Assert.False(disagrees);
        Assert.StartsWith("error the restored copy disagrees with its manifest: ticker 2026-10-19: restored 1", disagreesText, StringComparison.Ordinal);
        Assert.False(broken);
        Assert.Equal("error bad pages", State.Values[BackupStateKeys.LastVerify].Value);
        Assert.All(Db.Dropped, name => Assert.StartsWith("KalTerm_verify_", name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANetworkTargetIsConnectedForTheRestoreToo()
    {
        var connector = new FakeConnector();
        var (_, restore) = await ServicesAsync(() => new ShareCredential("u", "p"), connector, @"\\127.0.0.1\kalterm-no-such-share\KalTerm");

        await Assert.ThrowsAsync<InvalidOperationException>(() => restore.RestoreAsync("KalTerm_e", null, false, true, TestContext.Current.CancellationToken));

        Assert.Single(connector.Connections);
        Assert.Equal(1, connector.Disposed);
        Assert.Throws<ArgumentNullException>(() => new RestoreService(null!, State, Options()));
        Assert.Throws<ArgumentNullException>(() => new RestoreService(Db, null!, Options()));
        Assert.Throws<ArgumentNullException>(() => new RestoreService(Db, State, null!));
    }
}
