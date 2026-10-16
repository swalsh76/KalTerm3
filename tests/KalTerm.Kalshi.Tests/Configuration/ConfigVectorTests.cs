using System.Reflection;
using KalTerm.Kalshi.Configuration;

namespace KalTerm.Kalshi.Tests.Configuration;

/// <summary>
/// Differential tests against config.py itself (config.json, extracted from the reference source): a wrong URL, or
/// demo and production swapped, would send test traffic to the live exchange, so the URLs are checked against the original.
/// </summary>
public class ConfigVectorTests
{
    public static TheoryData<string, string, string> EndpointCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var environment in VectorFile.Load("config.json").GetProperty("environments").EnumerateObject())
        {
            data.Add(environment.Name, environment.Value.GetProperty("rest").GetString()!, environment.Value.GetProperty("webSocket").GetString()!);
        }

        return data;
    }

    public static TheoryData<decimal, decimal, decimal> BudgetCases()
    {
        var data = new TheoryData<decimal, decimal, decimal>();
        foreach (var b in VectorFile.Load("config.json").GetProperty("budgets").EnumerateArray())
        {
            data.Add(Number(b, "tokens"), Number(b, "margin"), Number(b, "budget"));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EndpointCases))]
    public void EndpointsMatchTheReference(string environmentName, string rest, string webSocket)
    {
        var environment = Enum.Parse<KalshiEnvironment>(environmentName, ignoreCase: true);

        Assert.Equal(rest, KalshiEndpoints.Rest(environment).AbsoluteUri);
        Assert.Equal(webSocket, KalshiEndpoints.WebSocket(environment).AbsoluteUri);
    }

    [Fact]
    public void EveryReferenceEnvironmentExistsHereAndViceVersa()
    {
        var reference = VectorFile.Load("config.json").GetProperty("environments").EnumerateObject()
            .Select(e => e.Name.ToUpperInvariant()).Order(StringComparer.Ordinal);
        var ours = Enum.GetNames<KalshiEnvironment>().Select(n => n.ToUpperInvariant()).Order(StringComparer.Ordinal);

        Assert.Equal(reference, ours);
    }

    [Fact]
    public void DefaultsMatchTheReference()
    {
        var defaults = VectorFile.Load("config.json").GetProperty("defaults");
        var settings = KalshiSettings.Default;

        Assert.Equal(defaults.GetProperty("env").GetString(), settings.Environment.ToString(), ignoreCase: true);
        Assert.Equal(Number(defaults, "read_tokens_per_second"), settings.ReadTokensPerSecond);
        Assert.Equal(Number(defaults, "write_tokens_per_second"), settings.WriteTokensPerSecond);
        Assert.Equal(Number(defaults, "rate_limit_margin"), settings.RateLimitMargin);
    }

    [Fact]
    public void ConstraintsMatchTheReference()
    {
        var constraints = VectorFile.Load("config.json").GetProperty("constraints");

        // Budgets: greater than zero.
        Assert.Equal(0m, Number(constraints.GetProperty("read_tokens_per_second"), "gt"));
        Assert.Equal(0m, Number(constraints.GetProperty("write_tokens_per_second"), "gt"));
        Assert.Throws<KalshiConfigurationException>(() => KalshiSettings.Create(readTokensPerSecond: 0));
        Assert.Throws<KalshiConfigurationException>(() => KalshiSettings.Create(writeTokensPerSecond: 0));

        // Margin: greater than zero and at most one (one itself is allowed).
        var margin = constraints.GetProperty("rate_limit_margin");
        Assert.Equal(0m, Number(margin, "gt"));
        Assert.Equal(1m, Number(margin, "le"));
        Assert.Throws<KalshiConfigurationException>(() => KalshiSettings.Create(rateLimitMargin: 0));
        Assert.Throws<KalshiConfigurationException>(() => KalshiSettings.Create(rateLimitMargin: 1.000001m));
        Assert.Equal(1m, KalshiSettings.Create(rateLimitMargin: 1).RateLimitMargin);
    }

    [Fact]
    public void EveryVariableUsesTheReferencePrefix()
    {
        var prefix = VectorFile.Load("config.json").GetProperty("variablePrefix").GetString()!;
        var names = typeof(KalshiSettings)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.Name.EndsWith("Variable", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(KalshiSettings.VariablePrefix, prefix);
        Assert.Equal(6, names.Count);
        Assert.All(names, name => Assert.StartsWith(prefix, name, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(BudgetCases))]
    public void BudgetsMatchTheReferenceFormula(decimal tokens, decimal margin, decimal expected)
    {
        var settings = KalshiSettings.Create(readTokensPerSecond: tokens, writeTokensPerSecond: tokens, rateLimitMargin: margin);

        Assert.Equal(expected, settings.ReadBudget);    // exact here; the reference's floats happen to agree on these
        Assert.Equal(expected, settings.WriteBudget);
    }

    private static decimal Number(System.Text.Json.JsonElement element, string property) =>
        decimal.Parse(element.GetProperty(property).GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
