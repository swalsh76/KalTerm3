using System.Text.Json;
using System.Text.Json.Serialization;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Models.Json;

namespace KalTerm.Kalshi.Models;

/// <summary>
/// A typed payload of a public WebSocket data channel. Kalshi adds fields over time, so unknown fields are ignored;
/// prices and counts are exact fixed-point values, as on the REST models.
/// </summary>
public interface IWsPayload;

/// <summary>A <c>ticker</c> message: the latest top of book, volume and open interest of one market.</summary>
/// <param name="MarketTicker">The market.</param>
/// <param name="MarketId">Kalshi's id for the market.</param>
/// <param name="PriceDollars">Last traded price.</param>
/// <param name="YesBidDollars">Best yes bid.</param>
/// <param name="YesAskDollars">Best yes ask.</param>
/// <param name="YesBidSizeFp">Size at the best yes bid.</param>
/// <param name="YesAskSizeFp">Size at the best yes ask.</param>
/// <param name="VolumeFp">Contracts traded in total.</param>
/// <param name="OpenInterestFp">Open interest in contracts.</param>
/// <param name="LastTradeSizeFp">Size of the last trade.</param>
/// <param name="TsMs">Server time of the update, in epoch milliseconds.</param>
public sealed record TickerMsg(
    string MarketTicker,
    string? MarketId = null,
    Dollars? PriceDollars = null,
    Dollars? YesBidDollars = null,
    Dollars? YesAskDollars = null,
    Contracts? YesBidSizeFp = null,
    Contracts? YesAskSizeFp = null,
    Contracts? VolumeFp = null,
    Contracts? OpenInterestFp = null,
    Contracts? LastTradeSizeFp = null,
    long? TsMs = null) : IWsPayload;

/// <summary>A <c>trade</c> message: one executed trade.</summary>
/// <param name="TradeId">Kalshi's trade id.</param>
/// <param name="MarketTicker">The market.</param>
/// <param name="YesPriceDollars">The yes price.</param>
/// <param name="NoPriceDollars">The no price.</param>
/// <param name="CountFp">Contracts traded.</param>
/// <param name="TakerSide">The side that took liquidity.</param>
/// <param name="IsBlockTrade">Whether it was a block trade.</param>
/// <param name="TsMs">Server time of the trade, in epoch milliseconds.</param>
public sealed record TradeMsg(
    string TradeId,
    string MarketTicker,
    Dollars YesPriceDollars,
    Dollars NoPriceDollars,
    Contracts CountFp,
    string? TakerSide = null,
    bool IsBlockTrade = false,
    long? TsMs = null) : IWsPayload;

/// <summary>
/// A market lifecycle message (<c>market_lifecycle_v2</c> or <c>multivariate_market_lifecycle</c>): a market was created,
/// opened, closed, determined or settled.
/// </summary>
/// <param name="EventType">What happened.</param>
/// <param name="MarketTicker">The market, when the message is about one.</param>
/// <param name="OpenTs">Open time, in epoch seconds.</param>
/// <param name="CloseTs">Close time, in epoch seconds.</param>
/// <param name="DeterminationTs">Determination time, in epoch seconds.</param>
/// <param name="SettledTs">Settlement time, in epoch seconds.</param>
/// <param name="Result">The result, once determined.</param>
/// <param name="SettlementValue">The settlement value as Kalshi sends it (text).</param>
/// <param name="IsDeactivated">Whether trading was switched off.</param>
/// <param name="ExchangeIndex">Which exchange shard the market is on.</param>
/// <param name="PriceLevelStructure">The market's price-level structure.</param>
/// <param name="AdditionalMetadata">Free-form extra data, kept as raw JSON.</param>
public sealed record LifecycleMsg(
    string EventType,
    string? MarketTicker = null,
    long? OpenTs = null,
    long? CloseTs = null,
    long? DeterminationTs = null,
    long? SettledTs = null,
    string? Result = null,
    string? SettlementValue = null,
    bool? IsDeactivated = null,
    int? ExchangeIndex = null,
    string? PriceLevelStructure = null,
    IReadOnlyDictionary<string, JsonElement>? AdditionalMetadata = null) : IWsPayload;

/// <summary>An <c>event_lifecycle</c> message: an event was created (also sent on the market lifecycle channels).</summary>
/// <param name="EventTicker">The event.</param>
/// <param name="SeriesTicker">Its series.</param>
/// <param name="Title">The event title.</param>
/// <param name="Subtitle">The event subtitle.</param>
/// <param name="CollateralReturnType">How collateral is returned.</param>
/// <param name="ExchangeIndex">Which exchange shard the event is on.</param>
public sealed record EventLifecycleMsg(
    string EventTicker,
    string? SeriesTicker = null,
    string Title = "",
    string Subtitle = "",
    string CollateralReturnType = "",
    int? ExchangeIndex = null) : IWsPayload;

