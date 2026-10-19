using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Models.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KalTerm.Kalshi.WebSocket;

/// <summary>
/// An authenticated Kalshi WebSocket client for the public data channels: connect, subscribe, unsubscribe, change a
/// subscription's markets, and stream the messages. It has no way to place or change an order: its only commands are
/// <c>subscribe</c>, <c>unsubscribe</c> and <c>update_subscription</c> (a test pins the public surface).
///
/// When the link is lost the client replaces it (see <see cref="KalshiWebSocketOptions.AutoReconnect"/>): it waits with
/// exponential backoff, reconnects with freshly signed headers, re-sends every active subscription, and puts a
/// <c>reconnected</c> message (<see cref="ReconnectedMsg"/>) in the stream so the consumer knows data may have been missed.
/// Data that arrives while the subscriptions are being restored is held and released after that message. Sequence-gap
/// detection is the order book builder's job (M3); the <c>control</c> markers in the stream are its input.
/// </summary>
public sealed partial class KalshiWebSocket : IAsyncDisposable
{
    private static readonly HashSet<string> ResponseTypes = ["subscribed", "ok", "unsubscribed", "error"];

    /// <summary>The type of the marker passed through the stream for a reply that used up a sequence number.</summary>
    public const string ControlType = "control";

    /// <summary>The type of the synthetic message that follows a reconnect.</summary>
    public const string ReconnectedType = "reconnected";

    private const string ConnectionLostReason = "connection_lost";
    private const string OverflowReason = "overflow";
    private static readonly TimeSpan DrainPoll = TimeSpan.FromMilliseconds(50);

    private readonly KalshiSigner _signer;
    private readonly IWsConnector _connector;
    private readonly Uri _url;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly KalshiWebSocketOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;
    private readonly Channel<WsMessage> _queue;
    private readonly ConcurrentDictionary<int, PendingCommand> _pending = new();
    private readonly ConcurrentDictionary<int, SubscriptionState> _subscriptions = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _heldLock = new();
    private List<WsMessage>? _held; // data held back while resubscribing; guarded by _heldLock
    private IWsConnection? _connection;
    private Task _supervisor = Task.CompletedTask;
    private int _nextId;
    private long _nextOrder;
    private volatile bool _connected;
    private volatile bool _closing;
    private volatile bool _flushOverflowed;
    private volatile string _reason = ConnectionLostReason; // why the last connection ended
    private long _overflows;
    private long _dropped;
    private long _droppedRun; // dropped since the last reconnected message
    private long _highWater;
    private string? _lost;

    /// <summary>Creates a client; nothing is opened until <see cref="ConnectAsync"/>.</summary>
    /// <param name="settings">Where to connect (demo by default) and whether to log the production banner.</param>
    /// <param name="signer">Signs the upgrade request.</param>
    /// <param name="options">Optional settings.</param>
    public KalshiWebSocket(KalshiSettings settings, KalshiSigner signer, KalshiWebSocketOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(signer);
        options ??= new KalshiWebSocketOptions();
        if (options.MaxQueueMessages < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxQueueMessages must be at least 1");
        }

        if (options.ReconnectBase <= TimeSpan.Zero || options.ReconnectMax < options.ReconnectBase)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ReconnectBase must be positive and ReconnectMax at least as long");
        }

        if (options.MaxReconnectAttempts is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxReconnectAttempts cannot be negative");
        }

        _signer = signer;
        _options = options;
        _url = options.Url ?? settings.WebSocketUrl;
        _connector = options.Connector ?? new ClientWebSocketConnector(options.MaxMessageBytes);
        _time = options.TimeProvider ?? TimeProvider.System;
        _logger = options.Logger ?? NullLogger.Instance;
        _delay = options.Delay ?? ((span, token) => Task.Delay(span, _time, token));
        _jitter = options.JitterSample ?? Random.Shared.NextDouble;
        _queue = Channel.CreateBounded<WsMessage>(new BoundedChannelOptions(options.MaxQueueMessages)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        if (settings.IsProduction)
        {
            LogProduction(_logger);
        }
    }

