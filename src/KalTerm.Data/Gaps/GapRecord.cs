namespace KalTerm.Data.Gaps;

/// <summary>The values of <c>ingest_gap.kind</c>.</summary>
public static class GapKinds
{
    /// <summary>The WebSocket link was replaced; messages sent while it was down are lost.</summary>
    public const string WsReconnected = "ws_reconnected";

    /// <summary>The WebSocket link was lost for good (the stream ended with an error).</summary>
    public const string WsLost = "ws_lost";

    /// <summary>The ingest process could not get a WebSocket connection for a while (the span between the stream ending for good and the next one opening).</summary>
    public const string WsDown = "ws_down";

    /// <summary>The ingest process was not running: from its last heartbeat or clean stop to the next start.</summary>
    public const string IngestDown = "ingest_down";

    /// <summary>A writer dropped rows: buffer full, a batch the database refused, or rows left at shutdown.</summary>
    public const string WriterDropped = "writer_dropped";
}

/// <summary>One period for which stream data is known to be missing.</summary>
/// <param name="Kind">What kind of gap; see <see cref="GapKinds"/> (at most 32 characters).</param>
/// <param name="StartedAt">When the missing period began (UTC).</param>
/// <param name="EndedAt">When it ended (UTC), or <see langword="null"/> if it has not.</param>
/// <param name="Detail">Free text for the operator; cut to 400 characters when stored.</param>
/// <param name="MarketId">The market, when the gap is about one.</param>
public sealed record GapRecord(string Kind, DateTime StartedAt, DateTime? EndedAt = null, string? Detail = null, int? MarketId = null);

/// <summary>Records gaps in the stream data.</summary>
public interface IGapLog
{
    /// <summary>Stores the gaps in one transaction: all of them or none. A record with a missing or over-long kind is refused before anything is stored.</summary>
    /// <param name="gaps">The gaps.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when they are stored.</returns>
    Task AppendAsync(IReadOnlyList<GapRecord> gaps, CancellationToken cancellationToken = default);
}
