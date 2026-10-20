using KalTerm.Trading.Risk;
using static KalTerm.Trading.Tests.Risk.RiskTestKit;

namespace KalTerm.Trading.Tests.Risk;

/// <summary>The risk layer, rule by rule (the cases of the Python reference's own tests, plus the ones its fuzz test only implies).</summary>
public class RiskCheckerTests
{
    [Fact]
    public void ASmallOrderInsideEveryLimitIsApprovedWithItsExactWorstCaseCost()
    {
        var verdict = Verdict();

        Assert.True(verdict.Ok);
        Assert.NotNull(verdict.Approved);
        // 1 contract at $0.50: 0.07 x 1 x 0.25 = $0.0175, rounded up to a cent = $0.02
        Assert.Equal((20_000L, 520_000L), (verdict.FeeE6, verdict.WorstCaseCostE6));
        Assert.Empty(verdict.Funding);
        Assert.Empty(verdict.Refusals);
        Assert.Equal((verdict.FeeE6, verdict.WorstCaseCostE6, false), (verdict.Approved.FeeE6, verdict.Approved.WorstCaseCostE6, verdict.Approved.Production));
    }

    [Fact]
    public void TheFeeIsTakenAtTheWorstPriceTheLimitAllows()
    {
        // A buy limited at 0.90 can fill as low as 0.50, where the fee peaks; a buy at 0.20 cannot.
        Assert.Equal(RiskChecker.WorstCaseFeeE6(Order(price: 500_000)), RiskChecker.WorstCaseFeeE6(Order(price: 900_000)));
        Assert.Equal(RiskChecker.WorstCaseFeeE6(Order(price: 500_000, count: 100_000)), RiskChecker.WorstCaseFeeE6(Order(price: 900_000, count: 100_000)));
        Assert.True(RiskChecker.WorstCaseFeeE6(Order(price: 200_000, count: 100_000)) < RiskChecker.WorstCaseFeeE6(Order(price: 500_000, count: 100_000)));
        // A sell is the mirror image: limited at 0.10 it can fill as high as 0.50.
        Assert.Equal(20_000L, RiskChecker.WorstCaseFeeE6(Order(action: TradeAction.Sell, price: 100_000)));
        Assert.Null(RiskChecker.WorstCaseFeeE6(Order(fee: null)));
    }

    [Fact]
    public void AFeeSoLargeItDoesNotFitIsAnUnknownFeeNotACrash()
    {
        var huge = new OrderIntent("KXA-1", 0, TradeAction.Buy, TradeOutcome.Yes, long.MaxValue / 2, 500_000, long.MaxValue / 2);

        Assert.Null(RiskChecker.WorstCaseFeeE6(huge));
        Assert.Contains(RiskCode.UnknownFee, Codes(Verdict(huge)));
    }

    [Fact]
    public void SellingWhatYouHoldLocksNothingAndSellingShortLocksTheOtherSide()
    {
        var sell = Order(action: TradeAction.Sell, count: 300, price: 600_000);

        Assert.Equal(0L, RiskChecker.CollateralE6(sell, 300));   // a pure close
        Assert.Equal(2 * 400_000L, RiskChecker.CollateralE6(sell, 100));   // 2 uncovered = NO at 0.40
        Assert.Equal(3 * 400_000L, RiskChecker.CollateralE6(sell, -500));   // long NO does not cover YES
        var sellNo = Order(action: TradeAction.Sell, outcome: TradeOutcome.No, count: 100, price: 300_000);
        Assert.Equal(0L, RiskChecker.CollateralE6(sellNo, -100));
        Assert.Equal(700_000L, RiskChecker.CollateralE6(sellNo, 0));
    }

    [Fact]
    public void ABuyLocksCountTimesLimitRoundedUp()
    {
        Assert.Equal(1L, RiskChecker.CollateralE6(Order(count: 1, price: 1), 0));   // a hundredth of a contract at a millionth of a dollar
        Assert.Equal(500_000L, RiskChecker.CollateralE6(Order(count: 100, price: 500_000), 0));
    }

