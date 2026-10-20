using KalTerm.Core.RateLimiting;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;

namespace KalTerm.Trading.Transport;

/// <summary>
/// The write budget of the account, read from Kalshi at run time instead of assumed: the tier's write bucket (<c>/account/limits</c>) and what the order routes cost (<c>/account/endpoint_costs</c>). The cost used for an order,
/// amend, decrease or transfer is the highest of the routes' costs (Kalshi lists none of them today, so the default of 10 applies); the cancel cost is the highest of the cancel routes'. Rounding up is the safe direction: the client
/// would rather wait a little than be rate limited.
/// </summary>
/// <param name="DefaultCost">Tokens an order, amend, decrease or transfer costs.</param>
/// <param name="CancelCost">Tokens a cancel costs (per order in a batch).</param>
/// <param name="RefillRate">Tokens per second the client lets itself spend (the tier's rate times the margin).</param>
/// <param name="Capacity">Tokens the bucket holds (the tier's capacity times the margin).</param>
public sealed record TradingBudget(decimal DefaultCost, decimal CancelCost, decimal RefillRate, decimal Capacity)
{
    /// <summary>Works out the budget from the account's answers.</summary>
    /// <param name="limits">The account's limits.</param>
    /// <param name="costs">The endpoint costs.</param>
    /// <param name="margin">The share of the budget to use, for example 0.9.</param>
    /// <param name="basePath">The API's base path, as Kalshi writes it in the cost list.</param>
    /// <returns>The budget.</returns>
    public static TradingBudget From(AccountLimits limits, EndpointCostTable costs, decimal margin, string basePath = "/trade-api/v2")
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(margin);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(margin, 1m);
        var order = basePath + "/portfolio/events/orders";
        var writes = new[]
        {
            costs.CostOf("POST", order),
            costs.CostOf("POST", order + "/:order_id/amend"),
            costs.CostOf("POST", order + "/:order_id/decrease"),
            costs.CostOf("POST", basePath + "/portfolio/intra_exchange_instance_transfer"),
        };
        var cancels = new[]
        {
            costs.CostOf("DELETE", order),
            costs.CostOf("DELETE", order + "/:order_id"),
            costs.CostOf("DELETE", order + "/batched"),
        };
        return new TradingBudget(writes.Max(), cancels.Max(), limits.Write.RefillRate * margin, limits.Write.BucketCapacity * margin);
    }

    /// <summary>Reads the account's limits and costs and works out the budget.</summary>
    /// <param name="client">The signed read client.</param>
    /// <param name="margin">The share of the budget to use.</param>
    /// <param name="basePath">The API's base path.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The budget.</returns>
    public static async Task<TradingBudget> ReadAsync(KalshiRestClient client, decimal margin, string basePath = "/trade-api/v2", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var limits = await client.AccountLimitsAsync(cancellationToken).ConfigureAwait(false);
        var costs = await client.EndpointCostsAsync(cancellationToken).ConfigureAwait(false);
        return From(limits, costs, margin, basePath);
    }

    /// <summary>A write bucket for this budget; the caller owns and disposes it.</summary>
    /// <param name="time">The clock; <see cref="TimeProvider.System"/> when omitted.</param>
    /// <returns>The bucket.</returns>
    public TokenBucket CreateBucket(TimeProvider? time = null) => new(RefillRate, Capacity, time);

    /// <summary>Options for the trading client with this budget's costs; pass the bucket from <see cref="CreateBucket"/> as <paramref name="bucket"/>.</summary>
    /// <param name="bucket">The bucket.</param>
    /// <param name="allowProduction">Whether production is allowed.</param>
    /// <param name="time">The clock.</param>
    /// <param name="audit">The audit log.</param>
    /// <returns>The options.</returns>
    public TradingClientOptions ToOptions(TokenBucket bucket, bool allowProduction, TimeProvider? time = null, ITradingAudit? audit = null) =>
        new() { Bucket = bucket, DefaultCost = DefaultCost, CancelCost = CancelCost, AllowProduction = allowProduction, TimeProvider = time, Audit = audit };
}
