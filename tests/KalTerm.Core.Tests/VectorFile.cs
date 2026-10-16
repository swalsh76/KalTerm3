using System.Text.Json;

namespace KalTerm.Core.Tests;

/// <summary>Loads a golden-vector file generated from the Python reference (see tools/vectors).</summary>
internal static class VectorFile
{
    internal static JsonElement Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vectors", name);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
