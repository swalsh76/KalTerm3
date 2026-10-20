using System.Globalization;
using System.Text.Json;

namespace KalTerm.Trading.Account;

/// <summary>The trading state file exists but cannot be read: trading is refused rather than guessing the day's limits and the halt.</summary>
public sealed class TradingStateException : Exception
{
    public TradingStateException()
    {
    }

    public TradingStateException(string message)
        : base(message)
    {
    }

    public TradingStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A transfer that was sent (or about to be) and not yet confirmed.</summary>
/// <param name="Id">The client transfer id.</param>
/// <param name="Source">The shard the money leaves.</param>
/// <param name="Destination">The shard it goes to.</param>
/// <param name="AmountE6">The amount in millionths of a dollar.</param>
public sealed record PendingTransfer(string Id, int Source, int Destination, long AmountE6);

/// <summary>
/// Trading state that must survive a restart, kept in <c>trading_state.json</c> (next to <c>risk.json</c>). Restarting must never quietly reset a limit, so three things live here: the day's realized-profit baseline
/// (Kalshi reports cumulative profit, so today's loss is the cumulative figure less what it was when the UTC day began, as first seen by this client), the day's transferred total (the per-day transfer cap is checked
/// against it), and the halt set by the kill switch. It also remembers client order ids whose outcome was never learned and transfers sent but not confirmed, so they are reconciled rather than forgotten.
/// A file that exists but cannot be read is an error, never a fresh start.
/// </summary>
public sealed class TradingState
{
    /// <summary>The file's name.</summary>
    public const string FileName = "trading_state.json";

    private readonly Lock _gate = new();
    private readonly string _path;
    private string _day = string.Empty;
    private long? _baseline;
    private long _transferred;
    private bool _halted;
    private string _haltReason = string.Empty;
    private List<string> _unknownOrders = [];
    private List<PendingTransfer> _pending = [];

    /// <param name="path">The file; <see cref="DefaultPath"/> when omitted.</param>
    public TradingState(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    /// <summary>The state file in the owner's profile.</summary>
    /// <returns>The path.</returns>
    public static string DefaultPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KalTerm", FileName);

    /// <summary>The UTC day a moment falls in, as <c>yyyy-MM-dd</c>.</summary>
    /// <param name="moment">The moment.</param>
    /// <returns>The day.</returns>
    public static string UtcDay(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The day the counters belong to.</summary>
    public string Day
    {
        get
        {
            lock (_gate)
            {
                return _day;
            }
        }
    }

    /// <summary>Whether the kill switch halted trading.</summary>
    public bool Halted
    {
        get
        {
            lock (_gate)
            {
                return _halted;
            }
        }
    }

    /// <summary>Why trading was halted.</summary>
    public string HaltReason
    {
        get
        {
            lock (_gate)
            {
                return _haltReason;
            }
        }
    }

    /// <summary>Moved between shards today, in millionths of a dollar.</summary>
    public long TransferredE6
    {
        get
        {
            lock (_gate)
            {
                return _transferred;
            }
        }
    }

    /// <summary>Client order ids whose outcome was never learned.</summary>
    public IReadOnlyList<string> UnknownOrders
    {
        get
        {
            lock (_gate)
            {
                return [.. _unknownOrders];
            }
        }
    }

    /// <summary>Transfers sent and not yet confirmed.</summary>
    public IReadOnlyList<PendingTransfer> PendingTransfers
    {
        get
        {
            lock (_gate)
            {
                return [.. _pending];
            }
        }
    }

    /// <summary>Today's starting cumulative realized profit, or <see langword="null"/> when none is recorded.</summary>
    public long? RealizedBaselineE6
    {
        get
        {
            lock (_gate)
            {
                return _baseline;
            }
        }
    }

    /// <summary>Reads the file; a missing file is a fresh state.</summary>
    /// <param name="path">The file; <see cref="DefaultPath"/> when omitted.</param>
    /// <returns>The state.</returns>
    /// <exception cref="TradingStateException">The file exists but cannot be read or is damaged.</exception>
    public static TradingState Load(string? path = null)
    {
        var state = new TradingState(path);
        string text;
        try
        {
            text = File.ReadAllText(state._path);
        }
        catch (FileNotFoundException)
        {
            return state;
        }
        catch (DirectoryNotFoundException)
        {
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TradingStateException($"cannot read {state._path}: {ex.Message}", ex);
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("not an object");
            }

            state._day = root.GetProperty("day").GetString() ?? throw new JsonException("day");
            var baseline = root.GetProperty("realized_baseline_e6");
            state._baseline = baseline.ValueKind == JsonValueKind.Null ? null : baseline.GetInt64();
            state._transferred = root.GetProperty("transferred_e6").GetInt64();
            state._halted = root.GetProperty("halted").GetBoolean();
            state._haltReason = root.TryGetProperty("halt_reason", out var reason) ? reason.GetString() ?? string.Empty : string.Empty;
            if (root.TryGetProperty("unknown_orders", out var unknown))
            {
                state._unknownOrders = [.. unknown.EnumerateArray().Select(e => e.GetString() ?? throw new JsonException("unknown_orders"))];
            }

            if (root.TryGetProperty("pending_transfers", out var pending))
            {
                state._pending = [.. pending.EnumerateArray().Select(e => new PendingTransfer(
                    e.GetProperty("id").GetString() ?? throw new JsonException("id"),
                    e.GetProperty("source").GetInt32(),
                    e.GetProperty("destination").GetInt32(),
                    e.GetProperty("amount_e6").GetInt64()))];
            }

            return state;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new TradingStateException(
                $"{state._path} is damaged ({ex.GetType().Name}); trading is refused rather than guessing the day's limits and the halt. Check the file; delete it only if you are sure no halt was set and no transfers are pending.",
                ex);
        }
    }

    /// <summary>Starts a new day: the counters reset; the halt and unresolved items stay.</summary>
    /// <param name="today">The UTC day.</param>
    /// <returns><see langword="true"/> when the day changed.</returns>
    public bool Roll(string today)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(today);
        lock (_gate)
        {
            if (_day == today)
            {
                return false;
            }

            _day = today;
            _baseline = null;
            _transferred = 0;
            SaveLocked();
            return true;
        }
    }

