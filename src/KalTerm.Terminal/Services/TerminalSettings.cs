using System.Text.Json;

namespace KalTerm.Terminal.Services;

/// <summary>What the terminal remembers between runs: the server it last used, and for each server the newest alert fire it has seen. A convenience, so a file that cannot be read is simply forgotten.</summary>
/// <param name="path">The file.</param>
internal sealed class TerminalSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The usual place: <c>%APPDATA%\KalTerm\terminal.json</c>.</summary>
    public static string DefaultPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KalTerm", "terminal.json");

    /// <summary>The server used last, or <see langword="null"/>.</summary>
    public Uri? LoadServer() =>
        Read().Server is { Length: > 0 } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;

    /// <summary>Remembers the server.</summary>
    public void SaveServer(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Write(Read() with { Server = server.GetLeftPart(UriPartial.Authority) });
    }

    /// <summary>The id of the newest alert fire this terminal has seen from a server, or <see langword="null"/> when it has seen none.</summary>
    /// <param name="serverKey">The server (its authority, as <see cref="KalTerm.Client.EndpointKey"/> gives it).</param>
    /// <returns>The id.</returns>
    public long? LoadLastFire(string serverKey) =>
        Read().LastFires is { } fires && fires.TryGetValue(serverKey, out var id) ? id : null;

    /// <summary>Remembers the newest alert fire seen from a server.</summary>
    /// <param name="serverKey">The server.</param>
    /// <param name="id">The fire's id.</param>
    public void SaveLastFire(string serverKey, long id)
    {
        var stored = Read();
        var fires = stored.LastFires is { } known ? new Dictionary<string, long>(known, StringComparer.Ordinal) : new Dictionary<string, long>(StringComparer.Ordinal);
        fires[serverKey] = id;
        Write(stored with { LastFires = fires });
    }

    private Stored Read()
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Options) is { } stored ? stored : new Stored(null, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Stored(null, null);
        }
    }

    private void Write(Stored stored)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, Options));
        File.Move(temp, path, overwrite: true);
    }

    private sealed record Stored(string? Server, Dictionary<string, long>? LastFires);
}
