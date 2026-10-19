using System.Text.Json;
using KalTerm.Data.Gaps;
using KalTerm.Data.Writing;
using KalTerm.Kalshi.Models;

namespace KalTerm.Ingest.Tests;

internal static class StreamTestKit
{
    public static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A wire-shaped message: <paramref name="payload"/> is the JSON of the <c>msg</c> object.</summary>
    public static WsMessage Message(string type, string payload, DateTimeOffset? received = null, int sid = 1, long seq = 1) =>
        new(type, Sid: sid, Seq: seq, Msg: JsonDocument.Parse(payload).RootElement.Clone(), ReceivedAt: received ?? Start);

    public static WsMessage Ticker(string market, DateTimeOffset? received = null, string extra = "") =>
        Message("ticker", $$"""{"market_ticker":"{{market}}","price_dollars":"0.5600"{{extra}}}""", received);

    public static WsMessage Trade(string market, string tradeId = "t-1", DateTimeOffset? received = null, string side = "yes") =>
        Message(
            "trade",
            $$"""{"trade_id":"{{tradeId}}","market_ticker":"{{market}}","yes_price_dollars":"0.4000","no_price_dollars":"0.6000","count_fp":"3.00","taker_side":"{{side}}"}""",
            received);

    public static WsMessage Reconnected(DateTimeOffset received, int dropped = 0, string reason = "connection_lost") =>
        Message("reconnected", $$"""{"resubscribed":[{"channel":"ticker","sid":2,"old_sid":1}],"failed":[],"reason":"{{reason}}","dropped":{{dropped}}}""", received);

    /// <summary>Yields the messages (one scheduler turn apart), then fails with <paramref name="thenFail"/> if given.</summary>
    public static async IAsyncEnumerable<WsMessage> Stream(
        IEnumerable<WsMessage> messages,
        Exception? thenFail = null,
        Action<WsMessage>? beforeEach = null)
    {
        foreach (var message in messages)
        {
            await Task.Yield();
            beforeEach?.Invoke(message);
            yield return message;
        }

        if (thenFail is not null)
        {
            throw thenFail;
        }
    }
}

/// <summary>A writer that keeps what it is given and hands out scripted dropped ranges.</summary>
internal sealed class FakeRowWriter<T> : IRowWriter<T>
{
    public List<T> Rows { get; } = [];

    public Queue<DroppedRange> Dropped { get; } = new();

    public bool TryWrite(T row)
    {
        Rows.Add(row);
        return true;
    }

    public DroppedRange? TakeDroppedRange() => Dropped.Count > 0 ? Dropped.Dequeue() : null;
}

/// <summary>A gap log that keeps what it stores and can be told to fail.</summary>
internal sealed class FakeGapLog : IGapLog
{
    public List<GapRecord> Stored { get; } = [];

    public int Calls { get; private set; }

    /// <summary>The number of upcoming calls that throw.</summary>
    public int FailNext { get; set; }

    public Task AppendAsync(IReadOnlyList<GapRecord> gaps, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (FailNext > 0)
        {
            FailNext--;
            throw new InvalidOperationException("the database is down");
        }

        Stored.AddRange(gaps);
        return Task.CompletedTask;
    }
}
