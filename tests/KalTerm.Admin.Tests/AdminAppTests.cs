using KalTerm.Admin;
using KalTerm.Backup;

namespace KalTerm.Admin.Tests;

internal sealed class FakeBackupService : IBackupService
{
    public List<BackupKind?> Calls { get; } = [];

    public BackupResult Result { get; set; } = new(true, "kalterm-full-20261019-080000.bak", BackupKind.Full, 5L * 1024 * 1024, TimeSpan.FromSeconds(7), 2, null);

    public Task<BackupResult> RunAsync(BackupKind? force = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(force);
        return Task.FromResult(Result);
    }
}

internal sealed class FakeRestoreService : IRestoreService
{
    public List<(string Database, string? Backup, bool Keep, bool PhysicalOnly)> Restores { get; } = [];

    public List<bool> Verifies { get; } = [];

    public bool VerifyResult { get; set; } = true;

    public Exception? Fails { get; set; }

    public CountComparison Comparison { get; set; } = new(true, []);

    public Task<RestoreResult> RestoreAsync(string database, string? backupName, bool keep, bool physicalOnly, CancellationToken cancellationToken = default)
    {
        Restores.Add((database, backupName, keep, physicalOnly));
        return Fails is null
            ? Task.FromResult(new RestoreResult(database, ["kalterm-full-20261019-080000.bak", "kalterm-diff-20261020-080000.bak"], keep, Comparison, TimeSpan.FromSeconds(3)))
            : Task.FromException<RestoreResult>(Fails);
    }

    public Task<bool> VerifyAsync(bool physicalOnly, CancellationToken cancellationToken = default)
    {
        Verifies.Add(physicalOnly);
        return Task.FromResult(VerifyResult);
    }
}

internal sealed class RecordingConnector : IShareConnector
{
    public List<(string Path, string User)> Connections { get; } = [];

    public Exception? Fails { get; set; }

