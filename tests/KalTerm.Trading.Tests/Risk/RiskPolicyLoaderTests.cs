using KalTerm.Trading.Risk;

namespace KalTerm.Trading.Tests.Risk;

/// <summary>The risk file: the only way to change a limit, and strict about it.</summary>
public sealed class RiskPolicyLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kalterm-risk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void AMissingFileMeansTheTinyDefaults()
    {
        Assert.Equal(new RiskPolicy(), RiskPolicyLoader.Load(Path.Combine(_directory, "nothing", "risk.json")));
    }

    [Fact]
    public void AnEmptyObjectMeansTheDefaultsToo()
    {
        Assert.Equal(new RiskPolicy(), RiskPolicyLoader.Parse("{}"));
    }

    [Fact]
    public void EveryLimitIsReadInItsOwnUnit()
    {
        var policy = RiskPolicyLoader.Parse(
            """
            {
              "limits": {
                "max_order_count": "2.5", "max_position": 20, "max_order_notional": "0.75", "max_open_exposure": "10",
                "max_daily_loss": "4.5", "max_open_orders": 7, "max_transfer_per_order": "1.2345", "max_transfer_per_day": "9.000001",
                "snapshot_max_age_seconds": 20
              },
              "trading": { "production_enabled": true }
            }
            """);

        Assert.Equal((250L, 2_000L, 750_000L, 10_000_000L, 4_500_000L, 7), (policy.MaxOrderCountE2, policy.MaxPositionE2, policy.MaxOrderNotionalE6, policy.MaxOpenExposureE6, policy.MaxDailyLossE6, policy.MaxOpenOrders));
        Assert.Equal((1_234_500L, 9_000_001L, 20, true), (policy.MaxTransferPerOrderE6, policy.MaxTransferPerDayE6, policy.SnapshotMaxAgeSeconds, policy.ProductionEnabled));
    }

    [Fact]
    public void AFileOnDiskIsLoadedWithTheOtherLimitsLeftAtTheirDefaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "risk.json");
        File.WriteAllText(path, """{ "limits": { "max_open_orders": 1 } }""");

        var policy = RiskPolicyLoader.Load(path);

        Assert.Equal(1, policy.MaxOpenOrders);
        Assert.Equal(new RiskPolicy().MaxOrderNotionalE6, policy.MaxOrderNotionalE6);
    }

    [Theory]
    [InlineData("""{ "limits": { "max_order_notional": 1.5 } }""", "max_order_notional")]             // a JSON fraction would go through binary rounding
    [InlineData("""{ "limits": { "max_order_notional": 1e2 } }""", "max_order_notional")]
    [InlineData("""{ "limits": { "max_order_notional": "-1" } }""", "zero or more")]
    [InlineData("""{ "limits": { "max_order_notional": "1.0000001" } }""", "at most 6 decimal places")]
    [InlineData("""{ "limits": { "max_order_count": "0.001" } }""", "at most 2 decimal places")]
    [InlineData("""{ "limits": { "max_order_notional": "abc" } }""", "not a number")]
    [InlineData("""{ "limits": { "max_order_notional": true } }""", "max_order_notional")]
    [InlineData("""{ "limits": { "max_order_notional": "99999999999999999999" } }""", "too large")]
    [InlineData("""{ "limits": { "max_open_orders": -1 } }""", "whole number")]
    [InlineData("""{ "limits": { "max_open_orders": 2.5 } }""", "whole number")]
    [InlineData("""{ "limits": { "max_open_orders": "3" } }""", "whole number")]
    [InlineData("""{ "limits": { "snapshot_max_age_seconds": 1e1 } }""", "whole number")]
    [InlineData("""{ "limits": { "max_everything": 1 } }""", "unknown limit 'max_everything'")]
    [InlineData("""{ "trading": { "production_enabled": "yes" } }""", "true or false")]
    [InlineData("""{ "trading": { "ui_idle_minutes": 5 } }""", "unknown setting 'ui_idle_minutes'")]
    [InlineData("""{ "secrets": {} }""", "unknown section 'secrets'")]
    [InlineData("""{ "limits": 5 }""", "limits must be an object")]
    [InlineData("""[]""", "top level must be an object")]
    [InlineData("""{ "limits": { "max_open_orders": 1, "max_open_orders": 99 } }""", "'max_open_orders' appears twice")]
    [InlineData("""{ "limits": {}, "limits": {} }""", "'limits' appears twice")]
    [InlineData("{ not json", "not valid JSON")]
    [InlineData("{ // a comment\n}", "not valid JSON")]
    [InlineData("""{ "limits": { "max_open_orders": 1, } }""", "not valid JSON")]
    public void AMistakeIsAnErrorThatNamesTheKeyNeverSilentlyIgnored(string json, string expected)
    {
        var thrown = Assert.Throws<RiskConfigException>(() => RiskPolicyLoader.Parse(json, "risk.json"));

        Assert.Contains(expected, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatCannotBeReadIsAnErrorNamingTheFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "risk.json");
        using var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var thrown = Assert.Throws<RiskConfigException>(() => RiskPolicyLoader.Load(path));

        Assert.Contains(path, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultPlaceIsTheProfileFolderOfTheUser()
    {
        var path = RiskPolicyLoader.DefaultPath();

        Assert.EndsWith(Path.Combine("KalTerm", "risk.json"), path, StringComparison.Ordinal);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), path, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExceptionTypeHasTheUsualConstructors()
    {
        Assert.NotEmpty(new RiskConfigException().Message);
        Assert.Equal("x", new RiskConfigException("x").Message);
        var inner = new InvalidOperationException("inner");
        Assert.Same(inner, new RiskConfigException("x", inner).InnerException);
    }
}
