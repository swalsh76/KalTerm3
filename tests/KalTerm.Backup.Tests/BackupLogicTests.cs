using KalTerm.Backup;

namespace KalTerm.Backup.Tests;

internal static class Tk
{
    public static DateTime T(int year, int month, int day, int hour = 8) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    public static DayCounts Counts(params (string Day, long Ticker, long Trade)[] days) => new(
        days.ToDictionary(d => d.Day, d => d.Ticker, StringComparer.Ordinal),
        days.ToDictionary(d => d.Day, d => d.Trade, StringComparer.Ordinal));

    public static BackupManifest Manifest(string name, BackupKind kind, DateTime at, string? baseFull = null, long size = 1000, DayCounts? before = null, DayCounts? after = null, string hash = "AB") =>
        new(1, name, kind, baseFull, at, at.AddMinutes(1), "KalTerm", size, hash, "17.0 Developer", before ?? Counts(), after ?? Counts());

    public static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "kalterm-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public class BackupNamingTests
{
    [Fact]
    public void NamesRoundTripThroughTheirKindAndTime()
    {
        var at = Tk.T(2026, 10, 19, 8);

        var full = BackupNaming.FileName(BackupKind.Full, at);
        var diff = BackupNaming.FileName(BackupKind.Differential, at.AddDays(1));

        Assert.Equal("kalterm-full-20261019-080000.bak", full);
        Assert.Equal("kalterm-diff-20261020-080000.bak", diff);
        Assert.True(BackupNaming.TryParse(full, out var kind, out var parsed));
        Assert.Equal((BackupKind.Full, at), (kind, parsed));
        Assert.True(BackupNaming.TryParse(diff, out kind, out parsed));
        Assert.Equal((BackupKind.Differential, at.AddDays(1)), (kind, parsed));
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal("kalterm-full-20261019-080000.bak.json", BackupNaming.ManifestName(full));
    }

    [Theory]
    [InlineData("kalterm-full-20261019-080000.bak.partial")]
    [InlineData("kalterm-full-20261019-080000.txt")]
    [InlineData("kalterm-weekly-20261019-080000.bak")]
    [InlineData("kalterm-full-2026-10-19.bak")]
    [InlineData("other.bak")]
    [InlineData("")]
    public void OnlyOurOwnNamesAreRecognised(string name)
    {
        Assert.False(BackupNaming.TryParse(name, out _, out _));
    }

    [Fact]
    public void NullArgumentsAreRefusedOrNotRecognised()
    {
        Assert.False(BackupNaming.TryParse(null!, out _, out _));
        Assert.Throws<ArgumentNullException>(() => BackupNaming.ManifestName(null!));
    }
}

public class BackupSqlTests
{
    [Fact]
    public void TheBackupStatementAlwaysChecksumsAndCompressesUnlessTheEditionCannot()
    {
        Assert.Equal("BACKUP DATABASE [KalTerm] TO DISK = @path WITH CHECKSUM, COMPRESSION, INIT, FORMAT", SqlBackupDatabase.BackupSql("KalTerm", BackupKind.Full, compress: true));
        Assert.Equal("BACKUP DATABASE [KalTerm] TO DISK = @path WITH DIFFERENTIAL, CHECKSUM, NO_COMPRESSION, INIT, FORMAT", SqlBackupDatabase.BackupSql("KalTerm", BackupKind.Differential, compress: false));
        Assert.Equal(1844, SqlBackupDatabase.CompressionNotSupported);
    }
}

public class ManifestTests
{
    [Fact]
    public void AManifestRoundTripsThroughJsonWithItsCounts()
    {
        var manifest = Tk.Manifest("kalterm-diff-20261020-080000.bak", BackupKind.Differential, Tk.T(2026, 10, 20), baseFull: "kalterm-full-20261019-080000.bak", before: Tk.Counts(("2026-10-19", 10, 3)), after: Tk.Counts(("2026-10-19", 12, 4)));

        var json = manifest.ToJson();
        var back = BackupManifest.FromJson(json);

        Assert.Contains("\"kind\": \"differential\"", json, StringComparison.Ordinal);
        Assert.Contains("\"base_full\"", json, StringComparison.Ordinal);
        Assert.Equal(manifest.Name, back.Name);
        Assert.Equal((BackupKind.Differential, "kalterm-full-20261019-080000.bak", 1000L), (back.Kind, back.BaseFull, back.SizeBytes));
        Assert.Equal(12L, back.After.Ticker["2026-10-19"]);
        Assert.Equal(3L, back.Before.Trade["2026-10-19"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"name\": 5}")]
    [InlineData("null")]
    public void TextThatIsNotAManifestIsRefused(string text)
    {
        Assert.Throws<InvalidDataException>(() => BackupManifest.FromJson(text));
        Assert.Throws<ArgumentNullException>(() => BackupManifest.FromJson(null!));
    }

    [Fact]
    public void ARestoredCopyBetweenTheBeforeAndAfterCountsMatches()
    {
        var manifest = Tk.Manifest("kalterm-full-20261019-080000.bak", BackupKind.Full, Tk.T(2026, 10, 19), before: Tk.Counts(("2026-10-18", 100, 10), ("2026-10-19", 50, 5)), after: Tk.Counts(("2026-10-18", 100, 10), ("2026-10-19", 80, 9)));

        var settled = ManifestCheck.Compare(manifest, Tk.Counts(("2026-10-18", 100, 10), ("2026-10-19", 50, 5)));
        var inside = ManifestCheck.Compare(manifest, Tk.Counts(("2026-10-18", 100, 10), ("2026-10-19", 65, 7)));
        var edge = ManifestCheck.Compare(manifest, Tk.Counts(("2026-10-18", 100, 10), ("2026-10-19", 80, 9)));

        Assert.True(settled.Matches && inside.Matches && edge.Matches);
    }

    [Fact]
    public void ADayThatDidNotChangeMustMatchExactlyAndAnyDayOutsideItsRangeIsReported()
    {
        var manifest = Tk.Manifest("kalterm-full-20261019-080000.bak", BackupKind.Full, Tk.T(2026, 10, 19), before: Tk.Counts(("2026-10-18", 100, 10), ("2026-10-19", 50, 5)), after: Tk.Counts(("2026-10-18", 100, 10), ("2026-10-19", 80, 9)));

        var lost = ManifestCheck.Compare(manifest, Tk.Counts(("2026-10-18", 99, 10), ("2026-10-19", 60, 6)));
        var extra = ManifestCheck.Compare(manifest, Tk.Counts(("2026-10-18", 100, 10), ("2026-10-19", 60, 6), ("2026-10-17", 1, 0)));
        var missingDay = ManifestCheck.Compare(manifest, Tk.Counts(("2026-10-19", 60, 6)));

        Assert.False(lost.Matches);
        Assert.Equal("ticker 2026-10-18: restored 99, but the backup saw 100 before and 100 after", Assert.Single(lost.Differences));
        Assert.Contains("ticker 2026-10-17: restored 1", Assert.Single(extra.Differences), StringComparison.Ordinal);
        Assert.Equal(2, missingDay.Differences.Count);   // the 18th is gone from both tables
    }

    [Fact]
    public void ADayDroppedByRetentionDuringTheBackupMayBeEmptyOrWhole()
    {
        var manifest = Tk.Manifest("kalterm-full-20261019-080000.bak", BackupKind.Full, Tk.T(2026, 10, 19), before: Tk.Counts(("2026-09-19", 500, 50)), after: Tk.Counts());

        Assert.True(ManifestCheck.Compare(manifest, Tk.Counts()).Matches);
        Assert.True(ManifestCheck.Compare(manifest, Tk.Counts(("2026-09-19", 500, 50))).Matches);
        Assert.False(ManifestCheck.Compare(manifest, Tk.Counts(("2026-09-19", 501, 50))).Matches);
        Assert.Throws<ArgumentNullException>(() => ManifestCheck.Compare(null!, Tk.Counts()));
        Assert.Throws<ArgumentNullException>(() => ManifestCheck.Compare(manifest, null!));
    }
}

public class GfsRetentionTests
{
    private static BackupEntry Full(DateTime at) => new(BackupNaming.FileName(BackupKind.Full, at), BackupKind.Full, at, null);

