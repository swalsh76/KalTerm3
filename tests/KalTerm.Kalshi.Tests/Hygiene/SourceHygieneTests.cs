using System.Text.RegularExpressions;

namespace KalTerm.Kalshi.Tests.Hygiene;

/// <summary>
/// CLAUDE.md: "Never hard-code localhost or Kalshi URLs outside the core configuration." Enforced here by scanning
/// the production sources, so the rule survives long after anyone remembers it.
/// </summary>
public partial class SourceHygieneTests
{
    private const string EndpointsFile = "KalshiEndpoints.cs";

    // A Kalshi host name (kalshi.com or kalshi.co), not the namespace KalTerm.Kalshi.Configuration.
    [GeneratedRegex(@"kalshi\.(?:com|co)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KalshiHost();

    [GeneratedRegex("localhost", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Localhost();

    [Fact]
    public void NoKalshiHostNameAppearsOutsideTheEndpointsFile()
    {
        var offenders = Offenders(SourceFiles(), KalshiHost(), exemptFileName: EndpointsFile);

        Assert.True(offenders.Count == 0, "Kalshi host names must live in KalshiEndpoints.cs only: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoLocalhostIsHardCodedAnywhereInTheProductionSources()
    {
        var offenders = Offenders(SourceFiles(), Localhost(), exemptFileName: null);

        Assert.True(offenders.Count == 0, "localhost must come from configuration, not code: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheEndpointsFileReallyHoldsTheKalshiHosts()
    {
        var endpoints = SourceFiles().Single(f => Path.GetFileName(f.Path) == EndpointsFile);

        Assert.Matches(KalshiHost(), endpoints.Text);
    }

    [Fact]
    public void TheScannerFindsViolationsWhenThereAreSome()
    {
        // Negative control: a scanner that can never fail would prove nothing.
        var files = new[]
        {
            (Path: @"src\A\Clean.cs", Text: "var x = 1;"),
            (Path: @"src\A\Bad.cs", Text: "var u = \"https://api.Kalshi.com/x\";"),
            (Path: @"src\A\BadDemo.cs", Text: "var u = \"wss://x.demo.kalshi.co\";"),
            (Path: @"src\A\Local.cs", Text: "var h = \"http://LocalHost:5000\";"),
            (Path: @"src\A\KalshiEndpoints.cs", Text: "\"https://api.kalshi.com\""),
        };

        Assert.Equal([@"src\A\Bad.cs", @"src\A\BadDemo.cs", @"src\A\KalshiEndpoints.cs"], Offenders(files, KalshiHost(), exemptFileName: null));
        Assert.Equal([@"src\A\Bad.cs", @"src\A\BadDemo.cs"], Offenders(files, KalshiHost(), exemptFileName: EndpointsFile));
        Assert.Equal([@"src\A\Local.cs"], Offenders(files, Localhost(), exemptFileName: null));
    }

    [Fact]
    public void TheNamespaceKalshiConfigurationIsNotAHostName()
    {
        // The regression this scanner first tripped over: "Kalshi.Co" begins "Kalshi.Configuration".
        var files = new[]
        {
            (Path: @"src\A\Settings.cs", Text: "namespace KalTerm.Kalshi.Configuration;\nusing KalTerm.Kalshi.Configuration;"),
            (Path: @"src\A\Core.cs", Text: "using KalTerm.Kalshi.Core;"),
        };

        Assert.Empty(Offenders(files, KalshiHost(), exemptFileName: null));
    }

    private static List<string> Offenders(IEnumerable<(string Path, string Text)> files, Regex pattern, string? exemptFileName) =>
        files
            .Where(f => exemptFileName is null || Path.GetFileName(f.Path) != exemptFileName)
            .Where(f => pattern.IsMatch(f.Text))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<(string Path, string Text)> SourceFiles()
    {
        var src = Path.Combine(RepositoryRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(p => (Path: p, Text: File.ReadAllText(p)))
            .ToList();

        Assert.True(files.Count > 5, "the scan found suspiciously few source files; is the repository root right?");
        return files;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KalTerm.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("KalTerm.sln not found above " + AppContext.BaseDirectory);
    }
}
