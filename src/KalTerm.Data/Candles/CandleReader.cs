using System.Data;
using Dapper;
using KalTerm.Data.Reading;

namespace KalTerm.Data.Candles;

/// <summary>One candle: the prices and volume of a market over one bucket, as stored (dollars at 10^-6, contracts at 10^-2).</summary>
/// <param name="BucketStart">The start of the bucket (UTC).</param>
/// <param name="IntervalSeconds">The bucket length.</param>
/// <param name="OpenE6">The first trade's yes price; <see langword="null"/> when there was no trade.</param>
/// <param name="HighE6">The highest trade price.</param>
/// <param name="LowE6">The lowest trade price.</param>
/// <param name="CloseE6">The last trade's yes price.</param>
/// <param name="VolumeE2">Contracts traded.</param>
/// <param name="TradeCount">Trades.</param>
/// <param name="YesBidE6">The yes bid at the last ticker update of the bucket.</param>
/// <param name="YesAskE6">The yes ask at the last ticker update.</param>
/// <param name="OpenInterestE2">The open interest at the last ticker update.</param>
/// <param name="TradesPartial">The bucket overlaps a gap whose trade backfill is not finished.</param>
/// <param name="BookPartial">The bucket overlaps a gap: ticker updates in it may be missing for good.</param>
/// <param name="GapKind">The kind of the gap that overlaps the most.</param>
/// <param name="GapSeconds">Seconds of the bucket covered by gaps.</param>
/// <param name="MarketId">The market.</param>
public sealed record StoredCandle(
    DateTime BucketStart,
    int IntervalSeconds,
    long? OpenE6,
    long? HighE6,
    long? LowE6,
    long? CloseE6,
    long VolumeE2,
    int TradeCount,
    long? YesBidE6,
    long? YesAskE6,
    long? OpenInterestE2,
    bool TradesPartial,
    bool BookPartial,
    string? GapKind,
    int? GapSeconds,
    int MarketId = 0)
{
    /// <summary>Whether any part of the candle may be incomplete.</summary>
    public bool Partial => TradesPartial || BookPartial;
}

/// <summary>Reads stored candles.</summary>
public interface ICandleReader
{
    /// <summary>The intervals the ingest process stores, with their scopes; empty when it has not said.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rules.</returns>
    Task<IReadOnlyList<CandleRule>> RulesAsync(CancellationToken cancellationToken = default);

    /// <summary>The market's id, or <see langword="null"/> when there is no such market.</summary>
    /// <param name="ticker">The ticker.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The id.</returns>
    Task<int?> MarketIdAsync(string ticker, CancellationToken cancellationToken = default);

    /// <summary>
    /// A market's stored candles of one interval, oldest first, with a bucket start in [<paramref name="start"/>, <paramref name="until"/>). Without a start, the latest
    /// <paramref name="limit"/> buckets before the end.
    /// </summary>
    /// <param name="marketId">The market.</param>
    /// <param name="intervalSeconds">The stored interval.</param>
    /// <param name="start">The first bucket wanted (inclusive), or <see langword="null"/> for the latest ones.</param>
    /// <param name="until">The end of the range (exclusive).</param>
    /// <param name="limit">The most candles, 1 to 20,000.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The candles.</returns>
    Task<IReadOnlyList<StoredCandle>> ReadAsync(int marketId, int intervalSeconds, DateTime? start, DateTime until, int limit, CancellationToken cancellationToken = default);

    /// <summary>Whether the raw ticker and trade rows exist for a moment (the retention may have dropped them); the oldest day still held, or <see langword="null"/> when unknown.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The day (UTC).</returns>
    Task<DateOnly?> OldestRawDayAsync(CancellationToken cancellationToken = default);
}

/// <summary><see cref="ICandleReader"/> over <c>dbo.candle</c>.</summary>
/// <param name="source">Opens connections.</param>
public sealed class CandleReader(DataSource source) : ICandleReader
{
    /// <summary>The key under which the ingest process records which intervals it stores.</summary>
    public const string RulesKey = "candles.stored";

    /// <summary>The most candles one read returns.</summary>
    public const int MaxRows = 20_000;