    /// <summary>Records today's starting cumulative realized profit, only if none is recorded.</summary>
    /// <param name="realizedE6">The cumulative realized profit now.</param>
    public void SetBaseline(long realizedE6)
    {
        lock (_gate)
        {
            if (_baseline is null)
            {
                _baseline = realizedE6;
                SaveLocked();
            }
        }
    }

    /// <summary>Today's realized profit (negative: loss).</summary>
    /// <param name="realizedNowE6">The cumulative realized profit now.</param>
    /// <returns>Zero before a baseline exists.</returns>
    public long RealizedToday(long realizedNowE6)
    {
        lock (_gate)
        {
            return _baseline is { } baseline ? realizedNowE6 - baseline : 0;
        }
    }

    /// <summary>Counts a transfer against the day and remembers it until confirmed. Saved before the request is sent, so a crash in between still counts it.</summary>
    /// <param name="id">The client transfer id.</param>
    /// <param name="source">The shard the money leaves.</param>
    /// <param name="destination">The shard it goes to.</param>
    /// <param name="amountE6">The amount.</param>
    public void BeginTransfer(string id, int source, int destination, long amountE6)
    {
        lock (_gate)
        {
            _transferred += amountE6;
            _pending.Add(new PendingTransfer(id, source, destination, amountE6));
            SaveLocked();
        }
    }

    /// <summary>A transfer was confirmed: it stays counted, and is no longer pending.</summary>
    /// <param name="id">The client transfer id.</param>
    public void FinishTransfer(string id)
    {
        lock (_gate)
        {
            _pending.RemoveAll(t => t.Id == id);
            SaveLocked();
        }
    }

    /// <summary>Kalshi definitely refused a transfer: take it back out of the day's total.</summary>
    /// <param name="id">The client transfer id.</param>
    public void AbortTransfer(string id)
    {
        lock (_gate)
        {
            foreach (var item in _pending.Where(t => t.Id == id))
            {
                _transferred = Math.Max(0, _transferred - item.AmountE6);
            }

            _pending.RemoveAll(t => t.Id == id);
            SaveLocked();
        }
    }

    /// <summary>Halts trading (the kill switch).</summary>
    /// <param name="reason">Why.</param>
    public void Halt(string reason)
    {
        lock (_gate)
        {
            _halted = true;
            _haltReason = reason;
            SaveLocked();
        }
    }

    /// <summary>Lifts the halt.</summary>
    public void ClearHalt()
    {
        lock (_gate)
        {
            _halted = false;
            _haltReason = string.Empty;
            SaveLocked();
        }
    }

    /// <summary>Remembers an order whose outcome was never learned.</summary>
    /// <param name="clientOrderId">The id; never to be reused.</param>
    public void NoteUnknownOrder(string clientOrderId)
    {
        lock (_gate)
        {
            if (!_unknownOrders.Contains(clientOrderId))
            {
                _unknownOrders.Add(clientOrderId);
                SaveLocked();
            }
        }
    }

    /// <summary>Forgets an order whose outcome is now known.</summary>
    /// <param name="clientOrderId">The id.</param>
    public void ResolveUnknownOrder(string clientOrderId)
    {
        lock (_gate)
        {
            if (_unknownOrders.Remove(clientOrderId))
            {
                SaveLocked();
            }
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private void SaveLocked()
    {
        var file = new StateFile
        {
            Day = _day,
            RealizedBaselineE6 = _baseline,
            TransferredE6 = _transferred,
            Halted = _halted,
            HaltReason = _haltReason,
            UnknownOrders = [.. _unknownOrders],
            PendingTransfers = [.. _pending.Select(t => new PendingFile { Id = t.Id, Source = t.Source, Destination = t.Destination, AmountE6 = t.AmountE6 })],
        };
        if (Path.GetDirectoryName(Path.GetFullPath(_path)) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private sealed class StateFile
    {
        public string Day { get; set; } = string.Empty;

        public long? RealizedBaselineE6 { get; set; }

        public long TransferredE6 { get; set; }

        public bool Halted { get; set; }

        public string? HaltReason { get; set; }

        public List<string>? UnknownOrders { get; set; }

        public List<PendingFile>? PendingTransfers { get; set; }
    }

    private sealed class PendingFile
    {
        public string Id { get; set; } = string.Empty;

        public int Source { get; set; }

        public int Destination { get; set; }

        public long AmountE6 { get; set; }
    }
}
