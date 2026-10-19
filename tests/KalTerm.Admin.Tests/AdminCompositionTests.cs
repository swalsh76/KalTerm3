using KalTerm.Admin;
using KalTerm.Backup;

namespace KalTerm.Admin.Tests;

public class AdminCompositionTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) => name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void WithoutABackupTargetTheServicesCannotBeBuiltAndTheErrorNamesTheSetting()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => AdminComposition.Create(Env()));

        Assert.Contains("KALTERM_Ingest__BackupTarget", failure.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => AdminComposition.Create(null!));
    }

    [Fact]
    public void ATargetAloneIsEnoughAndTheSettingsFlowIntoTheServices()
    {
        var plain = AdminComposition.Create(Env(("BackupTarget", @"\\127.0.0.1\kalterm-no-such-share\KalTerm")));
        var custom = AdminComposition.Create(Env(("BackupTarget", @"D:\backups"), ("BackupDaily", "3"), ("BackupWeekly", "2"), ("BackupMonthly", "6"), ("BackupAt", "03:15"), ("BackupCredentialPath", @"D:\x\cred"), ("Server", @"lpc:.\KALTERM"), ("Database", "KalTermTest"), ("BackupStaging", @"D:\stage")));

        Assert.Equal(@"\\127.0.0.1\kalterm-no-such-share\KalTerm", plain.Target.Path);
        Assert.Equal(@"D:\backups", custom.Target.Path);
        Assert.NotNull(custom.Backups);
        Assert.NotNull(custom.Restores);
        Assert.NotNull(custom.Credentials);
        Assert.IsType<Win32ShareConnector>(custom.Connector);
    }

    [Fact]
    public void ABadSettingIsRefusedBeforeAnythingRuns()
    {
        Assert.Throws<InvalidOperationException>(() => AdminComposition.Create(Env(("BackupTarget", "x"), ("BackupAt", "later"))));
        Assert.Throws<InvalidOperationException>(() => AdminComposition.Create(Env(("BackupTarget", "x"), ("BackupWeekly", "0"))));
    }
}