    public IDisposable Connect(string uncPath, ShareCredential credential)
    {
        if (Fails is not null)
        {
            throw Fails;
        }

        Connections.Add((uncPath, credential.UserName));
        return new Nothing();
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

public sealed class AdminAppTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kalterm-admin-test-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly FakeBackupService _backups = new();
    private readonly FakeRestoreService _restores = new();
    private readonly RecordingConnector _connector = new();
    private string _targetPath;
    private string _stdin = string.Empty;
    private bool _interactive;
    private string _typed = string.Empty;
    private Exception? _createFails;

    public AdminAppTests()
    {
        Directory.CreateDirectory(_dir);
        _targetPath = Path.Combine(_dir, "target");
    }

    private ShareCredentialStore Store => new(Path.Combine(_dir, "secrets", "share.cred"));

    private async Task<int> RunAsync(params string[] args)
    {
        var env = new AdminEnvironment
        {
            Out = _out,
            Error = _err,
            In = new StringReader(_stdin),
            InteractiveInput = _interactive,
            ReadPassword = () => _typed,
            CreateServices = () => _createFails is not null
                ? throw _createFails
                : new AdminServices(_backups, _restores, new BackupTarget(_targetPath), Store, _connector),
        };
        return await AdminApp.RunAsync(args, env, TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        _out.Dispose();
        _err.Dispose();
    }

    [Fact]
    public async Task InitTargetWritesTheMarkerOnceAndSaysSoTheSecondTime()
    {
        var first = await RunAsync("backup", "init-target");
        var second = await RunAsync("backup", "init-target");

        Assert.Equal((0, 0), (first, second));
        Assert.Contains("marker written", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("already has its marker", _out.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_targetPath, BackupTarget.MarkerName)));
    }

    [Fact]
    public async Task InitTargetOnANetworkShareConnectsWithTheStoredCredentialFirst()
    {
        _targetPath = @"\\127.0.0.1\kalterm-no-such-share\KalTerm";
        Store.Save(new ShareCredential("nas\\admin", "pw"));

        var exit = await RunAsync("backup", "init-target");

        Assert.Equal(1, exit);   // the share does not exist, so writing the marker fails after the connection was made
        Assert.Equal([(@"\\127.0.0.1\kalterm-no-such-share\KalTerm", "nas\\admin")], _connector.Connections);
    }

    [Fact]
    public async Task ACredentialIsReadFromThePipeTriedOnTheShareAndStoredWithoutEverBeingPrinted()
    {
        _targetPath = @"\\127.0.0.1\kalterm-no-such-share\KalTerm";
        _stdin = "s3cret-pw" + Environment.NewLine;

        var exit = await RunAsync("backup", "set-credential", "--user", "nas\\admin", "--password-stdin");

        Assert.Equal(0, exit);
        Assert.Equal([(@"\\127.0.0.1\kalterm-no-such-share\KalTerm", "nas\\admin")], _connector.Connections);
        Assert.Equal(("nas\\admin", "s3cret-pw"), (Store.Load()!.UserName, Store.Load()!.Password));
        Assert.DoesNotContain("s3cret-pw", _out + _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("credential for nas\\admin stored", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtAKeyboardThePasswordIsAskedForWithAPromptAndNotEchoed()
    {
        _interactive = true;
        _typed = "typed-pw";

        var exit = await RunAsync("backup", "set-credential", "--user", "admin", "--no-test");

        Assert.Equal(0, exit);
        Assert.Contains("Password: ", _out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("typed-pw", _out + _err.ToString(), StringComparison.Ordinal);
        Assert.Equal("typed-pw", Store.Load()!.Password);
    }

    [Fact]
    public async Task ACredentialThatTheShareRefusesIsNotStored()
    {
        _targetPath = @"\\127.0.0.1\kalterm-no-such-share\KalTerm";
        _stdin = "wrong" + Environment.NewLine;
        _connector.Fails = new IOException("could not connect to \\\\127.0.0.1\\kalterm-no-such-share as admin: Windows error 1326");

        var exit = await RunAsync("backup", "set-credential", "--user", "admin", "--password-stdin");

        Assert.Equal(1, exit);
        Assert.Contains("Windows error 1326", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("was not stored", _err.ToString(), StringComparison.Ordinal);
        Assert.Null(Store.Load());
    }

    [Fact]
    public async Task ACredentialCanBeStoredWithoutTryingItAndANonShareTargetCannotTryIt()
    {
        _stdin = "pw" + Environment.NewLine;
        var refused = await RunAsync("backup", "set-credential", "--user", "admin", "--password-stdin");
        _stdin = "pw" + Environment.NewLine;
        var skipped = await RunAsync("backup", "set-credential", "--user", "admin", "--password-stdin", "--no-test");

        Assert.Equal((1, 0), (refused, skipped));
        Assert.Contains("not a network share", _err.ToString(), StringComparison.Ordinal);
        Assert.Equal("admin", Store.Load()!.UserName);
        Assert.Empty(_connector.Connections);
    }

    [Fact]
    public async Task AnEmptyPasswordIsRefused()
    {
        _stdin = string.Empty;

        var exit = await RunAsync("backup", "set-credential", "--user", "admin", "--password-stdin", "--no-test");

        Assert.Equal(1, exit);
        Assert.Contains("no password was given", _err.ToString(), StringComparison.Ordinal);
        Assert.Null(Store.Load());
    }

    [Fact]
    public async Task RunLetsTheScheduleDecideUnlessAKindIsForcedAndRefusesBoth()
    {
        var auto = await RunAsync("backup", "run");
        var full = await RunAsync("backup", "run", "--full");
        var diff = await RunAsync("backup", "run", "--diff");
        var both = await RunAsync("backup", "run", "--full", "--diff");

        Assert.Equal((0, 0, 0, 1), (auto, full, diff, both));
        Assert.Equal([null, BackupKind.Full, BackupKind.Differential], _backups.Calls);
        Assert.Contains("Full backup kalterm-full-20261019-080000.bak: 5 MB in 7 s; 2 old backups deleted", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("either --full or --diff", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedBackupIsReportedWithItsReasonAndAnErrorExitCode()
    {
        _backups.Result = new BackupResult(false, null, null, 0, TimeSpan.Zero, 0, "the backup target cannot be reached");

        var exit = await RunAsync("backup", "run");

        Assert.Equal(1, exit);
        Assert.Contains("backup failed: the backup target cannot be reached", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListShowsTheBackupsNewestFirstWithTheirBaseOrSaysThereAreNone()
    {
        var none = await RunAsync("backup", "list");
        var target = new BackupTarget(_targetPath);
        await target.InitAsync(TestContext.Current.CancellationToken);
        foreach (var (kind, at, baseFull) in new[] { (BackupKind.Full, new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc), (string?)null), (BackupKind.Differential, new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc), "kalterm-full-20261019-080000.bak") })
        {
            var name = BackupNaming.FileName(kind, at);
            await File.WriteAllBytesAsync(target.FilePath(name), new byte[3 * 1024 * 1024], TestContext.Current.CancellationToken);
            var manifest = new BackupManifest(1, name, kind, baseFull, at, at.AddMinutes(1), "KalTerm", 3L * 1024 * 1024, "AB", "17.0", new DayCounts(new Dictionary<string, long>(), new Dictionary<string, long>()), new DayCounts(new Dictionary<string, long>(), new Dictionary<string, long>()));
            await File.WriteAllTextAsync(target.FilePath(BackupNaming.ManifestName(name)), manifest.ToJson(), TestContext.Current.CancellationToken);
        }

        var listed = await RunAsync("backup", "list");

        Assert.Equal((0, 0), (none, listed));
        var lines = _out.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("no backups on the target", lines[0]);
        Assert.Contains("kalterm-diff-20261020-080000.bak (on kalterm-full-20261019-080000.bak)", lines[2], StringComparison.Ordinal);
        Assert.Contains("kalterm-full-20261019-080000.bak", lines[3], StringComparison.Ordinal);
        Assert.Contains("      3", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyReportsWhetherTheNewestBackupRestoresCleanlyAndFailsOtherwise()
    {
        var good = await RunAsync("backup", "verify");
        _restores.VerifyResult = false;
        var bad = await RunAsync("backup", "verify", "--full-check");

        Assert.Equal((0, 1), (good, bad));
        Assert.Equal([true, false], _restores.Verifies);
        Assert.Contains("restores cleanly", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("VERIFICATION FAILED", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreCreatesANewDatabasePassesTheOptionsAndReportsAgreementWithTheManifest()
    {
        var exit = await RunAsync("restore", "--database", "KalTerm_restored", "--backup", "kalterm-diff-20261020-080000.bak", "--keep");

        Assert.Equal(0, exit);
        Assert.Equal([("KalTerm_restored", "kalterm-diff-20261020-080000.bak", true, true)], _restores.Restores);
        var text = _out.ToString();
        Assert.Contains("restored kalterm-full-20261019-080000.bak + kalterm-diff-20261020-080000.bak into KalTerm_restored", text, StringComparison.Ordinal);
        Assert.Contains("row counts agree with the manifest", text, StringComparison.Ordinal);
        Assert.Contains("was kept; switching the server to it is a manual step", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreThatDisagreesWithTheManifestPrintsTheDifferencesAndFails()
    {
        _restores.Comparison = new CountComparison(false, ["ticker 2026-10-19: restored 1, but the backup saw 5 before and 6 after"]);

        var exit = await RunAsync("restore", "--database", "KalTerm_check", "--full-check");

        Assert.Equal(1, exit);
        Assert.Equal([("KalTerm_check", null, false, false)], _restores.Restores);
        Assert.Contains("DIFFERENCE: ticker 2026-10-19", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("was dropped again", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProblemsAreReportedAsOneLineErrorsNotStackTraces()
    {
        _restores.Fails = new InvalidOperationException("database KalTerm_taken already exists; a restore only creates a new database");
        var refused = await RunAsync("restore", "--database", "KalTerm_taken");
        _createFails = new InvalidOperationException("no backup target is configured");
        var unconfigured = await RunAsync("backup", "run");

        Assert.Equal((1, 1), (refused, unconfigured));
        Assert.Contains("error: database KalTerm_taken already exists", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("error: no backup target is configured", _err.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRequiredOptionsAreAUsageError()
    {
        var noDatabase = await RunAsync("restore");
        var noUser = await RunAsync("backup", "set-credential");

        Assert.Equal((1, 1), (noDatabase, noUser));
        Assert.Contains("--database", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("--user", _err.ToString(), StringComparison.Ordinal);
    }
}

public class BackupSettingsTests
{
    [Fact]
    public void NoTargetMeansBackupsAreOffAndSettingsFlowIntoTheOptions()
    {
        Assert.Null(BackupSettings.Create(null, null, 7, 4, 12, null));
        Assert.Null(BackupSettings.Create("  ", null, 7, 4, 12, null));

        var options = BackupSettings.Create(@" \\127.0.0.1\kalterm-no-such-share\KalTerm ", " D:\\stage ", 5, 3, 6, "02:30")!;

        Assert.Equal((@"\\127.0.0.1\kalterm-no-such-share\KalTerm", @"D:\stage", new TimeOnly(2, 30)), (options.TargetPath, options.StagingPath, options.RunAt));
        Assert.Equal((5, 3, 6), (options.Retention.Daily, options.Retention.Weekly, options.Retention.Monthly));
        Assert.Null(BackupSettings.Create("x", " ", 7, 4, 12, null)!.StagingPath);
        Assert.Equal(new TimeOnly(8, 0), BackupSettings.Create("x", null, 7, 4, 12, null)!.RunAt);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("8am")]
    [InlineData("")]
    public void ABadTimeOfDayIsRefused(string runAt)
    {
        Assert.Throws<InvalidOperationException>(() => BackupSettings.Create("x", null, 7, 4, 12, runAt));
    }

    [Fact]
    public void ARetentionThatCannotWorkIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => BackupSettings.Create("x", null, 0, 4, 12, null));
        Assert.True(BackupSettings.TryParseRunAt("7:05", out var time));
        Assert.Equal(new TimeOnly(7, 5), time);
    }
}
