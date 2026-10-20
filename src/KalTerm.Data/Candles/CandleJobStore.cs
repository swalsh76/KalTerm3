using System.Globalization;
using Dapper;
using KalTerm.Data.Governor;
using KalTerm.Data.State;

namespace KalTerm.Data.Candles;

/// <summary>A gap whose trade backfill has finished (one way or the other) since its candles were last built, so the candles over it need rebuilding.</summary>
/// <param name="Id">The gap.</param>
/// <param name="StartedAt">When it began (UTC).</param>
/// <param name="EndedAt">When it ended (UTC).</param>
public sealed record GapToRebuild(long Id, DateTime StartedAt, DateTime EndedAt);

/// <summary>What the candle job remembers: how far each stored interval is built, which gaps still need their candles rebuilt, and how far back the raw rows go.</summary>
public interface ICandleJobStore
{
    /// <summary>Records which intervals are stored, for the API to read.</summary>
    /// <param name="rules">The rules.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    Task SaveRulesAsync(IReadOnlyList<CandleRule> rules, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>The end of the last bucket built for an interval; <see langword="null"/> when none was.</summary>
    /// <param name="intervalSeconds">The interval.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The moment (UTC).</returns>
    Task<DateTime?> GetThroughAsync(int intervalSeconds, CancellationToken cancellationToken = default);

    /// <summary>Records how far an interval is built.</summary>
    /// <param name="intervalSeconds">The interval.</param>
    /// <param name="through">The end of the last bucket built (UTC).</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    Task SetThroughAsync(int intervalSeconds, DateTime through, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>The first moment raw rows may still exist (the start of the oldest day the governor reports); <see langword="null"/> when the governor has not said.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The moment (UTC).</returns>
    Task<DateTime?> RawFromAsync(CancellationToken cancellationToken = default);

    /// <summary>Gaps that ended before <paramref name="endedBefore"/> and whose trade backfill finished after their candles were last built.</summary>
    /// <param name="endedBefore">Only gaps that ended before this.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The gaps, oldest first.</returns>
    Task<IReadOnlyList<GapToRebuild>> GapsToRebuildAsync(DateTime endedBefore, CancellationToken cancellationToken = default);

    /// <summary>Records that the candles over a gap were rebuilt.</summary>
    /// <param name="gapId">The gap.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when it is recorded.</returns>
    Task MarkRebuiltAsync(long gapId, DateTime now, CancellationToken cancellationToken = default);
}

/// <summary><see cref="ICandleJobStore"/> over <c>dbo.ingest_state</c> and <c>dbo.ingest_gap</c>.</summary>
/// <param name="source">Opens connections.</param>
public sealed class CandleJobStore(DataSource source) : ICandleJobStore
{
    private static string ThroughKey(int intervalSeconds) => string.Create(CultureInfo.InvariantCulture, $"candles.{intervalSeconds}.through");

    /// <inheritdoc/>
    public Task SaveRulesAsync(IReadOnlyList<CandleRule> rules, DateTime now, CancellationToken cancellationToken = default) =>
        new IngestStateStore(source).SetAsync(CandleReader.RulesKey, CandleRules.Format(rules), now, cancellationToken);

    /// <inheritdoc/>
    public async Task<DateTime?> GetThroughAsync(int intervalSeconds, CancellationToken cancellationToken = default)
    {
        var entry = await new IngestStateStore(source).GetAsync(ThroughKey(intervalSeconds), cancellationToken).ConfigureAwait(false);
        return entry is not null && DateTime.TryParseExact(entry.Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var through) ? through : null;
    }

    /// <inheritdoc/>
    public Task SetThroughAsync(int intervalSeconds, DateTime through, DateTime now, CancellationToken cancellationToken = default) =>
        new IngestStateStore(source).SetAsync(ThroughKey(intervalSeconds), through.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), now, cancellationToken);

    /// <inheritdoc/>
    public async Task<DateTime?> RawFromAsync(CancellationToken cancellationToken = default)
    {
        var entry = await new IngestStateStore(source).GetAsync(GovernorStateKeys.OldestDay, cancellationToken).ConfigureAwait(false);
        return entry is not null && DateOnly.TryParseExact(entry.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<GapToRebuild>> GapsToRebuildAsync(DateTime endedBefore, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<GapToRebuild>(new CommandDefinition(
            """
            SELECT gap_id AS Id, started_at AS StartedAt, ended_at AS EndedAt FROM dbo.ingest_gap
            WHERE ended_at IS NOT NULL AND ended_at < @endedBefore AND backfill_status IN ('done', 'failed', 'skipped')
              AND (candles_built_at IS NULL OR candles_built_at < backfill_at)
            ORDER BY started_at, gap_id
            """,
            new { endedBefore },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(g => g with { StartedAt = DateTime.SpecifyKind(g.StartedAt, DateTimeKind.Utc), EndedAt = DateTime.SpecifyKind(g.EndedAt, DateTimeKind.Utc) })];
    }

    /// <inheritdoc/>
    public async Task MarkRebuiltAsync(long gapId, DateTime now, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.ingest_gap SET candles_built_at = @now WHERE gap_id = @gapId",
            new { gapId, now },
            commandTimeout: 30,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
