using System.Net;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Rest;
using KalTerm.Trading.Transport;

namespace KalTerm.Trading.Tests.Transport;

public class TradingBudgetTests
{
    private const string Costs = """
        {"default_cost":10,"endpoint_costs":[
          {"method":"DELETE","path":"/trade-api/v2/portfolio/events/orders","cost":2},
          {"method":"DELETE","path":"/trade-api/v2/portfolio/events/orders/:order_id","cost":2},
          {"method":"DELETE","path":"/trade-api/v2/portfolio/events/orders/batched","cost":2},
          {"method":"GET","path":"/trade-api/v2/portfolio/orders/:order_id","cost":2},
          {"method":"DELETE","path":"/trade-api/v2/margin/orders/:order_id","cost":1}]}
        """;

    private const string Limits = """{"usage_tier":"basic","read":{"bucket_capacity":600,"refill_rate":200},"write":{"bucket_capacity":100,"refill_rate":100},"grants":[]}""";

    private static async Task<TradingBudget> ReadAsync(string costs, string limits, decimal margin = 0.9m)
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, limits).Respond(HttpStatusCode.OK, costs);
        using var signer = TransportKit.Signer();
        using var client = new KalshiRestClient(KalshiSettings.Default, new KalshiRestClientOptions { Signer = signer.Signer }, stub);
        var budget = await TradingBudget.ReadAsync(client, margin, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["/trade-api/v2/account/limits", "/trade-api/v2/account/endpoint_costs"], stub.Requests.Select(r => r.Uri.AbsolutePath));
        return budget;
    }

    [Fact]
    public async Task TheBudgetIsReadFromTheAccountAndScaledByTheMargin()
    {
        var budget = await ReadAsync(Costs, Limits);

        Assert.Equal((10m, 2m, 90m, 90m), (budget.DefaultCost, budget.CancelCost, budget.RefillRate, budget.Capacity));
    }

    [Fact]
    public async Task ARouteListedAboveTheDefaultRaisesTheCostAndTheHighestCancelWins()
    {
        const string Dear = """
            {"default_cost":10,"endpoint_costs":[
              {"method":"POST","path":"/trade-api/v2/portfolio/events/orders/:order_id/amend","cost":25},
              {"method":"DELETE","path":"/trade-api/v2/portfolio/events/orders","cost":2},
              {"method":"DELETE","path":"/trade-api/v2/portfolio/events/orders/batched","cost":3}]}
            """;

        var budget = await ReadAsync(Dear, Limits, 1m);

        Assert.Equal((25m, 10m, 100m, 100m), (budget.DefaultCost, budget.CancelCost, budget.RefillRate, budget.Capacity));
    }

    [Fact]
    public async Task ABiggerTierGivesABiggerBucket()
    {
        var budget = await ReadAsync(Costs, Limits.Replace("\"bucket_capacity\":100,\"refill_rate\":100", "\"bucket_capacity\":300,\"refill_rate\":300", StringComparison.Ordinal));

        Assert.Equal((270m, 270m), (budget.RefillRate, budget.Capacity));
    }

    [Fact]
    public async Task TheBudgetBuildsTheBucketAndTheOptionsTheClientUses()
    {
        var budget = await ReadAsync(Costs, Limits);

        using var bucket = budget.CreateBucket();
        var options = budget.ToOptions(bucket, allowProduction: true);

        Assert.Equal((90m, 90m), (bucket.TokensPerSecond, bucket.Capacity));
        Assert.Equal((10m, 2m, true), (options.DefaultCost, options.CancelCost, options.AllowProduction));
        Assert.Same(bucket, options.Bucket);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task AMarginOutsideZeroToOneIsRefused(int margin)
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, Limits).Respond(HttpStatusCode.OK, Costs);
        using var signer = TransportKit.Signer();
        using var client = new KalshiRestClient(KalshiSettings.Default, new KalshiRestClientOptions { Signer = signer.Signer }, stub);

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => TradingBudget.ReadAsync(client, margin, cancellationToken: TestContext.Current.CancellationToken));
    }
}