/// <summary>An <c>orderbook_snapshot</c> message: the full book of one market. Levels are bids, <c>[price, contracts]</c>, best last.</summary>
public sealed record OrderbookSnapshotMsg : IWsPayload
{
    /// <summary>Creates a snapshot; an absent side is empty.</summary>
    /// <param name="marketTicker">The market.</param>
    /// <param name="marketId">Kalshi's id for the market.</param>
    /// <param name="yesDollarsFp">Yes bids. A null body value is refused; only an absent one becomes empty.</param>
    /// <param name="noDollarsFp">No bids. A null body value is refused; only an absent one becomes empty.</param>
    [JsonConstructor]
    public OrderbookSnapshotMsg(
        string marketTicker,
        string? marketId = null,
        IReadOnlyList<PriceLevel> yesDollarsFp = null!,
        IReadOnlyList<PriceLevel> noDollarsFp = null!)
    {
        MarketTicker = marketTicker;
        MarketId = marketId;
        YesDollarsFp = yesDollarsFp ?? [];
        NoDollarsFp = noDollarsFp ?? [];
    }

    /// <summary>The market.</summary>
    public string MarketTicker { get; }

    /// <summary>Kalshi's id for the market.</summary>
    public string? MarketId { get; }

    /// <summary>Yes bids.</summary>
    [JsonConverter(typeof(PriceLevelPairListJsonConverter))]
    public IReadOnlyList<PriceLevel> YesDollarsFp { get; }

    /// <summary>No bids.</summary>
    [JsonConverter(typeof(PriceLevelPairListJsonConverter))]
    public IReadOnlyList<PriceLevel> NoDollarsFp { get; }
}

/// <summary>An <c>orderbook_delta</c> message: the signed change in contracts at one price level on one side.</summary>
/// <param name="MarketTicker">The market.</param>
/// <param name="PriceDollars">The price level that changed.</param>
/// <param name="DeltaFp">The change in contracts; negative when liquidity left.</param>
/// <param name="Side">Which side, <c>yes</c> or <c>no</c>.</param>
/// <param name="TsMs">Server time of the change, in epoch milliseconds.</param>
/// <param name="ClientOrderId">The client order id behind the change, when it is yours.</param>
public sealed record OrderbookDeltaMsg(
    string MarketTicker,
    Dollars PriceDollars,
    Contracts DeltaFp,
    string Side,
    long? TsMs = null,
    string? ClientOrderId = null) : IWsPayload;

/// <summary>
/// The envelope of everything the server sends. <see cref="Msg"/> is the raw payload; <see cref="Payload"/> types it for the
/// public data channels.
/// </summary>
/// <param name="Type">The message type, for example <c>ticker</c> or <c>subscribed</c>.</param>
/// <param name="Id">The id of the command this answers, if any.</param>
/// <param name="Sid">The subscription id.</param>
/// <param name="Seq">The per-subscription sequence number, used to detect gaps.</param>
/// <param name="SendingTsMs">When the server sent it, in epoch milliseconds.</param>
/// <param name="Msg">The raw payload.</param>
public sealed record WsMessage(
    string Type,
    int? Id = null,
    int? Sid = null,
    long? Seq = null,
    long? SendingTsMs = null,
    JsonElement? Msg = null)
{
    /// <summary>
    /// The typed payload for a known public data channel, else <see langword="null"/> (read <see cref="Msg"/>). Throws
    /// <see cref="KalshiResponseException"/> when the type is known but the payload is missing or invalid.
    /// </summary>
    public IWsPayload? Payload()
    {
        var ctx = KalshiJsonContext.Default;
        return Type switch
        {
            "ticker" => Read(ctx.TickerMsg),
            "trade" => Read(ctx.TradeMsg),
            "market_lifecycle_v2" or "multivariate_market_lifecycle" => Read(ctx.LifecycleMsg),
            "event_lifecycle" => Read(ctx.EventLifecycleMsg),
            "orderbook_snapshot" => Read(ctx.OrderbookSnapshotMsg),
            "orderbook_delta" => Read(ctx.OrderbookDeltaMsg),
            _ => null,
        };
    }

    private T Read<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class, IWsPayload
    {
        if (Msg is not { ValueKind: JsonValueKind.Object } body)
        {
            throw new KalshiResponseException($"{Type}: the message has no payload object");
        }

        return KalshiJson.Parse(body.GetRawText(), typeInfo, Type);
    }
}
