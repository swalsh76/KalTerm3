using Microsoft.Extensions.Logging;

namespace KalTerm.Data.Writing;

/// <summary>One ticker update. Prices are dollars at 10^-6, sizes and volumes contracts at 10^-2; <see langword="null"/> means the message did not carry the field.</summary>
public readonly record struct TickerRow(
    DateTime Ts,
    int MarketId,
    long? PriceE6 = null,
    long? YesBidE6 = null,
    long? YesAskE6 = null,
    long? YesBidSizeE2 = null,
    long? YesAskSizeE2 = null,
    long? VolumeE2 = null,
    long? OpenInterestE2 = null,
    long? LastTradeSizeE2 = null);

/// <summary>Which side took liquidity in a trade; the stored value of <c>trade.taker_side</c>.</summary>
public enum TakerSide : byte
{
    /// <summary>Not reported.</summary>
    Unknown = 0,

    /// <summary>The yes side.</summary>
    Yes = 1,

    /// <summary>The no side.</summary>
    No = 2,
}

/// <summary>One executed trade.</summary>
public readonly record struct TradeRow(
    DateTime Ts,
    int MarketId,
    string TradeId,
    long YesPriceE6,
    long NoPriceE6,
    long CountE2,
    TakerSide TakerSide,
    bool IsBlock);

/// <summary>Maps <see cref="TickerRow"/> onto <c>dbo.ticker</c>.</summary>
public sealed class TickerRowMapper : IRowMapper<TickerRow>
{
    /// <summary>The shared instance.</summary>
    public static TickerRowMapper Instance { get; } = new();

    /// <inheritdoc/>
    public string Table => "dbo.ticker";

    /// <inheritdoc/>
    public IReadOnlyList<string> Columns { get; } =
        ["ts", "market_id", "price_e6", "yes_bid_e6", "yes_ask_e6", "yes_bid_size_e2", "yes_ask_size_e2", "volume_e2", "open_interest_e2", "last_trade_size_e2"];

    /// <inheritdoc/>
    public IReadOnlyList<Type> ColumnTypes { get; } =
        [typeof(DateTime), typeof(int), typeof(long), typeof(long), typeof(long), typeof(long), typeof(long), typeof(long), typeof(long), typeof(long)];

    /// <inheritdoc/>
    public object? GetValue(TickerRow row, int column) => column switch
    {
        0 => row.Ts,
        1 => row.MarketId,
        2 => row.PriceE6,
        3 => row.YesBidE6,
        4 => row.YesAskE6,
        5 => row.YesBidSizeE2,
        6 => row.YesAskSizeE2,
        7 => row.VolumeE2,
        8 => row.OpenInterestE2,
        9 => row.LastTradeSizeE2,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    /// <inheritdoc/>
    public DateTime Timestamp(TickerRow row) => row.Ts;
}

/// <summary>Maps <see cref="TradeRow"/> onto <c>dbo.trade</c>.</summary>
public sealed class TradeRowMapper : IRowMapper<TradeRow>
{
    /// <summary>The shared instance.</summary>
    public static TradeRowMapper Instance { get; } = new();

    /// <inheritdoc/>
    public string Table => "dbo.trade";

    /// <inheritdoc/>
    public IReadOnlyList<string> Columns { get; } =
        ["ts", "market_id", "trade_id", "yes_price_e6", "no_price_e6", "count_e2", "taker_side", "is_block"];

    /// <inheritdoc/>
    public IReadOnlyList<Type> ColumnTypes { get; } =
        [typeof(DateTime), typeof(int), typeof(string), typeof(long), typeof(long), typeof(long), typeof(byte), typeof(bool)];

    /// <inheritdoc/>
    public object? GetValue(TradeRow row, int column) => column switch
    {
        0 => row.Ts,
        1 => row.MarketId,
        2 => row.TradeId,
        3 => row.YesPriceE6,
        4 => row.NoPriceE6,
        5 => row.CountE2,
        6 => (byte)row.TakerSide,
        7 => row.IsBlock,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    /// <inheritdoc/>
    public DateTime Timestamp(TradeRow row) => row.Ts;
}

/// <summary>Creates the two writers the ingest host uses.</summary>
public static class Writers
{
    /// <summary>A writer for <c>dbo.ticker</c>. The caller starts and disposes it; disposing it disposes the sink.</summary>
    /// <param name="source">Opens connections.</param>
    /// <param name="options">Tuning; <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives warnings and errors.</param>
    /// <returns>The writer.</returns>
    public static BulkWriter<TickerRow> Ticker(DataSource source, BulkWriterOptions? options = null, TimeProvider? time = null, ILogger? logger = null) =>
        new(new SqlBulkSink<TickerRow>(source, TickerRowMapper.Instance), TickerRowMapper.Instance, options, time, logger);

    /// <summary>A writer for <c>dbo.trade</c>. The caller starts and disposes it; disposing it disposes the sink.</summary>
    /// <param name="source">Opens connections.</param>
    /// <param name="options">Tuning; <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Receives warnings and errors.</param>
    /// <returns>The writer.</returns>
    public static BulkWriter<TradeRow> Trade(DataSource source, BulkWriterOptions? options = null, TimeProvider? time = null, ILogger? logger = null) =>
        new(new SqlBulkSink<TradeRow>(source, TradeRowMapper.Instance), TradeRowMapper.Instance, options, time, logger);
}
