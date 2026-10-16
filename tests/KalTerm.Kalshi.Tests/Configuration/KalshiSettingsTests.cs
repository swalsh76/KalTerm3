using KalTerm.Kalshi.Configuration;

namespace KalTerm.Kalshi.Tests.Configuration;

/// <summary>The reference's config tests (kalshi-core/tests/test_config.py and the rate-limit settings tests), ported.</summary>
public class KalshiSettingsTests
{
    private static KalshiSettings From(params (string Name, string? Value)[] variables) =>
        KalshiSettings.FromVariables(variables.ToDictionary(v => v.Name, v => v.Value));

    [Fact]
    public void DefaultsToDemo()
    {
        var settings = KalshiSettings.Default;

        Assert.Equal(KalshiEnvironment.Demo, settings.Environment);
        Assert.False(settings.IsProduction);
        Assert.Contains("demo", settings.RestUrl.Host, StringComparison.Ordinal);
        Assert.Contains("demo", settings.WebSocketUrl.Host, StringComparison.Ordinal);
        Assert.Null(settings.KeyId);
        Assert.Null(settings.PrivateKeyPath);
        Assert.Equal(200m, settings.ReadTokensPerSecond);
        Assert.Equal(100m, settings.WriteTokensPerSecond);
        Assert.Equal(0.9m, settings.RateLimitMargin);
    }

    [Fact]
    public void NothingSetMeansDemo()
    {
        Assert.Equal(KalshiSettings.Default, From());
        Assert.Equal(KalshiSettings.Default, KalshiSettings.FromEnvironment(_ => null));
    }

