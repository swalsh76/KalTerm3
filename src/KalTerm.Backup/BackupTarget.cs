using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KalTerm.Backup;

/// <summary>The backup target cannot be used; the message says why.</summary>
public sealed class BackupTargetException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BackupTargetException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the target cannot be used.</param>
    public BackupTargetException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the target cannot be used.</param>
    /// <param name="innerException">The cause.</param>
    public BackupTargetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The folder the finished backups live in (a local folder, or <c>\\host\share\folder</c>). It must carry a marker file, written once by <see cref="InitAsync"/> with the target
/// present: an unmounted or unreachable share looks like an empty or missing folder, and without the marker a backup would silently fill the wrong disk.
/// </summary>
/// <param name="path">The folder.</param>
public sealed partial class BackupTarget(string path)
{
    /// <summary>The marker file's name.</summary>
    public const string MarkerName = ".kalterm-backup-target";

    /// <summary>The folder.</summary>
    public string Path { get; } = !string.IsNullOrWhiteSpace(path) ? path : throw new ArgumentException("the backup target path is empty", nameof(path));

    /// <summary>Creates the folder if needed and writes the marker. Does nothing if the marker is already there.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see langword="true"/> if the marker was written now.</returns>
    public async Task<bool> InitAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path);
        var marker = System.IO.Path.Combine(Path, MarkerName);
        if (File.Exists(marker))
        {
            return false;
        }

        await File.WriteAllTextAsync(marker, "KalTerm backup target " + Guid.NewGuid().ToString("N") + Environment.NewLine, Encoding.ASCII, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Checks that the target is the right place and can take <paramref name="requiredFreeBytes"/> more.</summary>
    /// <param name="requiredFreeBytes">The space needed.</param>
    /// <exception cref="BackupTargetException">The folder or marker is missing, nothing can be written, or there is not enough space.</exception>
    public void Check(long requiredFreeBytes)
    {
        if (!Directory.Exists(Path))
        {
            throw new BackupTargetException($"the backup target {Path} cannot be reached");
        }

        if (!File.Exists(System.IO.Path.Combine(Path, MarkerName)))
        {
            throw new BackupTargetException($"the backup target {Path} has no marker file ({MarkerName}); is the share mounted? Run 'backup init-target' once with it present");
        }

        var probe = System.IO.Path.Combine(Path, ".write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupTargetException($"the backup target {Path} cannot be written to: {ex.Message}", ex);
        }

        if (FreeBytes(Path) is { } free && free < requiredFreeBytes)
        {
            throw new BackupTargetException($"the backup target {Path} has {free / (1024 * 1024)} MB free but {requiredFreeBytes / (1024 * 1024)} MB are needed");
        }
    }

    /// <summary>The path of a file in the target.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>The full path.</returns>
    public string FilePath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return System.IO.Path.Combine(Path, System.IO.Path.GetFileName(name));
    }

    /// <summary>Every finished backup on the target: a file in our naming with a readable manifest next to it. Anything else is ignored.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The manifests, newest first.</returns>
    public async Task<IReadOnlyList<BackupManifest>> ListAsync(CancellationToken cancellationToken = default)
    {
        var found = new List<BackupManifest>();
        if (!Directory.Exists(Path))
        {
            return found;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(Path, "kalterm-*.bak.json"))
        {
            var name = System.IO.Path.GetFileName(manifestPath)[..^".json".Length];
            if (!BackupNaming.TryParse(name, out _, out _) || !File.Exists(FilePath(name)))
            {
                continue;
            }

            try
            {
                var manifest = BackupManifest.FromJson(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
                if (manifest.Name == name)
                {
                    found.Add(manifest);
                }
            }
            catch (InvalidDataException)
            {
                // Not a manifest: leave the file alone and do not count it as a backup.
            }
        }

        return [.. found.OrderByDescending(m => m.StartedAt)];
    }

    /// <summary>Deletes a backup and its manifest. Refuses any name that is not one of ours.</summary>
    /// <param name="name">The backup file's name.</param>
    public void Delete(string name)
    {
        if (!BackupNaming.TryParse(name, out _, out _) || name != System.IO.Path.GetFileName(name))
        {
            throw new ArgumentException($"{name} is not a backup file name", nameof(name));
        }

        File.Delete(FilePath(BackupNaming.ManifestName(name)));   // manifest first: a half-deleted backup must not look like a backup
        File.Delete(FilePath(name));
    }

    /// <summary>Removes unfinished files left by a crash (<c>*.partial</c> in our naming).</summary>
    /// <returns>How many were removed.</returns>
    public int DeletePartials()
    {
        if (!Directory.Exists(Path))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(Path, "kalterm-*" + BackupNaming.PartialSuffix))
        {
            File.Delete(file);
            removed++;
        }

        return removed;
    }

    /// <summary>Free space on the volume of a folder, local or UNC; <see langword="null"/> when it cannot be read.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The bytes available to the caller.</returns>
    public static long? FreeBytes(string folder) =>
        GetDiskFreeSpaceEx(folder, out var free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : null;

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    /// <summary>The SHA-256 of a file, as upper-case hex.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The hash.</returns>
    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
