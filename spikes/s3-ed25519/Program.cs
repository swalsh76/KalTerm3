using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

// Spike S-3: which Ed25519 library signs byte-identically to Python's `cryptography` and loads
// the same PKCS#8 PEM files? Throwaway code; the answer goes in docs/BUILD-LOG.md.

var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json"))).RootElement;

Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var baseTypes = typeof(System.Security.Cryptography.HashAlgorithm).Assembly.GetTypes()
    .Concat(typeof(ECDsa).Assembly.GetTypes())
    .Where(t => t.FullName!.Contains("Ed25519", StringComparison.OrdinalIgnoreCase)).Select(t => t.FullName).Distinct().ToList();
Console.WriteLine($"Ed25519 types in base library: {(baseTypes.Count == 0 ? "none" : string.Join(", ", baseTypes))}");
Console.WriteLine();

Candidate[] candidates = [new NSecCandidate(), new BouncyCastleCandidate()];
var exit = 0;
foreach (var c in candidates)
{
    Console.WriteLine($"=== {c.Name} ({c.Version}) ===");
    int pass = 0, fail = 0;
    foreach (var v in vectors.GetProperty("vectors").EnumerateArray())
    {
        var name = v.GetProperty("name").GetString()!;
        try
        {
            var key = c.Load(v.GetProperty("pkcs8Pem").GetString()!);
            var pub = Convert.ToHexString(c.PublicKey(key)).ToLowerInvariant();
            var sig = Convert.ToHexString(c.Sign(key, Convert.FromHexString(v.GetProperty("messageHex").GetString()!))).ToLowerInvariant();
            var b64 = Convert.ToBase64String(Convert.FromHexString(sig));
            var ok = pub == v.GetProperty("publicHex").GetString() && sig == v.GetProperty("signatureHex").GetString()
                     && b64 == v.GetProperty("signatureBase64").GetString();
            if (ok) pass++; else { fail++; Console.WriteLine($"  FAIL {name}: public/signature mismatch"); }
        }
        catch (Exception ex) { fail++; Console.WriteLine($"  FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
    }
    Console.WriteLine($"  positive vectors: {pass} passed, {fail} failed");

    int rejected = 0, accepted = 0;
    foreach (var n in vectors.GetProperty("negatives").EnumerateArray())
    {
        var name = n.GetProperty("name").GetString()!;
        try { c.Load(n.GetProperty("pem").GetString()!); accepted++; Console.WriteLine($"  FAIL negative {name}: was ACCEPTED"); }
        catch (Exception ex) { rejected++; Console.WriteLine($"  ok   negative {name}: rejected ({ex.GetType().Name})"); }
    }
    Console.WriteLine($"  negative files: {rejected} rejected, {accepted} wrongly accepted");

    // Latency: sign a Kalshi-shaped message.
    var first = vectors.GetProperty("vectors")[3];
    var latKey = c.Load(first.GetProperty("pkcs8Pem").GetString()!);
    var msg = System.Text.Encoding.UTF8.GetBytes("1700000000000POST/trade-api/v2/portfolio/orders");
    for (var i = 0; i < 2000; i++) c.Sign(latKey, msg);
    const int n2 = 20000;
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < n2; i++) c.Sign(latKey, msg);
    sw.Stop();
    Console.WriteLine($"  sign latency: {sw.Elapsed.TotalMilliseconds * 1000 / n2:F1} us/sign over {n2} signs");
    if (fail > 0 || accepted > 0) exit = 1;
    Console.WriteLine();
}

Console.WriteLine("Output directory contents (dependency footprint):");
foreach (var f in Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories).OrderBy(f => f))
{
    var rel = Path.GetRelativePath(AppContext.BaseDirectory, f);
    if (rel.StartsWith("S3.Ed25519", StringComparison.Ordinal) || rel == "vectors.json") continue;
    Console.WriteLine($"  {rel}  {new FileInfo(f).Length / 1024} KB");
}
return exit;

abstract class Candidate
{
    public abstract string Name { get; }
    public abstract string Version { get; }
    public abstract object Load(string pem);
    public abstract byte[] PublicKey(object key);
    public abstract byte[] Sign(object key, byte[] message);

    // Only an unencrypted PKCS#8 "PRIVATE KEY" block is acceptable, like the Python loader.
    protected static byte[] PrivateKeyDer(string pem)
    {
        if (!PemEncoding.TryFind(pem, out var fields) || !pem.AsSpan(fields.Label).SequenceEqual("PRIVATE KEY"))
            throw new FormatException("not an unencrypted PKCS#8 PRIVATE KEY PEM block");
        return Convert.FromBase64String(pem.AsSpan(fields.Base64Data).ToString());
    }

    protected static string AsmVersion(Type t) =>
        t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
}

sealed class NSecCandidate : Candidate
{
    private static readonly NSec.Cryptography.SignatureAlgorithm Alg = NSec.Cryptography.SignatureAlgorithm.Ed25519;
    public override string Name => "NSec.Cryptography";
    public override string Version => AsmVersion(typeof(NSec.Cryptography.Key));
    public override object Load(string pem) =>
        NSec.Cryptography.Key.Import(Alg, PrivateKeyDer(pem), NSec.Cryptography.KeyBlobFormat.PkixPrivateKey);
    public override byte[] PublicKey(object key) =>
        ((NSec.Cryptography.Key)key).PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);
    public override byte[] Sign(object key, byte[] message) => Alg.Sign((NSec.Cryptography.Key)key, message);
}

sealed class BouncyCastleCandidate : Candidate
{
    public override string Name => "BouncyCastle.Cryptography";
    public override string Version => AsmVersion(typeof(Ed25519Signer));
    public override object Load(string pem) =>
        PrivateKeyFactory.CreateKey(PrivateKeyDer(pem)) as Ed25519PrivateKeyParameters
        ?? throw new InvalidOperationException("not an Ed25519 key");
    public override byte[] PublicKey(object key) => ((Ed25519PrivateKeyParameters)key).GeneratePublicKey().GetEncoded();
    public override byte[] Sign(object key, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, (Ed25519PrivateKeyParameters)key);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }
}
