using System.Text.RegularExpressions;

namespace KalTerm.Trading.Tests.Risk;

/// <summary>
/// An <c>ApprovedOrder</c> is the proof that the risk layer approved an order, and the order service will accept nothing else. Its constructor is internal, so nothing outside the assembly can build one; this
/// scan keeps the code inside the assembly honest too: the only place that may say <c>new ApprovedOrder(</c> is the risk checker.
/// </summary>
public partial class ApprovedOrderHygieneTests
{
    [GeneratedRegex(@"new\s+ApprovedOrder\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex Construction();

    [Fact]
    public void OnlyTheRiskCheckerBuildsAnApprovedOrder()
    {
        var root = FindRepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => Construction().IsMatch(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["RiskChecker.cs"], offenders);
    }

    [Fact]
    public void NoOtherAssemblyCanSeeTheInternalConstructor()
    {
        var attributes = typeof(KalTerm.Trading.Risk.RiskChecker).Assembly.GetCustomAttributes(typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute), inherit: false);

        Assert.Empty(attributes);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KalTerm.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("KalTerm.sln was not found above the test assembly");
    }
}
