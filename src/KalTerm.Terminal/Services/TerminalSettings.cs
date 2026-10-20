using System.Text.Json;

namespace KalTerm.Terminal.Services;

/// <summary>What the terminal remembers between runs: the server it last used. A convenience, so a file that cannot be read is simply forgotten.</summary>
/// <param name="path">The file.</param>
internal sealed class TerminalSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The usual place: <c>%APPDATA%\KalTerm\terminal.json</c>.</summary>
    public static string DefaultPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KalTerm", "terminal.json");

    /// <summary>The server used last, or <see langword="null"/>.</summary>
    public Uri? LoadServer()
    {
        try
        {
            if (!File.Exists(path) || JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Options) is not { Server: { Length: > 0 } text })
            {
                return null;
            }

            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Remembers the server.</summary>
    public void SaveServer(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(server.GetLeftPart(UriPartial.Authority)), Options));
        File.Move(temp, path, overwrite: true);
    }

    private sealed record Stored(string? Server);
}
