using System.Text.Json.Serialization;
using KalTerm.Core.Money;

namespace KalTerm.Kalshi.Models;

/// <summary>One leg of a multivariate (combo) market.</summary>
/// <param name="EventTicker">The leg's event.</param>
/// <param name="MarketTicker">The leg's market.</param>
/// <param name="Side">The side taken, <c>yes</c> or <c>no</c>.</param>
/// <param name="YesSettlementValueDollars">Set once the leg has settled.</param>
public sealed record MveSelectedLeg(
    string EventTicker,
    string MarketTicker,
    string Side,
    Dollars? YesSettlementValueDollars = null);

/// <summary>
/// A market. Prices are <see cref="Dollars"/>, counts are <see cref="Contracts"/> and strikes are <see cref="Level"/>, all exact;
/// Kalshi adds fields over time, so unknown fields are ignored and only the ones the project uses are modelled. The first four
/// fields are required; everything else may be absent.
/// </summary>
/// <param name="Ticker">The market ticker.</param>
/// <param name="EventTicker">The event it belongs to.</param>
/// <param name="MarketType">For example <c>binary</c>.</param>
/// <param name="Status">For example <c>active</c>.</param>
/// <param name="YesSubTitle">Label of the yes side.</param>
/// <param name="NoSubTitle">Label of the no side.</param>
/// <param name="CreatedTime">When the market was created.</param>
/// <param name="UpdatedTime">When the market last changed.</param>
/// <param name="OpenTime">When trading opens.</param>
/// <param name="CloseTime">When trading closes.</param>
/// <param name="LatestExpirationTime">The latest the market can expire.</param>
/// <param name="YesBidDollars">Best yes bid.</param>
/// <param name="YesAskDollars">Best yes ask.</param>
/// <param name="NoBidDollars">Best no bid.</param>
/// <param name="NoAskDollars">Best no ask.</param>
/// <param name="YesBidSizeFp">Size at the best yes bid.</param>
/// <param name="YesAskSizeFp">Size at the best yes ask.</param>
/// <param name="LastPriceDollars">Last traded price.</param>
/// <param name="VolumeFp">Contracts traded in total.</param>
/// <param name="Volume24hFp">Contracts traded in the last 24 hours.</param>
/// <param name="OpenInterestFp">Open interest in contracts.</param>
/// <param name="Result">The result once determined, otherwise empty.</param>
/// <param name="SettlementValueDollars">The settlement value once settled.</param>
/// <param name="SettlementTs">When it settled.</param>
/// <param name="RulesPrimary">The primary rules text.</param>
/// <param name="RulesSecondary">The secondary rules text.</param>
/// <param name="StrikeType">How the strike applies, for example <c>greater_or_equal</c>.</param>
/// <param name="FloorStrike">Lower strike level.</param>
/// <param name="CapStrike">Upper strike level.</param>
/// <param name="MveCollectionTicker">Multivariate (combo) markets only: the collection.</param>
/// <param name="MveSelectedLegs">Multivariate (combo) markets only: the legs.</param>
/// <param name="IsProvisional">Whether the market is provisional.</param>
/// <param name="ExchangeIndex">Which exchange shard lists it.</param>
public sealed record Market(
    string Ticker,
    string EventTicker,
    string MarketType,
    string Status,
    string YesSubTitle = "",
    string NoSubTitle = "",
    DateTimeOffset? CreatedTime = null,
    DateTimeOffset? UpdatedTime = null,
    DateTimeOffset? OpenTime = null,
    DateTimeOffset? CloseTime = null,
    DateTimeOffset? LatestExpirationTime = null,
    Dollars? YesBidDollars = null,
    Dollars? YesAskDollars = null,
    Dollars? NoBidDollars = null,
    Dollars? NoAskDollars = null,
    Contracts? YesBidSizeFp = null,
    Contracts? YesAskSizeFp = null,
    Dollars? LastPriceDollars = null,
    Contracts? VolumeFp = null,
    [property: JsonPropertyName("volume_24h_fp")] Contracts? Volume24hFp = null,
    Contracts? OpenInterestFp = null,
    string Result = "",
    Dollars? SettlementValueDollars = null,
    DateTimeOffset? SettlementTs = null,
    string RulesPrimary = "",
    string RulesSecondary = "",
    string? StrikeType = null,
    Level? FloorStrike = null,
    Level? CapStrike = null,
    string? MveCollectionTicker = null,
    IReadOnlyList<MveSelectedLeg>? MveSelectedLegs = null,
    bool? IsProvisional = null,
    int? ExchangeIndex = null)
{
    /// <summary>True for a multivariate (combo) market.</summary>
    [JsonIgnore]
    public bool IsMultivariate => MveCollectionTicker is not null;
}
