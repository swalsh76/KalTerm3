using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KalTerm.Backup;

/// <summary>A user name and password for a network share.</summary>
/// <param name="UserName">The account, for example <c>navi\steven</c> or <c>steven</c>.</param>
/// <param name="Password">Its password.</param>
public sealed record ShareCredential(string UserName, string Password)
{
    /// <summary>Hides the password from logs and exceptions.</summary>
    /// <returns>The user name only.</returns>
    public override string ToString() => UserName;
}

/// <summary>Opens a connection to a network share with a credential, for as long as the returned object lives.</summary>
public interface IShareConnector
{
    /// <summary>Connects to the share that contains <paramref name="uncPath"/>.</summary>
    /// <param name="uncPath">A <c>\\host\share[\folder]</c> path.</param>
    /// <param name="credential">The account to connect as.</param>
    /// <returns>Disposing it ends the connection.</returns>
    /// <exception cref="IOException">The connection was refused.</exception>
    IDisposable Connect(string uncPath, ShareCredential credential);
}

/// <summary>
/// Keeps the share credential on the server, protected with the Windows data protection API for the <b>machine</b> (any process on this computer can read it, no other computer can),
/// in a file only SYSTEM and administrators should be able to read. The password never appears in the repository, in a configuration file or in the environment.
/// </summary>
/// <param name="path">The file.</param>
public sealed class ShareCredentialStore(string path)
{
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("KalTerm backup share credential v1");

    /// <summary>Stores a credential, replacing any earlier one.</summary>
    /// <param name="credential">The credential.</param>
    public void Save(ShareCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.UserName);
        var plain = Encoding.UTF8.GetBytes(credential.UserName + "\n" + credential.Password);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Reads the stored credential.</summary>
    /// <returns>The credential, or <see langword="null"/> when none is stored.</returns>
    /// <exception cref="InvalidDataException">The file is not a credential written by this computer.</exception>
    public ShareCredential? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("the stored share credential cannot be read on this computer", ex);
        }

        try
        {
            var text = Encoding.UTF8.GetString(plain);
            var split = text.IndexOf('\n', StringComparison.Ordinal);
            return split < 1 ? throw new InvalidDataException("the stored share credential is malformed") : new ShareCredential(text[..split], text[(split + 1)..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}

/// <summary><see cref="IShareConnector"/> over the Windows <c>WNetAddConnection2</c> call: a connection with no drive letter, for this process only as far as the caller is concerned.</summary>
public sealed partial class Win32ShareConnector : IShareConnector
{
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorSessionCredentialConflict = 1219;

    /// <inheritdoc/>
    public IDisposable Connect(string uncPath, ShareCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var share = ShareRoot(uncPath);
        var remote = Marshal.StringToHGlobalUni(share);
        try
        {
            var resource = new NetResource { Type = 1, RemoteName = remote };
            var result = AddConnection(ref resource, credential.Password, credential.UserName, 0);
            if (result == ErrorSessionCredentialConflict)
            {
                // The computer already has a connection to this server under another account; drop it and connect as the account we were given.
                _ = CancelConnection(share, 0, true);
                result = AddConnection(ref resource, credential.Password, credential.UserName, 0);
            }

            return result is 0 or ErrorAlreadyAssigned
                ? new Connection(share)
                : throw new IOException($"could not connect to {share} as {credential.UserName}: Windows error {result}");
        }
        finally
        {
            Marshal.FreeHGlobal(remote);
        }
    }

    /// <summary>The <c>\\host\share</c> part of a UNC path.</summary>
    /// <param name="uncPath">A UNC path.</param>
    /// <returns>The share.</returns>
    public static string ShareRoot(string uncPath)
    {
        ArgumentNullException.ThrowIfNull(uncPath);
        var parts = uncPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return uncPath.StartsWith(@"\\", StringComparison.Ordinal) && parts.Length >= 2
            ? @"\\" + parts[0] + @"\" + parts[1]
            : throw new ArgumentException($"{uncPath} is not a \\\\host\\share path", nameof(uncPath));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public IntPtr LocalName;
        public IntPtr RemoteName;
        public IntPtr Comment;
        public IntPtr Provider;
    }

    private static int AddConnection(ref NetResource resource, string password, string userName, int flags) => WNetAddConnection2(ref resource, password, userName, flags);

    private static int CancelConnection(string name, int flags, bool force) => WNetCancelConnection2(name, flags, force);

    [LibraryImport("mpr.dll", EntryPoint = "WNetAddConnection2W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WNetAddConnection2(ref NetResource resource, string password, string userName, int flags);

    [LibraryImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    private sealed class Connection(string share) : IDisposable
    {
        public void Dispose() => _ = CancelConnection(share, 0, false);
    }
}
