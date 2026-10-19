using System.Globalization;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;

namespace KalTerm.Security;

/// <summary>Locks files and folders down to SYSTEM and administrators.</summary>
public static class FileProtection
{
    /// <summary>Removes inherited access from a file and leaves only SYSTEM and administrators.</summary>
    /// <param name="file">The file.</param>
    public static void RestrictToAdministrators(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in AdministrativeSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        new FileInfo(file).SetAccessControl(security);
    }

    /// <summary>Removes inherited access from a folder (and, through inheritance, what it will hold) and leaves only SYSTEM and administrators.</summary>
    /// <param name="folder">The folder.</param>
    public static void RestrictFolderToAdministrators(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in AdministrativeSids())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(folder).SetAccessControl(security);
    }

    private static SecurityIdentifier[] AdministrativeSids() =>
        [new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)];
}

/// <summary>How certificate fingerprints are written and compared: the SHA-256 of the certificate, 64 upper-case hex digits (a client may type or paste colons or spaces).</summary>
public static class CertificateFingerprint
{
    /// <summary>The fingerprint of a certificate.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>64 upper-case hex digits.</returns>
    public static string Of(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    /// <summary>Reads a fingerprint as people write it (colons, spaces, any case).</summary>
    /// <param name="text">The text.</param>
    /// <param name="fingerprint">The 64 upper-case hex digits.</param>
    /// <returns>Whether the text is a fingerprint.</returns>
    public static bool TryNormalize(string? text, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (text is null)
        {
            return false;
        }

        var cleaned = new string(text.Where(c => c is not (':' or ' ' or '-')).ToArray()).ToUpperInvariant();
        if (cleaned.Length != 64 || !cleaned.All(Uri.IsHexDigit))
        {
            return false;
        }

        fingerprint = cleaned;
        return true;
    }

    /// <summary>A fingerprint grouped for reading: <c>AB:CD:...</c>.</summary>
    /// <param name="fingerprint">64 hex digits.</param>
    /// <returns>The grouped text.</returns>
    public static string Format(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2)));
    }
}

/// <summary>What a certificate says about itself.</summary>
/// <param name="Subject">Its subject.</param>
/// <param name="Fingerprint">Its SHA-256 fingerprint.</param>
/// <param name="NotBefore">When it becomes valid (UTC).</param>
/// <param name="NotAfter">When it expires (UTC).</param>
/// <param name="Names">The DNS names and IP addresses it is valid for.</param>
public sealed record CertificateInfo(string Subject, string Fingerprint, DateTime NotBefore, DateTime NotAfter, IReadOnlyList<string> Names)
{
    /// <summary>Describes a certificate.</summary>
    /// <param name="certificate">The certificate.</param>
    /// <returns>The description.</returns>
    public static CertificateInfo Of(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var names = new List<string>();
        foreach (var extension in certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(extension.EnumerateDnsNames());
            names.AddRange(extension.EnumerateIPAddresses().Select(ip => ip.ToString()));
        }

        return new CertificateInfo(certificate.Subject, CertificateFingerprint.Of(certificate), certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(), names);
    }
}

/// <summary>
/// The server's self-signed TLS certificate: made once on the server, kept in a folder only SYSTEM and administrators can open (the key's password is itself protected for the
/// machine), loaded by the API at start. Clients do not trust a certificate authority; they pin this certificate's fingerprint on first use.
/// </summary>
public static class ServerCertificate
{
    private const string FileName = "server.pfx";
    private const string PasswordFileName = "server.pfx.pw";
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("KalTerm TLS key password v1");

    /// <summary>Makes a self-signed certificate (ECDSA P-256) for the given names and addresses.</summary>
    /// <param name="dnsNames">The host names (at least one).</param>
    /// <param name="addresses">The IP addresses.</param>
    /// <param name="validity">How long it is valid.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The certificate with its private key.</returns>
    public static X509Certificate2 Create(IEnumerable<string> dnsNames, IEnumerable<IPAddress> addresses, TimeSpan validity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(dnsNames);
        ArgumentNullException.ThrowIfNull(addresses);
        var names = dnsNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ArgumentOutOfRangeException.ThrowIfEqual(names.Count, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validity, TimeSpan.Zero);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + names[0] + ", O=KalTerm"), key, HashAlgorithmName.SHA256);
        var alternative = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            alternative.AddDnsName(name);
        }

        foreach (var address in addresses.Distinct())
        {
            alternative.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(alternative.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));   // server authentication
        using var created = request.CreateSelfSigned(now.AddMinutes(-5), now + validity);
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>Whether a certificate has been stored in a folder.</summary>
    /// <param name="folder">The TLS folder.</param>
    /// <returns>True if both files are there.</returns>
    public static bool Exists(string folder) => File.Exists(System.IO.Path.Combine(folder, FileName)) && File.Exists(System.IO.Path.Combine(folder, PasswordFileName));

    /// <summary>Stores a certificate in a folder that only SYSTEM and administrators can open, replacing any earlier one.</summary>
    /// <param name="folder">The TLS folder (created if needed).</param>
    /// <param name="certificate">The certificate with its private key.</param>
    public static void Save(string folder, X509Certificate2 certificate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(certificate);
        Directory.CreateDirectory(folder);
        FileProtection.RestrictFolderToAdministrators(folder);
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pfx = certificate.Export(X509ContentType.Pfx, password);
        var protectedPassword = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.LocalMachine);
        WriteReplacing(System.IO.Path.Combine(folder, PasswordFileName), protectedPassword);
        WriteReplacing(System.IO.Path.Combine(folder, FileName), pfx);
    }

    /// <summary>Loads the stored certificate.</summary>
    /// <param name="folder">The TLS folder.</param>
    /// <returns>The certificate with its private key.</returns>
    /// <exception cref="InvalidDataException">There is none, or it cannot be read on this computer.</exception>
    public static X509Certificate2 Load(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Exists(folder))
        {
            throw new InvalidDataException($"there is no TLS certificate in {folder}; create one with 'kalterm-admin cert init'");
        }

        try
        {
            var password = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(System.IO.Path.Combine(folder, PasswordFileName)), Entropy, DataProtectionScope.LocalMachine));
            return X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(System.IO.Path.Combine(folder, FileName)), password, X509KeyStorageFlags.DefaultKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("the stored TLS certificate cannot be read on this computer", ex);
        }
    }

    private static void WriteReplacing(string path, byte[] content)
    {
        var temp = path + ".new";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The validity text used in messages.</summary>
    /// <param name="info">The certificate description.</param>
    /// <returns>For example <c>valid 2026-10-19 to 2028-10-18</c>.</returns>
    public static string Validity(CertificateInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return string.Create(CultureInfo.InvariantCulture, $"valid {info.NotBefore:yyyy-MM-dd} to {info.NotAfter:yyyy-MM-dd}");
    }
}
