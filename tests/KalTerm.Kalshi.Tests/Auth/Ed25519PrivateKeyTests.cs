using KalTerm.Kalshi.Auth;
using NSec.Cryptography;

namespace KalTerm.Kalshi.Tests.Auth;

/// <summary>Key loading and raw signing, checked against the Python reference (ed25519.json) and the RFC 8032 vectors.</summary>
public class Ed25519PrivateKeyTests
{
    private const string NotPem = "is not an unencrypted PEM private key";

    public static TheoryData<string> KeyFileNames()
    {
        var data = new TheoryData<string>();
        foreach (var k in VectorFile.Load("ed25519.json").GetProperty("keyFiles").EnumerateArray())
        {
            data.Add(k.GetProperty("name").GetString()!);
        }

        return data;
    }

    public static TheoryData<string> RawSignNames()
    {
        var data = new TheoryData<string>();
        foreach (var v in VectorFile.Load("ed25519.json").GetProperty("rawSign").EnumerateArray())
        {
            data.Add(v.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(KeyFileNames))]
    public void KeyFilesBehaveAsInTheReference(string name)
    {
        var entry = VectorFile.Load("ed25519.json").GetProperty("keyFiles").EnumerateArray()
            .Single(k => k.GetProperty("name").GetString() == name);
        var pem = entry.GetProperty("pem").GetString()!;

        switch (entry.GetProperty("expect").GetString())
        {
            case "ok":
                using (var key = Ed25519PrivateKey.FromPem(pem))
                {
                    Assert.Equal(entry.GetProperty("publicHex").GetString(), Convert.ToHexStringLower(key.GetPublicKey()));
                }

                break;
            case "unsupported-type":
                var unsupported = Assert.Throws<AuthException>(() => Ed25519PrivateKey.FromPem(pem));
                Assert.Contains("unsupported key type " + entry.GetProperty("family").GetString() + ":", unsupported.Message, StringComparison.Ordinal);
                Assert.Contains("only Ed25519 keys are supported", unsupported.Message, StringComparison.Ordinal);
                break;
            case "not-pem":
                var notPem = Assert.Throws<AuthException>(() => Ed25519PrivateKey.FromPem(pem));
                Assert.Contains(NotPem, notPem.Message, StringComparison.Ordinal);
                break;
            default:
                throw new InvalidOperationException($"unknown expectation for {name}");
        }
    }

    [Theory]
    [MemberData(nameof(RawSignNames))]
    public void SignaturesAreByteIdenticalToTheReference(string name)
    {
        var vector = VectorFile.Load("ed25519.json").GetProperty("rawSign").EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == name);
        var message = Convert.FromHexString(vector.GetProperty("messageHex").GetString()!);

        using var key = Ed25519PrivateKey.FromPem(vector.GetProperty("pkcs8Pem").GetString());
        var signature = key.Sign(message);

        Assert.Equal(vector.GetProperty("publicHex").GetString(), Convert.ToHexStringLower(key.GetPublicKey()));
        Assert.Equal(vector.GetProperty("signatureHex").GetString(), Convert.ToHexStringLower(signature));
        Assert.Equal(vector.GetProperty("signatureBase64").GetString(), Convert.ToBase64String(signature));
    }