    private static BackupEntry Diff(DateTime at, BackupEntry baseFull) => new(BackupNaming.FileName(BackupKind.Differential, at), BackupKind.Differential, at, baseFull.Name);

    /// <summary>Six months of Sunday fulls with daily differentials: the shape the schedule produces.</summary>
    private static List<BackupEntry> SixMonths(DateTime lastSunday)
    {
        var entries = new List<BackupEntry>();
        for (var week = 0; week < 26; week++)
        {
            var sunday = lastSunday.AddDays(-7 * week);
            var full = Full(sunday);
            entries.Add(full);
            for (var day = 1; day < 7; day++)
            {
                entries.Add(Diff(sunday.AddDays(day), full));
            }
        }

        return entries;
    }

    [Fact]
    public void GrandfatherFatherSonKeepsTheNewestWeeksTheMonthEndsAndTheRecentDifferentials()
    {
        var sunday = Tk.T(2026, 10, 18);
        var now = sunday.AddDays(3).AddHours(1);
        var entries = SixMonths(sunday).Where(e => e.At <= now).ToList();

        var keep = GfsRetention.Keep(entries, now, new GfsOptions());

        var fulls = entries.Where(e => e.Kind == BackupKind.Full).Where(e => keep.Contains(e.Name)).Select(e => e.At).OrderByDescending(a => a).ToList();
        Assert.Equal([sunday, sunday.AddDays(-7), sunday.AddDays(-14), sunday.AddDays(-21)], fulls.Take(4));   // fathers: the newest four weeks
        Assert.Contains(Tk.T(2026, 9, 27), fulls);   // grandfathers: the last full of September...
        Assert.Contains(Tk.T(2026, 8, 30), fulls);   // ...and of August
        Assert.DoesNotContain(Tk.T(2026, 9, 6), fulls);   // a week in the middle of a month that is no longer a father is dropped
        var diffs = entries.Where(e => e.Kind == BackupKind.Differential && keep.Contains(e.Name)).ToList();
        Assert.All(diffs, d => Assert.True(d.At > now.AddDays(-7)));
        Assert.Equal(6, diffs.Count);   // Monday to Wednesday after the newest full, and the last three days of the week before (the full they build on is a father)
    }

