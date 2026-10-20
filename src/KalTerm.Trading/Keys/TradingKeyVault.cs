using System.Runtime.InteropServices;
using System.Text;

namespace KalTerm.Trading.Keys;

/// <summary>A trading key as it is kept: which Kalshi key id it is, and the private key as unencrypted PKCS#8 PEM text.</summary>
/// <param name="Environment">The Kalshi environment it is for: <c>demo</c> or <c>production</c>.</param>
/// <param name="KeyId">The id Kalshi issued for the key.</param>
/// <param name="Pem">The private key (Ed25519, PKCS#8 PEM).</param>
public sealed record TradingKeyEntry(string Environment, string KeyId, string Pem)
{
    /// <summary>Hides the private key from logs and debuggers.</summary>
    /// <returns>The environment and key id only.</returns>
    public override string ToString() => $"TradingKeyEntry({Environment}, {KeyId})";
}

/// <summary>Where trading keys are kept.</summary>
public interface ITradingKeyVault
{
    /// <summary>Stores a key, replacing the one kept for the same environment.</summary>
    /// <param name="entry">The key.</param>
    void Save(TradingKeyEntry entry);

    /// <summary>The key kept for an environment.</summary>
    /// <param name="environment">demo or production.</param>
    /// <returns>The key, or <see langword="null"/> when none is kept.</returns>
    TradingKeyEntry? Load(string environment);

    /// <summary>Forgets the key kept for an environment.</summary>
    /// <param name="environment">demo or production.</param>
    /// <returns>Whether there was one.</returns>
    bool Delete(string environment);
}

/// <summary>
/// The trading key in the Windows <b>Credential Manager</b> of the signed-in user (a generic credential per environment, named <c>KalTerm/trading-key/production</c> and so on; the user name field holds the
/// Kalshi key id, the secret the PEM). It is the user's own vault: another user, the server's service accounts and anything that cannot read this user's credentials cannot read it, and it is never a file in
/// the repository or on the server.
/// </summary>
public sealed partial class CredentialManagerKeyVault : ITradingKeyVault
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;   // ERROR_NOT_FOUND
    private const int MaxBlobBytes = 2_560;   // Credential Manager's limit for a secret

    private readonly string _prefix;

    /// <summary>Creates the vault.</summary>
    /// <param name="prefix">The start of each credential's name; tests use their own so they never touch a real key.</param>
    public CredentialManagerKeyVault(string prefix = "KalTerm/trading-key/")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        _prefix = prefix;
    }

    /// <inheritdoc/>
    public void Save(TradingKeyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RequireWindows();
        var blob = Encoding.UTF8.GetBytes(entry.Pem);
        if (blob.Length > MaxBlobBytes)
        {
            throw new ArgumentException($"the key is {blob.Length} bytes; Credential Manager keeps at most {MaxBlobBytes}", nameof(entry));
        }

        var target = Marshal.StringToCoTaskMemUni(TargetName(entry.Environment));
        var user = Marshal.StringToCoTaskMemUni(entry.KeyId);
        var comment = Marshal.StringToCoTaskMemUni("KalTerm trading key (Ed25519)");
        var secret = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, secret, blob.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = secret,
                Persist = PersistLocalMachine,
                UserName = user,
            };
            if (!CredWrite(in credential, 0))
            {
                throw new InvalidOperationException($"could not store the key in Credential Manager (Windows error {Marshal.GetLastPInvokeError()})");
            }
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, secret, blob.Length);   // do not leave the key in freed memory
            Marshal.FreeCoTaskMem(secret);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
            Marshal.FreeCoTaskMem(comment);
            Array.Clear(blob);
        }
    }

    /// <inheritdoc/>
    public TradingKeyEntry? Load(string environment)
    {
        RequireWindows();
        if (!CredRead(TargetName(environment), GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NotFound ? null : throw new InvalidOperationException($"could not read the key from Credential Manager (Windows error {error})");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            var pem = Encoding.UTF8.GetString(blob);
            Array.Clear(blob);
            return new TradingKeyEntry(environment, Marshal.PtrToStringUni(credential.UserName) ?? string.Empty, pem);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <inheritdoc/>
    public bool Delete(string environment)
    {
        RequireWindows();
        if (CredDelete(TargetName(environment), GenericCredential, 0))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        return error == NotFound ? false : throw new InvalidOperationException($"could not delete the key from Credential Manager (Windows error {error})");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the trading key is kept in the Windows Credential Manager");
        }
    }

    private string TargetName(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        return _prefix + environment.ToLowerInvariant();
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in NativeCredential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