    [Fact]
    public void ASignatureVerifiesWithThePublicKey()
    {
        using var key = Ed25519PrivateKey.FromPem(SampleVector("pkcs8Pem"));
        var message = "hello"u8.ToArray();

        var signature = key.Sign(message);

        var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, key.GetPublicKey(), KeyBlobFormat.RawPublicKey);
        Assert.Equal(64, signature.Length);
        Assert.True(SignatureAlgorithm.Ed25519.Verify(publicKey, message, signature));
        Assert.False(SignatureAlgorithm.Ed25519.Verify(publicKey, "hellp"u8, signature));
    }

    [Fact]
    public void ThePrivateKeyCannotBeExportedBackOut()
    {
        using var key = Ed25519PrivateKey.FromPem(SampleVector("pkcs8Pem"));

        Assert.Throws<InvalidOperationException>(() => key.UnderlyingKey.Export(KeyBlobFormat.PkixPrivateKey));
        Assert.Throws<InvalidOperationException>(() => key.UnderlyingKey.Export(KeyBlobFormat.RawPrivateKey));
        Assert.Equal(KeyExportPolicies.None, key.UnderlyingKey.ExportPolicy);
        Assert.Equal(32, key.GetPublicKey().Length);   // the public half stays available
    }

    [Fact]
    public void DisposingTheKeyWipesItAndStopsSigning()
    {
        var key = Ed25519PrivateKey.FromPem(SampleVector("pkcs8Pem"));
        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() => key.Sign([1, 2, 3]));
    }

    [Fact]
    public void SigningIsThreadSafeAndDeterministic()
    {
        using var key = Ed25519PrivateKey.FromPem(SampleVector("pkcs8Pem"));
        var message = "same message"u8.ToArray();
        var expected = Convert.ToHexStringLower(key.Sign(message));

        var results = new string[2_000];
        Parallel.For(0, results.Length, i => results[i] = Convert.ToHexStringLower(key.Sign(message)));

        Assert.All(results, r => Assert.Equal(expected, r));
    }

    [Fact]
    public void ToStringNeverShowsKeyMaterial()
    {
        var pem = SampleVector("pkcs8Pem");
        using var key = Ed25519PrivateKey.FromPem(pem);

        var text = key.ToString();

        Assert.Equal("Ed25519PrivateKey(redacted)", text);
        Assert.DoesNotContain(pem.Split('\n')[1][..20], text, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorMessagesNeverEchoKeyMaterial()
    {
        // A valid key under the wrong PEM label, and one with broken base64: the messages stay generic.
        var body = SampleVector("pkcs8Pem").Split('\n')[1];
        foreach (var name in new[] { "traditional-label", "broken-base64", "truncated-body", "encrypted-ed25519" })
        {
            var pem = VectorFile.Load("ed25519.json").GetProperty("keyFiles").EnumerateArray()
                .Single(k => k.GetProperty("name").GetString() == name).GetProperty("pem").GetString()!;

            var error = Assert.Throws<AuthException>(() => Ed25519PrivateKey.FromPem(pem));

            Assert.DoesNotContain(body[..16], error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSourceNamesTheKeyInErrorMessages()
    {
        var error = Assert.Throws<AuthException>(() => Ed25519PrivateKey.FromPem("nope", @"C:\keys\data.pem"));

        Assert.Equal(@"C:\keys\data.pem is not an unencrypted PEM private key", error.Message);
    }

    [Fact]
    public void NullPemIsRefusedAsNotAPem()
    {
        Assert.Throws<AuthException>(() => Ed25519PrivateKey.FromPem(null));
        Assert.Throws<ArgumentNullException>(() => Ed25519PrivateKey.FromPem("x", null!));
    }

    [Fact]
    public void ALoadedFileGivesTheSameKeyAsItsText()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("data.pem", SampleVector("pkcs8Pem"));

        using var fromFile = Ed25519PrivateKey.FromPemFile(path);
        using var fromText = Ed25519PrivateKey.FromPem(SampleVector("pkcs8Pem"));

        Assert.Equal(fromText.GetPublicKey(), fromFile.GetPublicKey());
    }

    [Fact]
    public void AMissingFileIsAnAuthErrorNamingThePath()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "missing.pem");

        var error = Assert.Throws<AuthException>(() => Ed25519PrivateKey.FromPemFile(path));

        Assert.StartsWith("cannot read private key file " + path, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryInPlaceOfAFileIsAnAuthError()
    {
        using var directory = new TempDirectory();

        var error = Assert.Throws<AuthException>(() => Ed25519PrivateKey.FromPemFile(directory.Path));

        Assert.Contains("cannot read private key file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithGarbageNamesTheFileInTheError()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("bad.pem", "not a key");

        var error = Assert.Throws<AuthException>(() => Ed25519PrivateKey.FromPemFile(path));

        Assert.Equal(path + " " + NotPem, error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankPathIsRefused(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => Ed25519PrivateKey.FromPemFile(path!));
    }

    private static string SampleVector(string property) =>
        VectorFile.Load("ed25519.json").GetProperty("rawSign")[3].GetProperty(property).GetString()!;
}