    [Fact]
    public void ADifferentialIsNeverKeptWithoutItsFullBackup()
    {
        var oldFull = Full(Tk.T(2026, 1, 4));
        var newFull = Full(Tk.T(2026, 10, 18));
        var orphan = Diff(Tk.T(2026, 10, 19), oldFull);
        var good = Diff(Tk.T(2026, 10, 19, 9), newFull);
        var noBase = new BackupEntry("kalterm-diff-20261019-100000.bak", BackupKind.Differential, Tk.T(2026, 10, 19, 10), null);

        var keep = GfsRetention.Keep([oldFull, newFull, orphan, good, noBase], Tk.T(2026, 10, 20), new GfsOptions { Weekly = 1, Monthly = 1 });

        Assert.Contains(newFull.Name, keep);
        Assert.Contains(good.Name, keep);
        Assert.DoesNotContain(oldFull.Name, keep);
        Assert.DoesNotContain(orphan.Name, keep);
        Assert.DoesNotContain(noBase.Name, keep);
    }

    [Fact]
    public void TheNewestFullBackupIsKeptEvenWhenEveryGenerationIsSetToOne()
    {
        var entries = new[] { Full(Tk.T(2026, 10, 4)), Full(Tk.T(2026, 10, 11)), Full(Tk.T(2026, 10, 18)) };

        var keep = GfsRetention.Keep(entries, Tk.T(2026, 10, 19), new GfsOptions { Daily = 1, Weekly = 1, Monthly = 1 });

        Assert.Equal([entries[2].Name], keep);
    }

