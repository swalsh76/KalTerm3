using System.Text.Json;

namespace KalTerm.TestSupport;

/// <summary>
/// Loads a golden-vector file generated from the Python reference (see tools/vectors). Files are read from the
/// <c>Vectors</c> folder next to the running test assembly, so each test project ships its own vectors.
/// </summary>
public static class VectorFile
{
    public static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vectors", name);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