    /// <summary>The active subscriptions, oldest first. Sids are only valid on the current connection.</summary>
    public IReadOnlyList<WsSubscription> Subscriptions =>
        [.. _subscriptions.OrderBy(p => p.Value.Order).Select(p => ToSubscription(p.Key, p.Value))];

    /// <summary>True while the link is up.</summary>
    public bool IsConnected => _connected;

    /// <summary>Queue health, for monitoring (for example a server status endpoint).</summary>
    public WsStats Stats => new(
        _queue.Reader.Count,
        _options.MaxQueueMessages,
        (int)Interlocked.Read(ref _highWater),
        Interlocked.Read(ref _overflows),
        Interlocked.Read(ref _dropped));

    /// <summary>Opens the connection with freshly signed headers and starts reading. May be called once.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            throw new InvalidOperationException("this client has already been connected; create a new one");
        }

        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _connection = connection;
        _connected = true;
        _supervisor = Task.Run(() => SuperviseAsync(connection), CancellationToken.None);
    }

    /// <summary>Subscribes to one channel and returns its subscription id. Channel names are passed through unchecked.</summary>
    /// <param name="channel">The channel name, for example <c>ticker</c>.</param>
    /// <param name="marketTickers">The markets, for per-market channels.</param>
    /// <param name="parameters">Any further request parameters; values are sent as given.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<int> SubscribeAsync(
        string channel,
        IReadOnlyList<string>? marketTickers = null,
        IReadOnlyDictionary<string, JsonElement>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);

        var body = new JsonObject();
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                if (name is "channels")
                {
                    throw new ArgumentException("'channels' is set from the channel argument", nameof(parameters));
                }

                body[name] = JsonNode.Parse(value.GetRawText());
            }
        }

        if (marketTickers is { Count: > 0 })
        {
            body["market_tickers"] = new JsonArray([.. marketTickers.Select(t => (JsonNode?)JsonValue.Create(t))]);
        }

        return await SubscribeBodyAsync(channel, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds markets to or removes them from a live subscription and returns the resulting full market set.</summary>
    /// <param name="sid">The subscription.</param>
    /// <param name="action">Kalshi's action name: <c>add_markets</c> or <c>delete_markets</c>.</param>
    /// <param name="marketTickers">The markets to add or remove.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<IReadOnlyList<string>> UpdateSubscriptionAsync(
        int sid,
        string action,
        IReadOnlyList<string> marketTickers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(marketTickers);

        var command = new JsonObject
        {
            ["sid"] = sid,
            ["market_tickers"] = new JsonArray([.. marketTickers.Select(t => (JsonNode?)JsonValue.Create(t))]),
            ["action"] = action,
        };

        // Kalshi answers with the full set after the change; it replaces the stored one so a resubscribe asks for it.
        // Stored as the reply is received (not when this method resumes) so a link that drops right after the reply
        // is restored with the new set.
        List<string>? current = null;
        await CommandAsync(
            "update_subscription",
            command,
            reply =>
            {
                if (reply.Msg is not { ValueKind: JsonValueKind.Object } msg
                    || !msg.TryGetProperty("market_tickers", out var set)
                    || set.ValueKind != JsonValueKind.Array
                    || set.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String))
                {
                    return new KalshiWsException($"the {action} reply carried no market set");
                }

                current = [.. set.EnumerateArray().Select(t => t.GetString()!)];
                if (_subscriptions.TryGetValue(sid, out var existing))
                {
                    existing.Parameters["market_tickers"] = new JsonArray([.. current.Select(t => (JsonNode?)JsonValue.Create(t))]);
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);

        return current!;
    }

    /// <summary>
    /// Asks for a fresh in-stream order book snapshot of one market of a subscription and returns the command's id. Kalshi has no separate acknowledgement: the
    /// reply is an <c>orderbook_snapshot</c> message in the stream (echoing the id and carrying the subscription's next sequence number), and a rejected command
    /// arrives as an <c>error</c> message with the same id. Like every command here it is an <c>update_subscription</c>; it cannot place or change an order.
    /// </summary>
    /// <param name="sid">The order book subscription.</param>
    /// <param name="marketTicker">The market.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The command id, to match the reply.</returns>
    public async Task<int> RequestSnapshotAsync(int sid, string marketTicker, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketTicker);
        var connection = _connection;
        if (connection is null || !_connected)
        {
            throw new KalshiWsException("not connected");
        }

        var id = Interlocked.Increment(ref _nextId);
        var parameters = new JsonObject
        {
            ["sid"] = sid,
            ["market_tickers"] = new JsonArray(JsonValue.Create(marketTicker)),
            ["action"] = "get_snapshot",
        };
        await connection.SendAsync(new JsonObject { ["id"] = id, ["cmd"] = "update_subscription", ["params"] = parameters }.ToJsonString(), cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>Ends a subscription.</summary>
    /// <param name="sid">The subscription.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task UnsubscribeAsync(int sid, CancellationToken cancellationToken = default)
    {
        await CommandAsync(
            "unsubscribe",
            new JsonObject { ["sids"] = new JsonArray(JsonValue.Create(sid)) },
            confirmed =>
            {
                _subscriptions.TryRemove(sid, out _);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams the data messages in order, plus <c>control</c> and <c>reconnected</c> messages. Ends normally after
    /// <see cref="CloseAsync"/>; if the link was lost for good (reconnecting is off, attempts ran out, or the consumer fell too
    /// far behind) the messages already queued are delivered and then a <see cref="KalshiWsException"/> is thrown. Enumerate it once.
    /// </summary>
    /// <param name="cancellationToken">Stops the enumeration.</param>
    public IAsyncEnumerable<WsMessage> Messages(CancellationToken cancellationToken = default) =>
        _queue.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Closes the connection, stops any reconnecting, and waits for the reader to finish. Safe to call more than once.</summary>
    public async Task CloseAsync()
    {
        _closing = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_connection is { } connection)
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }

        await _supervisor.ConfigureAwait(false);
        _queue.Writer.TryComplete();
    }

    /// <summary>For tests: cuts the link from our side, as a network failure would.</summary>
    internal Task DropConnectionAsync() => _connection?.CloseAsync() ?? Task.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        if (_connection is { } connection)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private static WsSubscription ToSubscription(int sid, SubscriptionState state)
    {
        IReadOnlyList<string> tickers = state.Parameters["market_tickers"] is JsonArray array
            ? [.. array.Select(n => n!.GetValue<string>())]
            : [];
        return new WsSubscription(sid, state.Channel, tickers, state.Parameters.ToJsonString());
    }

    private static int? ReadInt(WsMessage reply, string name) =>
        reply.Msg is { ValueKind: JsonValueKind.Object } msg
        && msg.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static (string Text, int? Code) ReadError(WsMessage message)
    {
        if (message.Msg is not { ValueKind: JsonValueKind.Object } body)
        {
            return ("error", null);
        }

        var text = body.TryGetProperty("msg", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "error";
        int? code = body.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : null;
        return (text, code);
    }

    /// <summary>Opens a connection. The headers are signed now: the timestamp must be current when the server sees it.</summary>
    private Task<IWsConnection> OpenAsync(CancellationToken cancellationToken) =>
        _connector.ConnectAsync(_url, _signer.Headers("GET", _url.AbsolutePath), cancellationToken);

    private async Task<int> SubscribeBodyAsync(string channel, JsonObject body, CancellationToken cancellationToken)
    {
        var command = new JsonObject { ["channels"] = new JsonArray(JsonValue.Create(channel)) };
        foreach (var (name, value) in body)
        {
            command[name] = value?.DeepClone();
        }

        var sid = 0;
        await CommandAsync(
            "subscribe",
            command,
            reply =>
            {
                if (ReadInt(reply, "sid") is not { } found)
                {
                    return new KalshiWsException("the subscribe reply carried no sid");
                }

                sid = found;
                _subscriptions[sid] = new SubscriptionState(channel, body, Interlocked.Increment(ref _nextOrder));
                return null;
            },
            cancellationToken).ConfigureAwait(false);
        return sid;
    }

    /// <summary>
    /// Sends a command and waits for its reply. <paramref name="apply"/> runs on the reader as the reply is received, before
    /// the waiting caller resumes, so the client's state is already right if the link drops straight after the reply; it
    /// returns an exception to fail the command.
    /// </summary>
    private async Task<WsMessage> CommandAsync(string cmd, JsonObject parameters, Func<WsMessage, Exception?>? apply, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null || !_connected)
        {
            throw new KalshiWsException("not connected");
        }

        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<WsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingCommand(reply, apply);
        try
        {
            var text = new JsonObject { ["id"] = id, ["cmd"] = cmd, ["params"] = parameters }.ToJsonString();
            await connection.SendAsync(text, cancellationToken).ConfigureAwait(false);
            return await reply.Task.WaitAsync(_options.CommandTimeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new KalshiWsException($"{cmd} timed out");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    // ---- the supervisor: read, and when the link is lost, replace it and restore the subscriptions ----------------------

    private async Task SuperviseAsync(IWsConnection connection)
    {
        var first = true;
        try
        {
            while (true)
            {
                if (!first)
                {
                    var next = await ReopenAsync().ConfigureAwait(false);
                    if (next is null)
                    {
                        break;
                    }

                    connection = next;
                    _connection = connection;
                    _connected = true;
                }

                _flushOverflowed = false;
                var reader = ReadLoopAsync(connection);
                if (!first)
                {
                    await RestoreSubscriptionsAsync().ConfigureAwait(false);
                }

                first = false;
                var overflowed = await reader.ConfigureAwait(false);
                _reason = overflowed ? OverflowReason : ConnectionLostReason;
                await connection.DisposeAsync().ConfigureAwait(false);
                if (_closing || !_options.AutoReconnect)
                {
                    break;
                }

                LogReconnecting(_logger);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lost = ex.Message;
            LogSupervisorFailed(_logger, ex);
        }
        finally
        {
            _connected = false;
            FailPending();
            _queue.Writer.TryComplete(_closing ? null : new KalshiWsException(_lost ?? "connection closed"));
        }
    }

    /// <summary>Reads until the connection ends. True if it ended because the consumer queue was full.</summary>
    private async Task<bool> ReadLoopAsync(IWsConnection connection)
    {
        _lost = null;
        var overflowed = false;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var raw = await connection.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                if (raw is null)
                {
                    break;
                }

                if (!Handle(raw))
                {
                    overflowed = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // CloseAsync stopped us.
        }
        catch (KalshiWsException ex)
        {
            _lost = ex.Message;
        }
        finally
        {
            // Fail commands the moment the link is gone, not when the supervisor notices: a command sent while
            // resubscribing would otherwise wait out its full timeout.
            _connected = false;
            FailPending();
        }

        overflowed |= _flushOverflowed;
        if (overflowed)
        {
            _lost = "receive queue overflow";
            await connection.CloseAsync().ConfigureAwait(false);
        }

        return overflowed;
    }

    /// <summary>Replaces the lost connection, waiting with backoff. Null if closing, or after giving up.</summary>
    private async Task<IWsConnection?> ReopenAsync()
    {
        if (_reason == OverflowReason)
        {
            // Reconnecting into a still-full queue would just overflow again and hammer Kalshi with connects: wait until
            // the consumer has drained it to half the limit.
            LogWaitingToDrain(_logger);
            while (_queue.Reader.Count > _options.MaxQueueMessages / 2)
            {
                if (_closing)
                {
                    return null;
                }

                try
                {
                    await _delay(DrainPoll, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        var attempt = 0;
        while (true)
        {
            if (_closing)
            {
                return null;
            }

            if (_options.MaxReconnectAttempts is { } max && attempt >= max)
            {
                _lost = "reconnect attempts exhausted";
                return null;
            }

            try
            {
                await _delay(Backoff(attempt), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            attempt++;
            if (_closing)
            {
                return null;
            }

            try
            {
                return await OpenAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_closing)
            {
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or WebSocketException or SocketException or IOException or TimeoutException or KalshiWsException)
            {
                LogReconnectFailed(_logger, attempt, ex.Message);
            }
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(_options.ReconnectMax.TotalSeconds, _options.ReconnectBase.TotalSeconds * Math.Pow(2, attempt));
        return TimeSpan.FromSeconds(seconds * (0.5 + (0.5 * _jitter())));
    }

    /// <summary>Re-sends the subscriptions, then releases data in order: the <c>reconnected</c> message first.</summary>
    private async Task RestoreSubscriptionsAsync()
    {
        lock (_heldLock)
        {
            _held = [];
        }

        var completed = false;
        try
        {
            completed = await ResubscribeAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_heldLock)
            {
                var held = _held;
                _held = null;
                if (completed && held is not null)
                {
                    for (var i = 0; i < held.Count; i++)
                    {
                        if (!_queue.Writer.TryWrite(held[i]))
                        {
                            // The rest did not fit: count them as dropped and reconnect, so the consumer backfills.
                            _flushOverflowed = true;
                            RecordOverflow(held.Count - i);
                            break;
                        }

                        NoteDepth();
                    }
                }
            }
        }

        // A restore that failed or overflowed leaves a live link with the subscriptions not restored (for example when a
        // resubscribe timed out). Close it, so the reader ends and the supervisor goes round again.
        if ((!completed || _flushOverflowed) && _connection is { } connection)
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Subscribes again to everything that was active. A subscription Kalshi refuses is reported and dropped; trouble with
    /// the link itself abandons the round (false) and keeps every subscription for the next one.
    /// </summary>
    private async Task<bool> ResubscribeAsync()
    {
        var old = _subscriptions.OrderBy(p => p.Value.Order).ToList();
        _subscriptions.Clear();
        var resubscribed = new List<ResubscribedEntry>();
        var failed = new List<ResubscribeFailure>();
        foreach (var (oldSid, state) in old)
        {
            try
            {
                var sid = await SubscribeBodyAsync(state.Channel, (JsonObject)state.Parameters.DeepClone(), _stop.Token).ConfigureAwait(false);
                resubscribed.Add(new ResubscribedEntry(state.Channel, sid, oldSid));
            }
            catch (KalshiWsException ex) when (ex.Code is not null)
            {
                LogResubscribeFailed(_logger, state.Channel, ex.Message);
                failed.Add(new ResubscribeFailure(state.Channel, oldSid, ex.Message));
            }
            catch (Exception ex) when (ex is KalshiWsException or OperationCanceledException)
            {
                // The link went down again (or we are closing): start over on the next connection with all of them.
                _subscriptions.Clear();
                foreach (var (sid, kept) in old)
                {
                    _subscriptions[sid] = kept;
                }

                return false;
            }
        }

        var dropped = (int)Interlocked.Read(ref _droppedRun);
        var body = new JsonObject
        {
            ["resubscribed"] = new JsonArray([.. resubscribed.Select(r => (JsonNode?)new JsonObject
            {
                ["channel"] = r.Channel,
                ["sid"] = r.Sid,
                ["old_sid"] = r.OldSid,
            })]),
            ["failed"] = new JsonArray([.. failed.Select(f => (JsonNode?)new JsonObject
            {
                ["channel"] = f.Channel,
                ["old_sid"] = f.OldSid,
                ["error"] = f.Error,
            })]),
            ["reason"] = _reason,
            ["dropped"] = dropped,
        };
        using var document = JsonDocument.Parse(body.ToJsonString());
        var message = new WsMessage(ReconnectedType, Msg: document.RootElement.Clone(), ReceivedAt: _time.GetUtcNow());
        try
        {
            // This message is what tells the consumer to backfill, so it is never dropped: if the queue is full, wait for
            // room (the reader meanwhile holds incoming data, which is bounded).
            await _queue.Writer.WriteAsync(message, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        Interlocked.Add(ref _droppedRun, -dropped);
        NoteDepth();
        return true;
    }

    // ---- routing what was received --------------------------------------------------------------------------------------

    /// <summary>Routes one received text message. False if the consumer queue was full.</summary>
    private bool Handle(string raw)
    {
        WsMessage message;
        try
        {
            message = KalshiJson.Parse(raw, KalshiJsonContext.Default.WsMessage, "WebSocket message") with { ReceivedAt = _time.GetUtcNow() };
        }
        catch (KalshiResponseException)
        {
            LogDropped(_logger, raw.Length > 200 ? raw[..200] : raw);
            return true;
        }

        if (ResponseTypes.Contains(message.Type) && message.Id is { } id && _pending.TryRemove(id, out var waiting))
        {
            if (message.Type == "error")
            {
                var (text, code) = ReadError(message);
                waiting.Reply.TrySetException(new KalshiWsException(text, code));
                return true;
            }

            var problem = waiting.Apply?.Invoke(message);
            if (problem is null)
            {
                waiting.Reply.TrySetResult(message);
            }
            else
            {
                waiting.Reply.TrySetException(problem);
            }

            // The reply used up one of the subscription's sequence numbers. Pass a marker through the stream, in order,
            // so gap detection can count it.
            return message is { Sid: not null, Seq: not null }
                ? Route(new WsMessage(ControlType, message.Id, message.Sid, message.Seq, ReceivedAt: message.ReceivedAt))
                : true;
        }

        if (ResponseTypes.Contains(message.Type) && message.Type != "error")
        {
            LogUnmatched(_logger, message.Type, message.Id);
            return true;
        }

        return Route(message);
    }

    /// <summary>Holds a message while the subscriptions are being restored, otherwise queues it.</summary>
    private bool Route(WsMessage message)
    {
        lock (_heldLock)
        {
            if (_held is not null)
            {
                if (_held.Count < _options.MaxQueueMessages)
                {
                    _held.Add(message);
                    return true;
                }

                // A consumer that has stalled while the subscriptions are being restored: bound the held data too.
                RecordOverflow(1);
                return false;
            }

            if (_queue.Writer.TryWrite(message))
            {
                NoteDepth();
                return true;
            }
        }

        RecordOverflow(1);
        return false;
    }

    private void RecordOverflow(int dropped)
    {
        Interlocked.Increment(ref _overflows);
        Interlocked.Add(ref _dropped, dropped);
        Interlocked.Add(ref _droppedRun, dropped);
        LogOverflow(_logger);
    }

    private void NoteDepth()
    {
        var depth = (long)_queue.Reader.Count;
        long seen;
        while ((seen = Interlocked.Read(ref _highWater)) < depth && Interlocked.CompareExchange(ref _highWater, depth, seen) != seen)
        {
        }
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiting))
            {
                waiting.Reply.TrySetException(new KalshiWsException("connection closed"));
            }
        }
    }

    /// <summary>A command waiting for its reply, and what to do to the client's state when the reply arrives.</summary>
    private sealed record PendingCommand(TaskCompletionSource<WsMessage> Reply, Func<WsMessage, Exception?>? Apply);

    /// <summary>What is kept for each subscription: enough to ask for it again.</summary>
    private sealed record SubscriptionState(string Channel, JsonObject Parameters, long Order);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kalshi WebSocket using PRODUCTION environment (read-only)")]
    private static partial void LogProduction(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dropping unparseable WebSocket message: {Text}")]
    private static partial void LogDropped(ILogger logger, string text);

    [LoggerMessage(Level = LogLevel.Debug, Message = "unmatched WebSocket reply: {Type} (id {Id})")]
    private static partial void LogUnmatched(ILogger logger, string type, int? id);

    [LoggerMessage(Level = LogLevel.Error, Message = "WebSocket receive queue full; closing the connection (the consumer must catch up and reconnect)")]
    private static partial void LogOverflow(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "waiting for the consumer to drain before reconnecting")]
    private static partial void LogWaitingToDrain(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kalshi WebSocket connection lost; reconnecting")]
    private static partial void LogReconnecting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kalshi WebSocket reconnect attempt {Attempt} failed: {Reason}")]
    private static partial void LogReconnectFailed(ILogger logger, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "resubscribe to {Channel} failed: {Reason}")]
    private static partial void LogResubscribeFailed(ILogger logger, string channel, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Kalshi WebSocket supervisor stopped on an unexpected error")]
    private static partial void LogSupervisorFailed(ILogger logger, Exception exception);
}
