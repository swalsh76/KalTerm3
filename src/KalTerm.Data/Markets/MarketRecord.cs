namespace KalTerm.Data.Markets;

/// <summary>
/// The catalog facts about one market, as stored in <c>dbo.market</c>. Times are UTC. Prices are dollars at 10^-6 (<c>_e6</c>).
/// This is a plain record so that <c>KalTerm.Data</c> knows nothing about Kalshi's models.
/// </summary>
public sealed record MarketRecord(
    string Ticker,
    string EventTicker,
    string MarketType,
    string Status,
    string YesSubTitle = "",
    string NoSubTitle = "",
    DateTime? OpenTime = null,
    DateTime? CloseTime = null,
    DateTime? LatestExpirationTime = null,
    DateTime? SettlementTs = null,
    string Result = "",
    long? SettlementValueE6 = null,
    string Title = "");

/// <summary>What one upsert did.</summary>
/// <param name="Ids">The <c>market_id</c> of every ticker in the batch, new and existing alike.</param>
/// <param name="Inserted">The tickers that did not exist before.</param>
/// <param name="Matched">How many already existed (and were updated).</param>
public sealed record MarketUpsertResult(IReadOnlyDictionary<string, int> Ids, IReadOnlyList<string> Inserted, int Matched)
{
    /// <summary>The result of an empty batch.</summary>
    public static MarketUpsertResult Empty { get; } = new(new Dictionary<string, int>(), [], 0);
}

/// <summary>The market catalog's storage.</summary>
public interface IMarketStore
{
    /// <summary>
    /// Inserts new markets and updates existing ones (matched on ticker) in one transaction; every ticker in the batch gets
    /// <paramref name="seenAtUtc"/> as its <c>last_seen_at</c>. A ticker repeated within the batch counts once, the last record winning.
    /// </summary>
    /// <param name="records">The markets.</param>
    /// <param name="seenAtUtc">When the listing that contained them was taken.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The ids, the new tickers and the number updated.</returns>
    Task<MarketUpsertResult> UpsertAsync(IReadOnlyList<MarketRecord> records, DateTime seenAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every market that is still tradable in the database but was not seen since <paramref name="passStartedUtc"/> as
    /// <see cref="MarketStore.UnlistedStatus"/>. Call it only after a complete listing.
    /// </summary>
    /// <param name="passStartedUtc">The <c>seenAtUtc</c> the pass used.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The tickers that were marked.</returns>
    Task<IReadOnlyList<string>> MarkUnlistedAsync(DateTime passStartedUtc, CancellationToken cancellationToken = default);

    /// <summary>Every known ticker and its <c>market_id</c>.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The ids by ticker.</returns>
    Task<IReadOnlyDictionary<string, int>> LoadIdsAsync(CancellationToken cancellationToken = default);
}