    public static TheoryData<string, RiskCode> EachLimit() => new()
    {
        { "count 6", RiskCode.OrderTooLarge },
        { "notional", RiskCode.NotionalTooLarge },
        { "orders", RiskCode.TooManyOrders },
        { "exposure", RiskCode.ExposureLimit },
        { "loss", RiskCode.DailyLoss },
        { "stream", RiskCode.StreamDown },
        { "halted", RiskCode.Halted },
        { "stale", RiskCode.StaleSnapshot },
        { "future", RiskCode.StaleSnapshot },
        { "shard", RiskCode.UnknownShard },
        { "fee", RiskCode.UnknownFee },
        { "inactive", RiskCode.ShardNotTrading },
    };

    [Theory]
    [MemberData(nameof(EachLimit))]
    public void EachLimitRefusesWithItsOwnCode(string what, RiskCode code)
    {
        var verdict = what switch
        {
            "count 6" => Verdict(Order(count: 600)),
            "notional" => Verdict(Order(count: 300, price: 900_000)),
            "orders" => Verdict(snapshot: Snapshot(openOrders: 3)),
            "exposure" => Verdict(snapshot: Snapshot(exposure: 2_900_000)),
            "loss" => Verdict(snapshot: Snapshot(pnl: -2_000_000)),
            "stream" => Verdict(snapshot: Snapshot(streamOk: false)),
            "halted" => Verdict(snapshot: Snapshot(halted: true)),
            "stale" => Verdict(snapshot: Snapshot(asOf: Now.AddSeconds(-11))),
            "future" => Verdict(snapshot: Snapshot(asOf: Now.AddMinutes(1))),
            "shard" => Verdict(Order(shard: 9)),
            "fee" => Verdict(Order(fee: null)),
            _ => Verdict(snapshot: Snapshot(shards: Shards(false, 5_000_000, 0, 0, 0))),
        };

        Assert.False(verdict.Ok);
        Assert.Null(verdict.Approved);
        Assert.Contains(code, Codes(verdict));
        Assert.Empty(verdict.Funding);
    }

    [Fact]
    public void TheLimitsAreInclusiveAtTheBoundary()
    {
        Assert.True(Verdict(Order(count: 500, price: 100_000)).Ok);   // exactly the size limit
        Assert.True(Verdict(snapshot: Snapshot(openOrders: 2)).Ok);   // the third order is allowed
        Assert.True(Verdict(snapshot: Snapshot(pnl: -1_999_999)).Ok);
        Assert.True(Verdict(snapshot: Snapshot(asOf: Now.AddSeconds(-10))).Ok);   // exactly as old as allowed
        Assert.True(Verdict(snapshot: Snapshot(asOf: Now.AddSeconds(5))).Ok);   // the clock may be a little behind
        Assert.False(Verdict(snapshot: Snapshot(asOf: Now.AddSeconds(6))).Ok);
    }

    [Fact]
    public void AMissingSnapshotRefusesEverything()
    {
        var verdict = RiskChecker.Check(Order(), null, new RiskPolicy(), Now, production: false);

        Assert.Contains(RiskCode.NoSnapshot, Codes(verdict));
        Assert.False(verdict.Ok);
        Assert.Null(verdict.WorstCaseCostE6);
    }

    [Fact]
    public void AllTheReasonsAreReportedNotJustTheFirst()
    {
        var verdict = Verdict(Order(count: 600), Snapshot(openOrders: 3, streamOk: false));

        Assert.True(Codes(verdict).IsSupersetOf([RiskCode.OrderTooLarge, RiskCode.TooManyOrders, RiskCode.StreamDown]));
    }

    [Fact]
    public void TheDailyLossLimitTripsTheKillSwitchAndNothingElseDoes()
    {
        Assert.True(Verdict(snapshot: Snapshot(pnl: -3_000_000)).TripKill);
        Assert.False(Verdict(Order(count: 600)).TripKill);
        Assert.False(Verdict().TripKill);
    }

