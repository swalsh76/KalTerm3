using System.Reflection;

namespace KalTerm.Core;

/// <summary>Build metadata of the KalTerm.Core assembly.</summary>
public static class BuildInfo
{
    /// <summary>The informational version of this assembly, or "0.0.0" if none is stamped.</summary>
    public static string Version { get; } =
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
