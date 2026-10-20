using System.Text.Json;
using KalTerm.Trading.Risk;

namespace KalTerm.Trading.Tests.Risk;

/// <summary>
/// Differential tests: 1,544 scenarios (the reference's own cases and seeded random ones) with the Python reference's verdict on each (risk.json, made by tools/vectors/generate_risk.py). The port must agree on
/// whether the order is approved, every refusal code and message in order, the fee and the cost, the funding transfers and the kill-switch trip.
/// </summary>
public class RiskVectorTests
{
    private static readonly Lazy<JsonElement> Document = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vectors", "risk.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    });

    public static TheoryData<int> CaseNumbers()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Document.Value.GetProperty("cases").GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Fact]
    public void TheVectorFileHoldsBothApprovalsAndRefusals()
    {
        var cases = Document.Value.GetProperty("cases").EnumerateArray().ToList();

        Assert.True(cases.Count >= 1_500);
        Assert.True(cases.Count(c => c.GetProperty("expect").GetProperty("ok").GetBoolean()) > 200);
        Assert.True(cases.Count(c => !c.GetProperty("expect").GetProperty("ok").GetBoolean()) > 200);
        Assert.Contains(cases, c => c.GetProperty("expect").GetProperty("funding").GetArrayLength() > 1);
        Assert.Contains(cases, c => c.GetProperty("expect").GetProperty("tripKill").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(CaseNumbers))]
    public void TheReferenceAndThePortAgree(int number)
    {
        var scenario = Document.Value.GetProperty("cases")[number];
        var now = DateTimeOffset.Parse(Document.Value.GetProperty("now").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        var verdict = RiskChecker.Check(Intent(scenario.GetProperty("intent")), Snapshot(scenario, now), Policy(scenario.GetProperty("policy")), now, scenario.GetProperty("production").GetBoolean());

        var expect = scenario.GetProperty("expect");
        Assert.Equal(expect.GetProperty("ok").GetBoolean(), verdict.Ok);
        Assert.Equal(
            expect.GetProperty("refusals").EnumerateArray().Select(r => (r[0].GetString()!, r[1].GetString()!.Replace("risk.toml", "risk.json", StringComparison.Ordinal))),   // the one deliberate difference: the file is JSON here
            verdict.Refusals.Select(r => (RiskCodes.Name(r.Code), r.Message)));
        Assert.Equal(Nullable(expect.GetProperty("fee")), verdict.FeeE6);
        Assert.Equal(Nullable(expect.GetProperty("cost")), verdict.WorstCaseCostE6);
        Assert.Equal(
            expect.GetProperty("funding").EnumerateArray().Select(t => (t[0].GetInt32(), t[1].GetInt32(), t[2].GetInt64())),
            verdict.Funding.Select(t => (t.Source, t.Destination, t.AmountE6)));
        Assert.Equal(expect.GetProperty("tripKill").GetBoolean(), verdict.TripKill);
    }

    private static long? Nullable(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();

    private static OrderIntent Intent(JsonElement i) => new(
        i.GetProperty("ticker").GetString()!,
        i.GetProperty("shard").GetInt32(),
        i.GetProperty("action").GetString() == "buy" ? TradeAction.Buy : TradeAction.Sell,
        i.GetProperty("outcome").GetString() == "yes" ? TradeOutcome.Yes : TradeOutcome.No,
        i.GetProperty("count").GetInt64(),
        i.GetProperty("price").GetInt64(),
        Nullable(i.GetProperty("fee")));

    private static RiskPolicy Policy(JsonElement p)
    {
        var policy = new RiskPolicy();
        foreach (var property in p.EnumerateObject())
        {
            policy = property.Name switch
            {
                "max_order_count_e2" => policy with { MaxOrderCountE2 = property.Value.GetInt64() },
                "max_position_e2" => policy with { MaxPositionE2 = property.Value.GetInt64() },
                "max_order_notional_e6" => policy with { MaxOrderNotionalE6 = property.Value.GetInt64() },
                "max_open_exposure_e6" => policy with { MaxOpenExposureE6 = property.Value.GetInt64() },
                "max_daily_loss_e6" => policy with { MaxDailyLossE6 = property.Value.GetInt64() },
                "max_open_orders" => policy with { MaxOpenOrders = property.Value.GetInt32() },
                "max_transfer_per_order_e6" => policy with { MaxTransferPerOrderE6 = property.Value.GetInt64() },
                "max_transfer_per_day_e6" => policy with { MaxTransferPerDayE6 = property.Value.GetInt64() },
                "production_enabled" => policy with { ProductionEnabled = property.Value.GetBoolean() },
                _ => throw new InvalidOperationException("unknown policy key " + property.Name),
            };
        }

        return policy;
    }

    private static AccountSnapshot Snapshot(JsonElement s, DateTimeOffset now) => new(
        now - TimeSpan.FromSeconds(s.GetProperty("ageSeconds").GetInt32()),
        s.GetProperty("shards").EnumerateArray()
            .Select(x => new ShardState(x.GetProperty("index").GetInt32(), x.GetProperty("balance").GetInt64(), x.GetProperty("trading").GetBoolean(), x.GetProperty("transfers").GetBoolean()))
            .ToDictionary(x => x.Index),
        s.GetProperty("markets").EnumerateObject().ToDictionary(m => m.Name, m => new MarketState(m.Value[0].GetInt64(), m.Value[1].GetInt64(), m.Value[2].GetInt64())),
        s.GetProperty("openOrders").GetInt32(),
        s.GetProperty("exposure").GetInt64(),
        s.GetProperty("pnl").GetInt64(),
        s.GetProperty("transferred").GetInt64(),
        s.GetProperty("streamOk").GetBoolean(),
        s.GetProperty("halted").GetBoolean());
}
