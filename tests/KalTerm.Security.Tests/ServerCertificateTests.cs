using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using KalTerm.Security;

namespace KalTerm.Security.Tests;

public sealed class ServerCertificateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kalterm-tls-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static X509Certificate2 Make(params string[] names) =>
        ServerCertificate.Create(names.Length == 0 ? ["kaldb"] : names, [IPAddress.Parse("192.168.1.20")], TimeSpan.FromDays(730), Now);

    [Fact]
    public void ACertificateNamesTheHostItsAddressAndIsValidForTwoYears()
    {
        using var certificate = Make("kaldb", "localhost");

        var info = CertificateInfo.Of(certificate);

        Assert.Equal("CN=kaldb, O=KalTerm", info.Subject);
        Assert.Equal(["kaldb", "localhost", "192.168.1.20"], info.Names);
        Assert.Equal(Now.UtcDateTime.AddMinutes(-5), info.NotBefore);
        Assert.Equal(Now.UtcDateTime.AddDays(730), info.NotAfter);
        Assert.Equal("valid 2026-10-19 to 2028-10-18", ServerCertificate.Validity(info));
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal("ECDsa", certificate.GetKeyAlgorithm() == "1.2.840.10045.2.1" ? "ECDsa" : certificate.GetKeyAlgorithm());
    }

    [Fact]
    public void TwoCertificatesNeverShareAFingerprintAndTheFingerprintIsSixtyFourUpperCaseHexDigits()
    {
        using var first = Make();
        using var second = Make();

        var a = CertificateFingerprint.Of(first);

        Assert.NotEqual(a, CertificateFingerprint.Of(second));
        Assert.Matches("^[0-9A-F]{64}$", a);
        Assert.Equal(a, CertificateInfo.Of(first).Fingerprint);
        Assert.Throws<ArgumentNullException>(() => CertificateFingerprint.Of(null!));
    }

    [Theory]
    [InlineData("AB", false)]
    [InlineData("", false)]
    [InlineData("ZZ00000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF", true)]
    [InlineData("00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff:00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff", true)]
    [InlineData("00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF 00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF", true)]
    public void AFingerprintIsReadAsPeopleWriteItAndAnythingElseIsRefused(string text, bool valid)
    {
        Assert.Equal(valid, CertificateFingerprint.TryNormalize(text, out var normalized));
        if (valid)
        {
            Assert.Equal("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF", normalized);
            Assert.StartsWith("00:11:22:33:", CertificateFingerprint.Format(normalized), StringComparison.Ordinal);
            Assert.Equal(95, CertificateFingerprint.Format(normalized).Length);
        }

        Assert.False(CertificateFingerprint.TryNormalize(null, out _));
        Assert.Throws<ArgumentNullException>(() => CertificateFingerprint.Format(null!));
    }

    [Fact]
    public void ASavedCertificateLoadsBackWithTheSameFingerprintAndItsKeyAndTheFolderIsLockedDown()
    {
        using var certificate = Make();

        ServerCertificate.Save(_dir, certificate);
        using var loaded = ServerCertificate.Load(_dir);

        Assert.True(ServerCertificate.Exists(_dir));
        Assert.Equal(CertificateFingerprint.Of(certificate), CertificateFingerprint.Of(loaded));
        Assert.True(loaded.HasPrivateKey);
        var rules = new DirectoryInfo(_dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, rule => Assert.False(rule.IsInherited));
        Assert.Equivalent(
            new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid }.Select(s => new SecurityIdentifier(s, null).Value).Order(),
            rules.Select(r => r.IdentityReference.Value).Distinct().Order());
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".new", StringComparison.Ordinal));
    }

    [Fact]
    public void SavingAgainReplacesTheCertificateAsARotationDoes()
    {
        using var first = Make();
        using var second = Make();
        ServerCertificate.Save(_dir, first);

        ServerCertificate.Save(_dir, second);
        using var loaded = ServerCertificate.Load(_dir);

        Assert.Equal(CertificateFingerprint.Of(second), CertificateFingerprint.Of(loaded));
    }

    [Fact]
    public void LoadingWhatIsNotThereOrIsDamagedIsReportedWithAHint()
    {
        var missing = Assert.Throws<InvalidDataException>(() => ServerCertificate.Load(_dir));
        using var certificate = Make();
        ServerCertificate.Save(_dir, certificate);
        File.WriteAllBytes(Path.Combine(_dir, "server.pfx.pw"), [1, 2, 3]);
        var damaged = Assert.Throws<InvalidDataException>(() => ServerCertificate.Load(_dir));

        Assert.Contains("cert init", missing.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be read on this computer", damaged.Message, StringComparison.Ordinal);
        Assert.False(ServerCertificate.Exists(Path.Combine(_dir, "nowhere")));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerCertificate.Create([], [], TimeSpan.FromDays(1), Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerCertificate.Create([" "], [], TimeSpan.FromDays(1), Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerCertificate.Create(["kaldb"], [], TimeSpan.Zero, Now));
        Assert.Throws<ArgumentNullException>(() => ServerCertificate.Create(null!, [], TimeSpan.FromDays(1), Now));
        Assert.Throws<ArgumentNullException>(() => ServerCertificate.Create(["kaldb"], null!, TimeSpan.FromDays(1), Now));
        Assert.Throws<ArgumentNullException>(() => ServerCertificate.Save(_dir, null!));
        Assert.Throws<ArgumentNullException>(() => CertificateInfo.Of(null!));
        Assert.Throws<ArgumentNullException>(() => ServerCertificate.Validity(null!));
    }

    [Fact]
    public void FilesAndFoldersCanBeLockedDownToAdministrators()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "secret.bin");
        File.WriteAllBytes(file, [1]);

        FileProtection.RestrictToAdministrators(file);

        var rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(2, rules.Select(r => r.IdentityReference.Value).Distinct().Count());
        Assert.Throws<ArgumentException>(() => FileProtection.RestrictToAdministrators(" "));
        Assert.Throws<ArgumentException>(() => FileProtection.RestrictFolderToAdministrators(" "));
    }
}