    [Fact]
    public void PositionLimitsCountRestingOrdersAndNetAgainstTheOtherSide()
    {
        var policy = new RiskPolicy { MaxPositionE2 = 500, MaxOrderNotionalE6 = 5_000_000 };
        var held = Snapshot(markets: new() { ["KXA-1"] = new MarketState(PositionE2: 300, RestingYesE2: 100) });

        Assert.True(Verdict(Order(count: 100), held, policy).Ok);   // 300 + 100 + 100 = 500
        Assert.Contains(RiskCode.PositionLimit, Codes(Verdict(Order(count: 101), held, policy)));
        // Buying NO against a long YES position reduces it, so it is fine at any size within limits.
        Assert.True(Verdict(Order(outcome: TradeOutcome.No, count: 300), held, policy).Ok);
        // ...but buying more NO than the position leaves a net NO position that is itself limited.
        Assert.Contains(RiskCode.PositionLimit, Codes(Verdict(Order(outcome: TradeOutcome.No, count: 900), held, policy)));
        // Selling NO adds YES exactly like buying YES does.
        Assert.Contains(RiskCode.PositionLimit, Codes(Verdict(Order(action: TradeAction.Sell, outcome: TradeOutcome.No, count: 101), held, policy)));
    }

    [Fact]
    public void ProductionNeedsTheSwitchDemoDoesNot()
    {
        Assert.True(Verdict().Ok);
        Assert.Contains(RiskCode.ProductionDisabled, Codes(Verdict(production: true)));
        var verdict = Verdict(production: true, policy: new RiskPolicy { ProductionEnabled = true });
        Assert.True(verdict.Ok);
        Assert.True(verdict.Approved!.Production);
    }

    [Fact]
    public void AnUnfundedShardIsFundedFromTheRichestOtherShard()
    {
        var verdict = Verdict(Order(shard: 3), Snapshot(shards: Shards(3_000_000, 0, 1_000_000, 0)));

        Assert.True(verdict.Ok);
        Assert.Equal([new Transfer(0, 3, verdict.WorstCaseCostE6!.Value)], verdict.Funding);
    }

    [Fact]
    public void FundingCanComeFromSeveralShardsAndOnlyWhatIsNeeded()
    {
        var verdict = Verdict(Order(shard: 3), Snapshot(shards: Shards(300_000, 0, 250_000, 0)));   // needs 520,000

        Assert.True(verdict.Ok);
        Assert.Equal([(0, 300_000L), (2, 220_000L)], verdict.Funding.Select(t => (t.Source, t.AmountE6)));
        Assert.Equal(verdict.WorstCaseCostE6, verdict.Funding.Sum(t => t.AmountE6));
        // a shard that already has enough is never topped up
        Assert.Empty(Verdict(Order(shard: 0), Snapshot(shards: Shards(900_000, 5_000_000, 0, 0))).Funding);
    }

    [Fact]
    public void TransfersAreWholeCenticentsEvenWhenTheCostIsNot()
    {
        var verdict = Verdict(Order(shard: 1, count: 1, price: 333_333), Snapshot());

        Assert.True(verdict.Ok);
        Assert.All(verdict.Funding, t => Assert.Equal(0, t.AmountE6 % 100));
        Assert.True(verdict.Funding.Sum(t => t.AmountE6) >= verdict.WorstCaseCostE6);
    }