    [Theory]
    [InlineData("production")]
    [InlineData("PRODUCTION")]
    [InlineData("  Production  ")]
    public void ProductionRequiresTheExplicitWord(string value)
    {
        var settings = From((KalshiSettings.EnvironmentVariable, value));

        Assert.True(settings.IsProduction);
        Assert.Equal(KalshiEnvironment.Production, settings.Environment);
        Assert.Equal(KalshiEndpoints.Rest(KalshiEnvironment.Production), settings.RestUrl);
        Assert.Equal(KalshiEndpoints.WebSocket(KalshiEnvironment.Production), settings.WebSocketUrl);
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("prod")]
    [InlineData("live")]
    [InlineData("1")]
    [InlineData("true")]
    public void AnythingElseIsRefusedNotTreatedAsProductionOrDemo(string value)
    {
        var error = Assert.Throws<KalshiConfigurationException>(() => From((KalshiSettings.EnvironmentVariable, value)));

        Assert.Contains("KALSHI_ENV", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("demo")]
    [InlineData("DEMO")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DemoAndEmptyValuesMeanDemo(string? value)
    {
        Assert.Equal(KalshiEnvironment.Demo, From((KalshiSettings.EnvironmentVariable, value)).Environment);
    }

    [Fact]
    public void CredentialsComeFromTheVariables()
    {
        var settings = From(
            (KalshiSettings.KeyIdVariable, "abc"),
            (KalshiSettings.PrivateKeyPathVariable, @"C:\keys\k.pem"));

        Assert.Equal("abc", settings.KeyId);
        Assert.Equal(@"C:\keys\k.pem", settings.PrivateKeyPath);
    }

    [Fact]
    public void VariableNamesAreMatchedCaseInsensitivelyLikeWindowsDoes()
    {
        var settings = From(("kalshi_env", "production"), ("Kalshi_Key_Id", "abc"));

        Assert.True(settings.IsProduction);
        Assert.Equal("abc", settings.KeyId);
    }

    [Fact]
    public void BlankCredentialsAreTreatedAsUnset()
    {
        var settings = From((KalshiSettings.KeyIdVariable, "  "), (KalshiSettings.PrivateKeyPathVariable, string.Empty));

        Assert.Null(settings.KeyId);
        Assert.Null(settings.PrivateKeyPath);
    }

    [Fact]
    public void BudgetsAndMarginComeFromTheVariables()
    {
        var settings = From(
            (KalshiSettings.ReadTokensVariable, "600"),
            (KalshiSettings.WriteTokensVariable, "50.5"),
            (KalshiSettings.RateLimitMarginVariable, "0.5"));

        Assert.Equal(600m, settings.ReadTokensPerSecond);
        Assert.Equal(50.5m, settings.WriteTokensPerSecond);
        Assert.Equal(300m, settings.ReadBudget);
        Assert.Equal(25.25m, settings.WriteBudget);
    }

    [Fact]
    public void TheDefaultBudgetsApplyTheMargin()
    {
        Assert.Equal(180m, KalshiSettings.Default.ReadBudget);   // 200 x 0.9
        Assert.Equal(90m, KalshiSettings.Default.WriteBudget);   // 100 x 0.9
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("abc")]
    public void AMarginOutsideZeroToOneIsRefused(string value)
    {
        var error = Assert.Throws<KalshiConfigurationException>(() => From((KalshiSettings.RateLimitMarginVariable, value)));

        Assert.Contains("KALSHI_RATE_LIMIT_MARGIN", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMarginOfExactlyOneIsAllowed()
    {
        Assert.Equal(200m, From((KalshiSettings.RateLimitMarginVariable, "1")).ReadBudget);
    }

    [Theory]
    [InlineData(KalshiSettings.ReadTokensVariable, "0")]
    [InlineData(KalshiSettings.ReadTokensVariable, "-5")]
    [InlineData(KalshiSettings.ReadTokensVariable, "fast")]
    [InlineData(KalshiSettings.ReadTokensVariable, "1e3")]
    [InlineData(KalshiSettings.WriteTokensVariable, "0")]
    public void ABadBudgetIsRefusedByName(string name, string value)
    {
        var error = Assert.Throws<KalshiConfigurationException>(() => From((name, value)));

        Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyBudgetMeansUnsetSoTheDefaultApplies()
    {
        Assert.Equal(KalshiSettings.Default, From((KalshiSettings.WriteTokensVariable, string.Empty)));
    }

    [Fact]
    public void ABudgetThatTheMarginMakesTooFineIsRefused()
    {
        // 0.000001 x 0.5 = 0.0000005: finer than the token bucket's six decimal places.
        Assert.Throws<KalshiConfigurationException>(() => KalshiSettings.Create(readTokensPerSecond: 0.000001m, rateLimitMargin: 0.5m));
    }

    [Fact]
    public void AnUndefinedEnvironmentValueIsRefusedInCode()
    {
        Assert.Throws<KalshiConfigurationException>(() => KalshiSettings.Create((KalshiEnvironment)42));
    }

    [Fact]
    public void FromEnvironmentReadsExactlyTheKnownVariables()
    {
        var asked = new List<string>();
        var settings = KalshiSettings.FromEnvironment(name =>
        {
            asked.Add(name);
            return name == KalshiSettings.EnvironmentVariable ? "production" : null;
        });

        Assert.True(settings.IsProduction);
        string[] expected =
        [
            "KALSHI_ENV", "KALSHI_KEY_ID", "KALSHI_PRIVATE_KEY_PATH",
            "KALSHI_RATE_LIMIT_MARGIN", "KALSHI_READ_TOKENS_PER_SECOND", "KALSHI_WRITE_TOKENS_PER_SECOND",
        ];
        Assert.Equal(expected, asked.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => KalshiSettings.FromVariables(null!));
    }

    [Fact]
    public void SettingsAreValueEqual()
    {
        Assert.Equal(KalshiSettings.Create(keyId: "a"), KalshiSettings.Create(keyId: "a"));
        Assert.NotEqual(KalshiSettings.Create(keyId: "a"), KalshiSettings.Create(keyId: "b"));
    }
}