    [Fact]
    public void NothingToKeepWhenThereAreNoBackupsAndBadOptionsAreRefused()
    {
        Assert.Empty(GfsRetention.Keep([], Tk.T(2026, 10, 19), new GfsOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => GfsRetention.Keep([], Tk.T(2026, 10, 19), new GfsOptions { Daily = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GfsOptions { Weekly = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GfsOptions { Monthly = 0 }.Validate());
        Assert.Throws<ArgumentNullException>(() => GfsRetention.Keep(null!, Tk.T(2026, 10, 19), new GfsOptions()));
        Assert.Throws<ArgumentNullException>(() => GfsRetention.Keep([], Tk.T(2026, 10, 19), null!));
    }
}

public sealed class BackupTargetTests : IDisposable
{
    private readonly string _dir = Tk.TempDir();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task WriteBackupAsync(BackupTarget target, BackupManifest manifest, byte[]? content = null)
    {
        await File.WriteAllBytesAsync(target.FilePath(manifest.Name), content ?? [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(target.FilePath(BackupNaming.ManifestName(manifest.Name)), manifest.ToJson(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ATargetWithoutTheMarkerIsRefusedUntilItIsInitialisedOnce()
    {
        var target = new BackupTarget(Path.Combine(_dir, "share"));

        var missing = Assert.Throws<BackupTargetException>(() => target.Check(0));
        Directory.CreateDirectory(target.Path);
        var noMarker = Assert.Throws<BackupTargetException>(() => target.Check(0));
        var first = await target.InitAsync(TestContext.Current.CancellationToken);
        var second = await target.InitAsync(TestContext.Current.CancellationToken);
        target.Check(0);

        Assert.Contains("cannot be reached", missing.Message, StringComparison.Ordinal);
        Assert.Contains("no marker file", noMarker.Message, StringComparison.Ordinal);
        Assert.True(first);
        Assert.False(second);
        Assert.True(File.Exists(Path.Combine(target.Path, BackupTarget.MarkerName)));
    }

    [Fact]
    public async Task TooLittleFreeSpaceIsRefusedWithBothSizes()
    {
        var target = new BackupTarget(Path.Combine(_dir, "share"));
        await target.InitAsync(TestContext.Current.CancellationToken);

        var failure = Assert.Throws<BackupTargetException>(() => target.Check(long.MaxValue));

        Assert.Contains("MB free but", failure.Message, StringComparison.Ordinal);
        Assert.True(BackupTarget.FreeBytes(_dir) > 0);
        Assert.Null(BackupTarget.FreeBytes(Path.Combine(_dir, "no", "such", "folder", "at", "all")));
    }

    [Fact]
    public async Task OnlyBackupsWithAReadableManifestAndTheirFileAreListedNewestFirst()
    {
        var target = new BackupTarget(_dir);
        await target.InitAsync(TestContext.Current.CancellationToken);
        var older = Tk.Manifest("kalterm-full-20261011-080000.bak", BackupKind.Full, Tk.T(2026, 10, 11));
        var newer = Tk.Manifest("kalterm-diff-20261012-080000.bak", BackupKind.Differential, Tk.T(2026, 10, 12), "kalterm-full-20261011-080000.bak");
        await WriteBackupAsync(target, older);
        await WriteBackupAsync(target, newer);
        await File.WriteAllBytesAsync(target.FilePath("kalterm-full-20261013-080000.bak"), [1], TestContext.Current.CancellationToken);   // no manifest: not a backup
        await File.WriteAllTextAsync(target.FilePath("kalterm-full-20261014-080000.bak.json"), "{}", TestContext.Current.CancellationToken);   // no file
        await File.WriteAllBytesAsync(target.FilePath("kalterm-full-20261015-080000.bak"), [1], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(target.FilePath("kalterm-full-20261015-080000.bak.json"), "garbage", TestContext.Current.CancellationToken);   // damaged manifest
        await File.WriteAllTextAsync(target.FilePath("kalterm-notes.bak.json"), "x", TestContext.Current.CancellationToken);

        var listed = await target.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([newer.Name, older.Name], listed.Select(m => m.Name));
        Assert.Empty(await new BackupTarget(Path.Combine(_dir, "nowhere")).ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteRemovesTheBackupAndItsManifestAndRefusesAnyOtherName()
    {
        var target = new BackupTarget(_dir);
        var manifest = Tk.Manifest("kalterm-full-20261011-080000.bak", BackupKind.Full, Tk.T(2026, 10, 11));
        await WriteBackupAsync(target, manifest);
        await File.WriteAllTextAsync(target.FilePath("precious.txt"), "keep me", TestContext.Current.CancellationToken);

        target.Delete(manifest.Name);

        Assert.False(File.Exists(target.FilePath(manifest.Name)));
        Assert.False(File.Exists(target.FilePath(BackupNaming.ManifestName(manifest.Name))));
        Assert.Throws<ArgumentException>(() => target.Delete("precious.txt"));
        Assert.Throws<ArgumentException>(() => target.Delete(@"..\kalterm-full-20261011-080000.bak"));
        Assert.True(File.Exists(target.FilePath("precious.txt")));
    }

    [Fact]
    public async Task UnfinishedFilesOfOurOwnNamingAreRemovedAndNothingElse()
    {
        var target = new BackupTarget(_dir);
        await File.WriteAllTextAsync(target.FilePath("kalterm-full-20261011-080000.bak.partial"), "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(target.FilePath("kalterm-full-20261011-080000.bak.json.partial"), "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(target.FilePath("other.partial"), "x", TestContext.Current.CancellationToken);

        Assert.Equal(2, target.DeletePartials());
        Assert.True(File.Exists(target.FilePath("other.partial")));
        Assert.Equal(0, new BackupTarget(Path.Combine(_dir, "nowhere")).DeletePartials());
    }

    [Fact]
    public async Task HashesAreUpperCaseSha256AndPathsAreKeptInsideTheTarget()
    {
        var target = new BackupTarget(_dir);
        await File.WriteAllTextAsync(target.FilePath("a.bin"), "abc", TestContext.Current.CancellationToken);

        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", await BackupTarget.HashAsync(target.FilePath("a.bin"), TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(_dir, "evil.bin"), target.FilePath(@"..\..\evil.bin"));
        Assert.Throws<ArgumentException>(() => new BackupTarget(" "));
        Assert.Throws<ArgumentException>(() => target.FilePath(" "));
    }
}

public sealed class ShareAccessTests : IDisposable
{
    private readonly string _dir = Tk.TempDir();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ACredentialIsStoredProtectedAndReadBackAndNeverShownAsText()
    {
        var store = new ShareCredentialStore(Path.Combine(_dir, "secrets", "share.cred"));
        Assert.Null(store.Load());

        store.Save(new ShareCredential("nas\\admin", "p@ss:word\nwith newline"));
        var loaded = store.Load();

        Assert.Equal(("nas\\admin", "p@ss:word\nwith newline"), (loaded!.UserName, loaded.Password));
        Assert.DoesNotContain("p@ss", RawText(Path.Combine(_dir, "secrets", "share.cred")), StringComparison.Ordinal);
        Assert.Equal("nas\\admin", loaded.ToString());
        store.Save(new ShareCredential("other", "x"));
        Assert.Equal("other", store.Load()!.UserName);
    }

    private static string RawText(string path) => System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));

    [Fact]
    public void TheCredentialFileIsOpenOnlyToSystemAndAdministrators()
    {
        var path = Path.Combine(_dir, "locked.cred");
        new ShareCredentialStore(path).Save(new ShareCredential("u", "p"));

        var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();

        Assert.All(rules, rule => Assert.False(rule.IsInherited));
        Assert.Equivalent(
            new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid }.Select(s => new System.Security.Principal.SecurityIdentifier(s, null).Value).Order(),
            rules.Select(r => r.IdentityReference.Value).Distinct().Order());
    }

    [Fact]
    public void ADamagedOrForeignCredentialFileIsReportedNotTrusted()
    {
        var path = Path.Combine(_dir, "bad.cred");
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.Throws<InvalidDataException>(() => new ShareCredentialStore(path).Load());
        Assert.Throws<ArgumentNullException>(() => new ShareCredentialStore(path).Save(null!));
        Assert.Throws<ArgumentException>(() => new ShareCredentialStore(path).Save(new ShareCredential(" ", "x")));
    }

    [Theory]
    [InlineData(@"\\127.0.0.1\kalterm-no-such-share", @"\\127.0.0.1\kalterm-no-such-share")]
    [InlineData(@"\\127.0.0.1\kalterm-no-such-share\KalTerm\sub", @"\\127.0.0.1\kalterm-no-such-share")]
    [InlineData(@"\\203.0.113.16\share\", @"\\203.0.113.16\share")]
    public void TheShareIsTheHostAndFirstFolderOfAUncPath(string unc, string share)
    {
        Assert.Equal(share, Win32ShareConnector.ShareRoot(unc));
    }

    [Fact]
    public void ConnectingToAShareThatDoesNotExistFailsWithTheWindowsErrorAndNeverWithThePassword()
    {
        var connector = new Win32ShareConnector();

        var failure = Assert.Throws<IOException>(() => connector.Connect(@"\\127.0.0.1\kalterm-no-such-share\folder", new ShareCredential("nobody", "never-shown-pw")));

        Assert.Contains(@"\\127.0.0.1\kalterm-no-such-share", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Windows error", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("never-shown-pw", failure.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => connector.Connect(@"\\127.0.0.1\x", null!));
    }

    [Theory]
    [InlineData(@"C:\backups")]
    [InlineData(@"\\onlyhost")]
    [InlineData("")]
    public void APathThatIsNotAUncShareIsRefused(string path)
    {
        Assert.Throws<ArgumentException>(() => Win32ShareConnector.ShareRoot(path));
        Assert.Throws<ArgumentNullException>(() => Win32ShareConnector.ShareRoot(null!));
    }
}
