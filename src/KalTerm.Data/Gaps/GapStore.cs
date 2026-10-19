using Microsoft.Data.SqlClient;

namespace KalTerm.Data.Gaps;

/// <summary>
/// <see cref="IGapLog"/> over <c>dbo.ingest_gap</c>. Gaps are rare, so each is a plain insert, all in one transaction. Every record is
/// checked before anything is written, so a bad one stores none of the batch.
/// </summary>
/// <param name="source">Opens connections.</param>
public sealed class GapStore(DataSource source) : IGapLog
{
    private const int KindLength = 32;
    private const int DetailLength = 400;

    /// <inheritdoc/>
    public async Task AppendAsync(IReadOnlyList<GapRecord> gaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        if (gaps.Count == 0)
        {
            return;
        }

        foreach (var gap in gaps)
        {
            // A parameter silently cuts a longer string to its size; a gap filed under a cut-off kind would be worse than no gap.
            if (string.IsNullOrWhiteSpace(gap.Kind) || gap.Kind.Length > KindLength)
            {
                throw new ArgumentException($"a gap's kind must be 1 to {KindLength} characters", nameof(gaps));
            }
        }

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            "INSERT dbo.ingest_gap (kind, market_id, started_at, ended_at, detail) VALUES (@kind, @market, @started, @ended, @detail)",
            connection,
            transaction);
        var kind = command.Parameters.Add("@kind", System.Data.SqlDbType.VarChar, KindLength);
        var market = command.Parameters.Add("@market", System.Data.SqlDbType.Int);
        var started = command.Parameters.Add("@started", System.Data.SqlDbType.DateTime2);
        started.Scale = 3;
        var ended = command.Parameters.Add("@ended", System.Data.SqlDbType.DateTime2);
        ended.Scale = 3;
        var detail = command.Parameters.Add("@detail", System.Data.SqlDbType.NVarChar, DetailLength);
        foreach (var gap in gaps)
        {
            kind.Value = gap.Kind;
            market.Value = (object?)gap.MarketId ?? DBNull.Value;
            started.Value = gap.StartedAt;
            ended.Value = (object?)gap.EndedAt ?? DBNull.Value;
            detail.Value = gap.Detail is null ? DBNull.Value : gap.Detail.Length <= DetailLength ? gap.Detail : gap.Detail[..DetailLength];
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
