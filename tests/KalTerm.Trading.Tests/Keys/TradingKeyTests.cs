using System.Security.Cryptography;
using System.Text;
using KalTerm.Kalshi.Auth;
using KalTerm.Trading.Keys;
using NSec.Cryptography;

namespace KalTerm.Trading.Tests.Keys;

/// <summary>A key of the test's own making: nothing here is, or ever was, a real Kalshi key.</summary>
internal static class TestKeys
{
    public static string NewPem()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return new string(PemEncoding.Write("PRIVATE KEY", key.Export(KeyBlobFormat.PkixPrivateKey)));
    }
}

/// <summary>An in-memory vault, for the tests that are not about the Windows API.</summary>
internal sealed class MemoryKeyVault : ITradingKeyVault
{
    public Dictionary<string, TradingKeyEntry> Entries { get; } = [];

    public void Save(TradingKeyEntry entry) => Entries[entry.Environment] = entry;

    public TradingKeyEntry? Load(string environment) => Entries.GetValueOrDefault(environment);

    public bool Delete(string environment) => Entries.Remove(environment);
}

/// <summary>The real Windows Credential Manager, under a name of the test's own so a real trading key is never touched.</summary>
public sealed class CredentialManagerKeyVaultTests : IDisposable
{
    private readonly string _prefix = "KalTerm-test/" + Guid.NewGuid().ToString("N") + "/";
    private readonly CredentialManagerKeyVault _vault;

    public CredentialManagerKeyVaultTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the vault is the Windows Credential Manager");
        _vault = new CredentialManagerKeyVault(_prefix);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            _vault.Delete("demo");
            _vault.Delete("production");
        }
    }

    [Fact]
    public void AKeyIsStoredReadBackAndForgotten()
    {
        var pem = TestKeys.NewPem();

        _vault.Save(new TradingKeyEntry("demo", "key-id-1", pem));
        var loaded = _vault.Load("demo");

        Assert.NotNull(loaded);
        Assert.Equal(("demo", "key-id-1", pem), (loaded.Environment, loaded.KeyId, loaded.Pem));
        Assert.True(_vault.Delete("demo"));
        Assert.Null(_vault.Load("demo"));
        Assert.False(_vault.Delete("demo"));
    }

    [Fact]
    public void ANewKeyReplacesTheOldOneAndEnvironmentsAreKeptApart()
    {
        var first = TestKeys.NewPem();
        var second = TestKeys.NewPem();

        _vault.Save(new TradingKeyEntry("demo", "id-a", first));
        _vault.Save(new TradingKeyEntry("production", "id-p", first));
        _vault.Save(new TradingKeyEntry("demo", "id-b", second));

        Assert.Equal(("id-b", second), (_vault.Load("demo")!.KeyId, _vault.Load("demo")!.Pem));
        Assert.Equal(("id-p", first), (_vault.Load("production")!.KeyId, _vault.Load("production")!.Pem));
    }

    [Fact]
    public void AnEnvironmentWithoutAKeyHasNone()
    {
        Assert.Null(_vault.Load("production"));
    }

    [Fact]
    public void ANameIsNotCaseSensitive()
    {
        _vault.Save(new TradingKeyEntry("Demo", "id", TestKeys.NewPem()));

        Assert.NotNull(_vault.Load("DEMO"));
    }

    [Fact]
    public void AKeyTooLargeForTheVaultIsRefusedNotTruncated()
    {
        var thrown = Assert.Throws<ArgumentException>(() => _vault.Save(new TradingKeyEntry("demo", "id", new string('x', 3_000))));

        Assert.Contains("2560", thrown.Message, StringComparison.Ordinal);
        Assert.Null(_vault.Load("demo"));
    }

    [Fact]
    public void TheKeyNeverAppearsInTheTextOfAnEntry()
    {
        var pem = TestKeys.NewPem();

        Assert.DoesNotContain("PRIVATE", new TradingKeyEntry("demo", "id", pem).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(pem[30..60], new TradingKeyEntry("demo", "id", pem).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankPrefixOrEnvironmentIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new CredentialManagerKeyVault(" "));
        Assert.Throws<ArgumentException>(() => _vault.Load(" "));
    }
}

public class TradingKeyLoaderTests
{
    [Fact]
    public void AKeyThatIsStoredCanSignAndTheSignatureChecksOut()
    {
        var vault = new MemoryKeyVault();
        var pem = TestKeys.NewPem();
        TradingKeyLoader.Store(vault, "production", "kid-1", pem);

        using var trading = TradingKeyLoader.Load(vault, "production")!;
        var headers = trading.Signer.Headers("GET", "/trade-api/v2/portfolio/balance", 1_700_000_000_000);

        Assert.Equal(("production", "kid-1"), (trading.Environment, trading.Signer.KeyId));
        Assert.Equal("kid-1", headers[KalshiSigner.KeyHeader]);
        using var key = Key.Import(SignatureAlgorithm.Ed25519, PemEncoding.Find(pem).Base64Data is var range ? Convert.FromBase64String(pem[range]) : [], KeyBlobFormat.PkixPrivateKey);
        var message = Encoding.UTF8.GetBytes("1700000000000GET/trade-api/v2/portfolio/balance");
        Assert.True(SignatureAlgorithm.Ed25519.Verify(key.PublicKey, message, Convert.FromBase64String(headers[KalshiSigner.SignatureHeader])));
    }

    [Fact]
    public void NoKeyKeptMeansNoSigner()
    {
        Assert.Null(TradingKeyLoader.Load(new MemoryKeyVault(), "demo"));
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("")]
    [InlineData("-----BEGIN PUBLIC KEY-----\nMCowBQYDK2VwAyEAGb9ECWmEzf6FQbrBZ9w7lshQhqowtrbLDFw4rXAxZuE=\n-----END PUBLIC KEY-----")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----")]
    public void AKeyThatIsNotAnUnencryptedEd25519PrivateKeyIsRefusedBeforeItIsStored(string pem)
    {
        var vault = new MemoryKeyVault();

        Assert.Throws<AuthException>(() => TradingKeyLoader.Store(vault, "demo", "kid", pem));
        Assert.Empty(vault.Entries);
    }

    [Theory]
    [InlineData("staging", "kid")]
    [InlineData("demo", "")]
    [InlineData("demo", "has space")]
    public void AnUnusableEnvironmentOrKeyIdIsRefused(string environment, string keyId)
    {
        Assert.Throws<AuthException>(() => TradingKeyLoader.Store(new MemoryKeyVault(), environment, keyId, TestKeys.NewPem()));
    }

    [Fact]
    public void AStoredEnvironmentIsKeptInLowerCaseAndLoadedWhateverTheCase()
    {
        var vault = new MemoryKeyVault();
        TradingKeyLoader.Store(vault, "DEMO", "kid", TestKeys.NewPem());

        Assert.Contains("demo", vault.Entries.Keys);
        using var trading = TradingKeyLoader.Load(vault, "Demo");
        Assert.NotNull(trading);
        Assert.Throws<AuthException>(() => TradingKeyLoader.Load(vault, "staging"));
    }

    [Fact]
    public void AKeptKeyThatHasGoneBadIsReportedNamingTheEnvironment()
    {
        var vault = new MemoryKeyVault();
        vault.Save(new TradingKeyEntry("demo", "kid", "garbage"));

        var thrown = Assert.Throws<AuthException>(() => TradingKeyLoader.Load(vault, "demo"));

        Assert.Contains("demo trading key", thrown.Message, StringComparison.Ordinal);
    }
}
