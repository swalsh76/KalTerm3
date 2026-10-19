using System.Collections.Concurrent;
using System.Threading.Channels;
using KalTerm.Data;
using KalTerm.Data.State;
using KalTerm.Data.Writing;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.WebSocket;
using Microsoft.Extensions.Logging;

namespace KalTerm.Ingest.Tests;

/// <summary>Polls a condition; the runner's loops run on the real clock with tiny intervals, so tests wait for outcomes, not for counted timer fires.</summary>
internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int milliseconds = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("timed out waiting for " + what);
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>A batch sink that keeps every row it is given.</summary>
internal sealed class RecordingSink<T> : IBatchSink<T>
{
    private readonly ConcurrentQueue<T> _rows = new();

    public IReadOnlyCollection<T> Rows => [.. _rows];

    public Task WriteAsync(IReadOnlyList<T> batch, CancellationToken cancellationToken)
    {
        foreach (var row in batch)
        {
            _rows.Enqueue(row);
        }

        return Task.CompletedTask;
    }
}

/// <summary>In-memory run state that records every write and can be told to fail.</summary>
internal sealed class FakeIngestState : IIngestState
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, IngestStateEntry> _values = new(StringComparer.Ordinal);
    private readonly List<(string Name, string Value, DateTime At)> _history = [];

    /// <summary>When set, a write of this value fails.</summary>
    public string? FailWhenWriting { get; set; }

    public IReadOnlyList<(string Name, string Value, DateTime At)> History
    {
        get
        {
            lock (_sync)
            {
                return [.. _history];
            }
        }
    }

    public int Writes(string value) => History.Count(h => h.Value == value);

    public void Seed(string name, string value, DateTime at)
    {
        lock (_sync)
        {
            _values[name] = new IngestStateEntry(value, at);
        }
    }

    public IngestStateEntry? Current(string name)
    {
        lock (_sync)
        {
            return _values.GetValueOrDefault(name);
        }
    }

    public Task<IngestStateEntry?> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Current(name));

    public Task SetAsync(string name, string value, DateTime at, CancellationToken cancellationToken = default)
    {
        if (value == FailWhenWriting)
        {
            throw new InvalidOperationException("the database is down");
        }

        lock (_sync)
        {
            _values[name] = new IngestStateEntry(value, at);
            _history.Add((name, value, at));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Records partition top-ups and can be told to fail.</summary>
internal sealed class FakePartitions : IPartitionMaintenance
{
    private readonly ConcurrentQueue<(DateOnly Today, int Days)> _calls = new();
    private int _count;

    public IReadOnlyCollection<(DateOnly Today, int Days)> Calls => [.. _calls];

    /// <summary>Calls from this number on fail (1-based); 0 means never.</summary>
    public int FailFromCall { get; set; }

    public Task<IReadOnlyList<DateOnly>> EnsureAheadAsync(DateOnly today, int daysAhead, CancellationToken cancellationToken = default)
    {
        var n = Interlocked.Increment(ref _count);
        _calls.Enqueue((today, daysAhead));
        if (FailFromCall > 0 && n >= FailFromCall)
        {
            throw new InvalidOperationException("cannot split the partition");
        }

        return Task.FromResult<IReadOnlyList<DateOnly>>([]);
    }
}

/// <summary>A stream the test feeds by hand.</summary>
internal sealed class ScriptedSession : IStreamSession
{
    private readonly Channel<WsMessage> _channel = Channel.CreateUnbounded<WsMessage>();

    public bool Disposed { get; private set; }

    public bool IsConnected => !Disposed;

    public WsStats Stats { get; } = new(0, 100, 0, 0, 0);

    /// <summary>The order book subscription's id; settable so a test can move it, as a reconnect does.</summary>
    public int? OrderBookSid { get; set; }

    /// <summary>Every snapshot request made, with the command id handed back.</summary>
    public List<(int Sid, string Ticker, int Id)> SnapshotRequests { get; } = [];

    /// <summary>When set, the next snapshot requests fail with this.</summary>
    public Exception? SnapshotRequestFails { get; set; }

    public Task<int> RequestSnapshotAsync(int sid, string ticker, CancellationToken cancellationToken = default)
    {
        if (SnapshotRequestFails is not null)
        {
            return Task.FromException<int>(SnapshotRequestFails);
        }

        lock (SnapshotRequests)
        {
            var id = 100 + SnapshotRequests.Count;
            SnapshotRequests.Add((sid, ticker, id));
            return Task.FromResult(id);
        }
    }

    public void Push(WsMessage message) => _channel.Writer.TryWrite(message);

    /// <summary>Ends the stream normally.</summary>
    public void End() => _channel.Writer.TryComplete();

    /// <summary>Ends the stream with an error.</summary>
    public void Fail(Exception error) => _channel.Writer.TryComplete(error);

    public IAsyncEnumerable<WsMessage> Messages(CancellationToken cancellationToken = default) => _channel.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Answers each attempt to open a stream with a scripted result.</summary>
internal sealed class ScriptedSource(Func<int, Task<IStreamSession>> open) : IStreamSource
{
    private int _opens;

    public int Opens => Volatile.Read(ref _opens);

    public Task<IStreamSession> OpenAsync(CancellationToken cancellationToken = default) => open(Interlocked.Increment(ref _opens));
}

/// <summary>Keeps every formatted line, so a test can check what an operator would have seen.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly Lock _sync = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_sync)
            {
                return [.. _lines];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_sync)
        {
            _lines.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }


}

/// <summary>A governor that counts its checks and can be told to fail.</summary>
internal sealed class FakeGovernor : KalTerm.Data.Governor.IStorageGovernor
{
    private int _checks;

    public int Checks => Volatile.Read(ref _checks);

    public Exception? Failure { get; set; }

    public Task<KalTerm.Data.Governor.GovernorReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _checks);
        return Failure is null
            ? Task.FromResult(new KalTerm.Data.Governor.GovernorReport(DateTime.UtcNow, KalTerm.Data.Governor.BudgetLevel.Ok, 1, [], null))
            : Task.FromException<KalTerm.Data.Governor.GovernorReport>(Failure);
    }
}

/// <summary>A backfiller that hands out a number of "gaps" and counts its calls.</summary>
internal sealed class FakeBackfiller : KalTerm.Ingest.Backfill.ITradeBackfiller
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>How many calls find a gap to recover; later calls find none.</summary>
    public int GapsToRecover { get; set; }

    public Exception? Failure { get; set; }

    public Task<KalTerm.Ingest.Backfill.BackfillOutcome?> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        if (Failure is not null)
        {
            return Task.FromException<KalTerm.Ingest.Backfill.BackfillOutcome?>(Failure);
        }

        return Task.FromResult<KalTerm.Ingest.Backfill.BackfillOutcome?>(call <= GapsToRecover ? new KalTerm.Ingest.Backfill.BackfillOutcome(call, "done", 1, 1, "n") : null);
    }
}