    private const string Columns = "bucket_start, interval_s, open_e6, high_e6, low_e6, close_e6, volume_e2, trade_count, yes_bid_e6, yes_ask_e6, open_interest_e2, trades_partial, book_partial, gap_kind, gap_seconds, market_id";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CandleRule>> RulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var text = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT value FROM dbo.ingest_state WHERE name = @name", new { name = RulesKey }, commandTimeout: 15, cancellationToken: cancellationToken)).ConfigureAwait(false);
        try
        {
            return CandleRules.Parse(text);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<int?> MarketIdAsync(string ticker, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReaderSupport.MarketIdAsync(connection, ticker, new ReaderOptions(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredCandle>> ReadAsync(int marketId, int intervalSeconds, DateTime? start, DateTime until, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxRows);
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = start is null
            ? $"SELECT TOP (@limit) {Columns} FROM dbo.candle WHERE interval_s = @i AND market_id = @m AND bucket_start < @until ORDER BY bucket_start DESC"
            : $"SELECT TOP (@limit) {Columns} FROM dbo.candle WHERE interval_s = @i AND market_id = @m AND bucket_start >= @start AND bucket_start < @until ORDER BY bucket_start";
        Add(command, "@limit", limit);
        Add(command, "@i", intervalSeconds);
        Add(command, "@m", marketId);
        Add(command, "@until", until);
        if (start is { } from)
        {
            Add(command, "@start", from);
        }

        var rows = new List<StoredCandle>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        if (start is null)
        {
            rows.Reverse();
        }

        return rows;
    }

    /// <inheritdoc/>
    public async Task<DateOnly?> OldestRawDayAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var text = await connection.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT value FROM dbo.ingest_state WHERE name = @name", new { name = Governor.GovernorStateKeys.OldestDay }, commandTimeout: 15, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) ? day : null;
    }

    /// <summary>Reads a candle from a row with the columns of <c>dbo.candle</c>.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The candle.</returns>
    public static StoredCandle Read(IDataRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new StoredCandle(
            DateTime.SpecifyKind(row.GetDateTime(row.GetOrdinal("bucket_start")), DateTimeKind.Utc),
            row.GetInt32(row.GetOrdinal("interval_s")),
            Long(row, "open_e6"),
            Long(row, "high_e6"),
            Long(row, "low_e6"),
            Long(row, "close_e6"),
            row.GetInt64(row.GetOrdinal("volume_e2")),
            row.GetInt32(row.GetOrdinal("trade_count")),
            Long(row, "yes_bid_e6"),
            Long(row, "yes_ask_e6"),
            Long(row, "open_interest_e2"),
            row.GetBoolean(row.GetOrdinal("trades_partial")),
            row.GetBoolean(row.GetOrdinal("book_partial")),
            row.IsDBNull(row.GetOrdinal("gap_kind")) ? null : row.GetString(row.GetOrdinal("gap_kind")),
            row.IsDBNull(row.GetOrdinal("gap_seconds")) ? null : row.GetInt32(row.GetOrdinal("gap_seconds")),
            row.GetInt32(row.GetOrdinal("market_id")));
    }

    private static long? Long(IDataRecord row, string name) => row.IsDBNull(row.GetOrdinal(name)) ? null : row.GetInt64(row.GetOrdinal(name));

    private static void Add(Microsoft.Data.SqlClient.SqlCommand command, string name, object value) => command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter(name, value));
}

/// <summary>Combines candles of a short interval into candles of a longer one that is a whole multiple of it.</summary>
public static class CandleRollup
{
    /// <summary>
    /// Rolls candles up exactly: open is the first candle's open that has one, close the last close, high the largest high, low the smallest low, volume and trade count the sums, bid, ask and
    /// open interest those of the last candle that has them, and the partial flags and gap seconds combine (a flag if any candle has it).
    /// </summary>
    /// <param name="candles">Candles of one market and one interval, oldest first.</param>
    /// <param name="targetSeconds">The longer interval.</param>
    /// <returns>The rolled-up candles, oldest first.</returns>
    public static IReadOnlyList<StoredCandle> Roll(IEnumerable<StoredCandle> candles, int targetSeconds)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var rolled = new List<StoredCandle>();
        foreach (var group in candles.GroupBy(c => CandleRules.BucketStart(c.BucketStart, targetSeconds)).OrderBy(g => g.Key))
        {
            var members = group.OrderBy(c => c.BucketStart).ToList();
            var withPrice = members.Where(c => c.OpenE6 is not null).ToList();
            var gaps = members.Where(c => c.GapKind is not null).ToList();
            rolled.Add(new StoredCandle(
                group.Key,
                targetSeconds,
                withPrice.Count > 0 ? withPrice[0].OpenE6 : null,
                withPrice.Count > 0 ? withPrice.Max(c => c.HighE6) : null,
                withPrice.Count > 0 ? withPrice.Min(c => c.LowE6) : null,
                withPrice.Count > 0 ? withPrice[^1].CloseE6 : null,
                members.Sum(c => c.VolumeE2),
                members.Sum(c => c.TradeCount),
                members.LastOrDefault(c => c.YesBidE6 is not null)?.YesBidE6,
                members.LastOrDefault(c => c.YesAskE6 is not null)?.YesAskE6,
                members.LastOrDefault(c => c.OpenInterestE2 is not null)?.OpenInterestE2,
                members.Any(c => c.TradesPartial),
                members.Any(c => c.BookPartial),
                gaps.Count == 0 ? null : gaps.MaxBy(c => c.GapSeconds ?? 0)!.GapKind,
                gaps.Count == 0 ? null : gaps.Sum(c => c.GapSeconds ?? 0),
                members[0].MarketId));
        }

        return rolled;
    }
}