    [Fact]
    public void AnAccountThatCannotCoverTheOrderIsRefusedWithTheShortfall()
    {
        var verdict = Verdict(Order(shard: 3), Snapshot(shards: Shards(200_000, 100_000, 0, 0)));

        Assert.Contains(RiskCode.InsufficientFunds, Codes(verdict));
        Assert.Contains("$0.2200 short", verdict.Refusals[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FundingRespectsWhichShardsCanTransfer()
    {
        var source = Shards(5_000_000, 0, 0, 0);
        source[0] = new ShardState(0, 5_000_000, true, TransfersActive: false);
        Assert.Contains(RiskCode.InsufficientFunds, Codes(Verdict(Order(shard: 3), Snapshot(shards: source))));

        var target = Shards(5_000_000, 0, 0, 0);
        target[3] = new ShardState(3, 0, true, TransfersActive: false);
        Assert.Contains(RiskCode.InsufficientFunds, Codes(Verdict(Order(shard: 3), Snapshot(shards: target))));
    }

    [Fact]
    public void TransferCapsBoundWhatAnOrderCanMove()
    {
        var tight = new RiskPolicy { MaxTransferPerOrderE6 = 400_000, MaxTransferPerDayE6 = 1_000_000 };
        Assert.Contains(RiskCode.TransferLimit, Codes(Verdict(Order(shard: 3), Snapshot(), tight)));

        var roomy = new RiskPolicy { MaxTransferPerOrderE6 = 600_000, MaxTransferPerDayE6 = 1_000_000 };
        Assert.True(Verdict(Order(shard: 3), Snapshot(), roomy).Ok);
        Assert.Contains(RiskCode.TransferLimit, Codes(Verdict(Order(shard: 3), Snapshot(transferred: 600_000), roomy)));
    }

    [Fact]
    public void PlanFundingNeverOverdrawsASource()
    {
        var snapshot = Snapshot(shards: Shards(150_050, 99_999, 0, 0));

        Assert.Null(RiskChecker.PlanFunding(snapshot, 3, 260_000));   // only 249,900 is usable once balances are cut to whole centicents
        var plan = RiskChecker.PlanFunding(snapshot, 3, 200_000);
        Assert.NotNull(plan);
        Assert.All(plan, t =>
        {
            Assert.True(t.AmountE6 <= snapshot.Shards[t.Source].BalanceE6);
            Assert.Equal(0, t.AmountE6 % 100);
        });
        Assert.Empty(RiskChecker.PlanFunding(snapshot, 3, 0)!);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(100, 0)]
    [InlineData(100, 1_000_000)]
    [InlineData(100, -5)]
    public void AnInvalidOrderIsAProgrammingErrorNotAVerdict(long count, long price)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Order(count: count, price: price));
    }

    [Fact]
    public void ABlankTickerIsAProgrammingErrorToo()
    {
        Assert.ThrowsAny<ArgumentException>(() => Order(ticker: " "));
    }

    [Fact]
    public void AnApprovedOrderCannotBeBuiltOutsideTheRiskLayer()
    {
        var constructors = typeof(ApprovedOrder).GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        Assert.Empty(constructors);
        Assert.NotNull(Verdict().Approved);
    }

    [Fact]
    public void ARefusalNeverCarriesATransferEvenWhenItWasPlanned()
    {
        // The transfer cap refuses; the plan that broke the cap is not handed out as if it were usable.
        var tight = new RiskPolicy { MaxTransferPerOrderE6 = 100_000 };

        var verdict = Verdict(Order(shard: 3), Snapshot(), tight);

        Assert.False(verdict.Ok);
        Assert.Empty(verdict.Funding);
    }

    [Theory]
    [InlineData(5_000_000L, "$5.0000")]
    [InlineData(220_000L, "$0.2200")]
    [InlineData(333_333L, "$0.333333")]
    [InlineData(-1_500_000L, "-$1.5000")]
    [InlineData(0L, "$0.0000")]
    public void DollarsAreWrittenToFourPlacesOrSixWhenFiner(long micros, string expected)
    {
        Assert.Equal(expected, RiskChecker.DollarsText(micros));
    }

    [Theory]
    [InlineData(500L, "5")]
    [InlineData(50L, "0.5")]
    [InlineData(1_234L, "12.34")]
    [InlineData(1L, "0.01")]
    [InlineData(0L, "0")]
    public void CountsAreWrittenWithoutTrailingZeros(long hundredths, string expected)
    {
        Assert.Equal(expected, RiskChecker.CountText(hundredths));
    }

    [Fact]
    public void TheCodeNamesAreStableWireNames()
    {
        Assert.Equal("order_too_large", RiskCodes.Name(RiskCode.OrderTooLarge));
        Assert.Equal("market_not_tradable", RiskCodes.Name(RiskCode.MarketNotTradable));
        Assert.All(Enum.GetValues<RiskCode>(), code => Assert.Matches("^[a-z_]+$", RiskCodes.Name(code)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RiskCodes.Name((RiskCode)999));
    }

    [Fact]
    public void TheDefaultLimitsAreTinyAndProductionIsOffUntilTheFileSaysOtherwise()
    {
        var policy = new RiskPolicy();

        Assert.Equal((500L, 1_000L, 1_000_000L, 3_000_000L, 2_000_000L, 3), (policy.MaxOrderCountE2, policy.MaxPositionE2, policy.MaxOrderNotionalE6, policy.MaxOpenExposureE6, policy.MaxDailyLossE6, policy.MaxOpenOrders));
        Assert.Equal((2_000_000L, 5_000_000L, 10, false), (policy.MaxTransferPerOrderE6, policy.MaxTransferPerDayE6, policy.SnapshotMaxAgeSeconds, policy.ProductionEnabled));
    }
}
