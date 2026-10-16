using KalTerm.Kalshi.Auth;

namespace KalTerm.Kalshi.Tests.Auth;

/// <summary>
/// Differential tests: the reference's own <c>KalshiSigner.headers()</c> produced every expected header set
/// (ed25519.json) over methods, query strings, non-ASCII and empty paths, and timestamps.
/// </summary>
public class SignerVectorTests
{
    public static TheoryData<string, string, string, string, long> HeaderCases()
    {
        var data = new TheoryData<string, string, string, string, long>();
        foreach (var c in VectorFile.Load("ed25519.json").GetProperty("headers").EnumerateArray())
        {
            data.Add(
                c.GetProperty("signer").GetString()!,
                c.GetProperty("keyId").GetString()!,
                c.GetProperty("method").GetString()!,
                c.GetProperty("path").GetString()!,
                c.GetProperty("timestamp").GetInt64());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HeaderCases))]
    public void HeadersAreIdenticalToTheReference(string signerName, string keyId, string method, string path, long timestamp)
    {
        var vectors = VectorFile.Load("ed25519.json");
        var pem = vectors.GetProperty("signers").EnumerateArray().Single(s => s.GetProperty("name").GetString() == signerName).GetProperty("pkcs8Pem").GetString();
        var expected = vectors.GetProperty("headers").EnumerateArray()
            .Single(c => c.GetProperty("signer").GetString() == signerName
                         && c.GetProperty("method").GetString() == method
                         && c.GetProperty("path").GetString() == path
                         && c.GetProperty("timestamp").GetInt64() == timestamp)
            .GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

        using var key = Ed25519PrivateKey.FromPem(pem);
        var actual = new KalshiSigner(keyId, key).Headers(method, path, timestamp);

        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), actual.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void TheVectorFileHasTheExpectedBreadth()
    {
        var file = VectorFile.Load("ed25519.json");

        Assert.Equal(168, file.GetProperty("headers").GetArrayLength());
        Assert.Equal(21, file.GetProperty("rawSign").GetArrayLength());
        Assert.True(file.GetProperty("keyFiles").GetArrayLength() >= 18);
        Assert.Matches("^[0-9a-f]{40}$", file.GetProperty("referenceCommit").GetString());
    }
}
