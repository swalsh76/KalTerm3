using KalTerm.Backup;
using KalTerm.Data.State;

namespace KalTerm.Backup.Tests;

internal sealed class ScriptedBackups : IBackupService
{
    public List<BackupKind?> Runs { get; } = [];

    public BackupKind Kind { get; set; } = BackupKind.Full;

    public bool Succeed { get; set; } = true;

    public Exception? Throws { get; set; }

    public FakeState? State { get; set; }

    public FakeTime? Time { get; set; }

    public Task<BackupResult> RunAsync(BackupKind? force = null, CancellationToken cancellationToken = default)
    {
        Runs.Add(force);
        if (Throws is not null)
        {
            return Task.FromException<BackupResult>(Throws);
        }

        var now = Time!.Now;
        if (Succeed)
        {
            State!.Values[BackupStateKeys.LastGood] = new IngestStateEntry("kalterm-x.bak", now);
            State.Values[BackupStateKeys.Last] = new IngestStateEntry("ok", now);
            return Task.FromResult(new BackupResult(true, "kalterm-x.bak", Kind, 1, TimeSpan.Zero, 0, null));
        }

        State!.Values[BackupStateKeys.Last] = new IngestStateEntry("error it broke", now);
        return Task.FromResult(new BackupResult(false, null, null, 0, TimeSpan.Zero, 0, "it broke"));
    }
}

internal sealed class ScriptedRestores : IRestoreService
{
    public List<bool> Verifies { get; } = [];

    public Task<RestoreResult> RestoreAsync(string database, string? backupName, bool keep, bool physicalOnly, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> VerifyAsync(bool physicalOnly, CancellationToken cancellationToken = default)
    {
        Verifies.Add(physicalOnly);
        return Task.FromResult(true);
    }
}

public class BackupScheduleTests
{
    private readonly FakeState _state = new();
    private readonly FakeTime _time = new(Tk.T(2026, 10, 19, 7));
    private readonly ScriptedBackups _backups = new();
    private readonly ScriptedRestores _restores = new();

    private BackupSchedule Schedule(BackupOptions? options = null)
    {
        _backups.State = _state;
        _backups.Time = _time;
        return new BackupSchedule(_backups, _restores, _state, options ?? new BackupOptions { TargetPath = "T" }, _time);
    }

    [Fact]
    public async Task TheFirstEverBackupRunsAtOnceWhateverTheTimeOfDayAndTheTargetIsRecorded()
    {
        var ran = await Schedule().RunIfDueAsync(TestContext.Current.CancellationToken);

        Assert.True(ran);
        Assert.Single(_backups.Runs);
        Assert.Null(_backups.Runs[0]);
        Assert.Equal("T", _state.Values[BackupStateKeys.Configured].Value);
    }

    [Fact]
    public async Task AfterAGoodBackupNothingIsDueUntilTheNextDayAtTheConfiguredTime()
    {
        var schedule = Schedule();
        await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);   // 07:00 Monday
        _time.Now = Tk.T(2026, 10, 19, 20);
        var sameDay = await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);
        _time.Now = Tk.T(2026, 10, 20, 7);
        var tooEarly = await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);
        _time.Now = Tk.T(2026, 10, 20, 8);
        var due = await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);

        Assert.False(sameDay);
        Assert.False(tooEarly);
        Assert.True(due);
        Assert.Equal(2, _backups.Runs.Count);
    }

    [Fact]
    public async Task ABackupMissedBecauseTheHostWasDownIsTakenAtOnceEvenBeforeTheUsualTimeOfDay()
    {
        var schedule = Schedule();
        await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);   // Monday 07:00
        _time.Now = Tk.T(2026, 10, 20, 6).AddMinutes(30);   // Tuesday 06:30: before 08:00 and not 26 hours yet

        var notYet = await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);
        _time.Now = Tk.T(2026, 10, 21, 6);   // Wednesday 06:00: before 08:00 but 47 hours since the last good one
        var overdue = await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);

        Assert.False(notYet);
        Assert.True(overdue);
    }

    [Fact]
    public async Task AFailedAttemptWaitsHalfAnHourAndIsThenRetried()
    {
        _backups.Succeed = false;
        var schedule = Schedule();

        var first = await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);
        _time.Now = _time.Now.AddMinutes(29);
        var tooSoon = await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);
        _time.Now = _time.Now.AddMinutes(2);
        var retried = await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);

        Assert.True(first);
        Assert.False(tooSoon);
        Assert.True(retried);
        Assert.Equal(2, _backups.Runs.Count);
        Assert.Empty(_restores.Verifies);
    }

    [Fact]
    public async Task OnlyAFullBackupIsFollowedByAProofByRestoreAndAFailedOneIsNot()
    {
        var schedule = Schedule();
        await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);
        _backups.Kind = BackupKind.Differential;
        _time.Now = Tk.T(2026, 10, 20, 9);
        await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);
        _backups.Succeed = false;
        _backups.Kind = BackupKind.Full;
        _time.Now = Tk.T(2026, 10, 21, 9);
        await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, _backups.Runs.Count);
        Assert.Equal([true], _restores.Verifies);   // physical-only, after the first (full) backup only
    }

    [Fact]
    public async Task ATroubleWithTheServiceIsLoggedNotThrownAndCancellationIsNotSwallowed()
    {
        _backups.Throws = new InvalidOperationException("boom");
        var schedule = Schedule();

        var ran = await schedule.RunIfDueAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _backups.Throws = new OperationCanceledException(cts.Token);

        Assert.False(ran);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Schedule().RunIfDueAsync(cts.Token));
    }

    [Fact]
    public void MissingPartsAndBadOptionsAreRefused()
    {
        var options = new BackupOptions { TargetPath = "T" };

        Assert.Throws<ArgumentNullException>(() => new BackupSchedule(null!, _restores, _state, options));
        Assert.Throws<ArgumentNullException>(() => new BackupSchedule(_backups, null!, _state, options));
        Assert.Throws<ArgumentNullException>(() => new BackupSchedule(_backups, _restores, null!, options));
        Assert.Throws<ArgumentNullException>(() => new BackupSchedule(_backups, _restores, _state, null!));
        Assert.Throws<ArgumentException>(() => new BackupSchedule(_backups, _restores, _state, new BackupOptions()));
    }
}
