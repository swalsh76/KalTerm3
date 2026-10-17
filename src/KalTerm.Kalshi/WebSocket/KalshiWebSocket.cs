using System.Collections.Concurrent;
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
/// This is the connection core. It does not reconnect: a lost link ends <see cref="Messages"/> with a
/// <see cref="KalshiWsException"/> after the already-queued messages have been delivered.
/// </summary>
public sealed partial class KalshiWebSocket : IAsyncDisposable
{
    private static readonly HashSet<string> ResponseTypes = ["subscribed", "ok", "unsubscribed", "error"];

    /// <summary>The type of the marker passed through the stream for a reply that used up a sequence number.</summary>
    public const string ControlType = "control";

    private readonly KalshiSigner _signer;
    private readonly IWsConnector _connector;
    private readonly Uri _url;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _commandTimeout;
    private readonly Channel<WsMessage> _queue;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<WsMessage>> _pending = new();
    private readonly ConcurrentDictionary<int, (string Channel, JsonObject Parameters)> _subscriptions = new();
    private readonly CancellationTokenSource _stop = new();
    private IWsConnection? _connection;
    private Task _readLoop = Task.CompletedTask;
    private int _nextId;
    private volatile bool _connected;
    private volatile bool _closing;
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

        _signer = signer;
        _url = options.Url ?? settings.WebSocketUrl;
        _connector = options.Connector ?? new ClientWebSocketConnector(options.MaxMessageBytes);
        _time = options.TimeProvider ?? TimeProvider.System;
        _logger = options.Logger ?? NullLogger.Instance;
        _commandTimeout = options.CommandTimeout;
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

    /// <summary>The active subscriptions. Sids are only valid on the current connection.</summary>
    public IReadOnlyList<WsSubscription> Subscriptions =>
        [.. _subscriptions.OrderBy(p => p.Key).Select(p => ToSubscription(p.Key, p.Value))];

    /// <summary>True while the link is up.</summary>
    public bool IsConnected => _connected;

    /// <summary>Opens the connection with freshly signed headers and starts reading. May be called once.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            throw new InvalidOperationException("this client has already been connected; create a new one");
        }

        // Signed now: the timestamp must be current when the server sees it.
        var headers = _signer.Headers("GET", _url.AbsolutePath);
        _connection = await _connector.ConnectAsync(_url, headers, cancellationToken).ConfigureAwait(false);
        _connected = true;
        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
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

        var command = new JsonObject { ["channels"] = new JsonArray(JsonValue.Create(channel)) };
        foreach (var (name, value) in body)
        {
            command[name] = value?.DeepClone();
        }

        var reply = await CommandAsync("subscribe", command, cancellationToken).ConfigureAwait(false);
        var sid = ReadInt(reply, "sid") ?? throw new KalshiWsException("the subscribe reply carried no sid");
        _subscriptions[sid] = (channel, body);
        return sid;
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
        var reply = await CommandAsync("update_subscription", command, cancellationToken).ConfigureAwait(false);

        if (reply.Msg is not { ValueKind: JsonValueKind.Object } msg
            || !msg.TryGetProperty("market_tickers", out var set)
            || set.ValueKind != JsonValueKind.Array
            || set.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String))
        {
            throw new KalshiWsException($"the {action} reply carried no market set");
        }

        // Kalshi answers with the full set after the change; it replaces the stored one so a resubscribe asks for it.
        var current = set.EnumerateArray().Select(t => t.GetString()!).ToList();
        if (_subscriptions.TryGetValue(sid, out var existing))
        {
            existing.Parameters["market_tickers"] = new JsonArray([.. current.Select(t => (JsonNode?)JsonValue.Create(t))]);
        }

        return current;
    }

    /// <summary>Ends a subscription.</summary>
    /// <param name="sid">The subscription.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task UnsubscribeAsync(int sid, CancellationToken cancellationToken = default)
    {
        await CommandAsync("unsubscribe", new JsonObject { ["sids"] = new JsonArray(JsonValue.Create(sid)) }, cancellationToken).ConfigureAwait(false);
        _subscriptions.TryRemove(sid, out _);
    }

    /// <summary>
    /// Streams the data messages in order. Ends normally after <see cref="CloseAsync"/>; if the link was lost (or the consumer
    /// fell too far behind) the messages already queued are delivered and then a <see cref="KalshiWsException"/> is thrown.
    /// Enumerate it once.
    /// </summary>
    /// <param name="cancellationToken">Stops the enumeration.</param>
    public IAsyncEnumerable<WsMessage> Messages(CancellationToken cancellationToken = default) =>
        _queue.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Closes the connection and waits for the reader to finish. Safe to call more than once.</summary>
    public async Task CloseAsync()
    {
        _closing = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_connection is not null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
        }

        await _readLoop.ConfigureAwait(false);
        _queue.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private static WsSubscription ToSubscription(int sid, (string Channel, JsonObject Parameters) state)
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

    private async Task<WsMessage> CommandAsync(string cmd, JsonObject parameters, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null || !_connected)
        {
            throw new KalshiWsException("not connected");
        }

        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<WsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            var text = new JsonObject { ["id"] = id, ["cmd"] = cmd, ["params"] = parameters }.ToJsonString();
            await connection.SendAsync(text, cancellationToken).ConfigureAwait(false);
            return await reply.Task.WaitAsync(_commandTimeout, _time, cancellationToken).ConfigureAwait(false);
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

    private async Task ReadLoopAsync()
    {
        var connection = _connection!;
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
            _connected = false;
            FailPending();
            if (overflowed)
            {
                _lost = "receive queue overflow";
                await connection.CloseAsync().ConfigureAwait(false);
            }

            _queue.Writer.TryComplete(_closing ? null : new KalshiWsException(_lost ?? "connection closed"));
        }
    }

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
                waiting.TrySetException(new KalshiWsException(text, code));
                return true;
            }

            waiting.TrySetResult(message);

            // The reply used up one of the subscription's sequence numbers. Pass a marker through the stream, in order,
            // so gap detection can count it.
            return message is { Sid: not null, Seq: not null }
                ? Enqueue(new WsMessage(ControlType, message.Id, message.Sid, message.Seq, ReceivedAt: message.ReceivedAt))
                : true;
        }

        if (ResponseTypes.Contains(message.Type) && message.Type != "error")
        {
            LogUnmatched(_logger, message.Type, message.Id);
            return true;
        }

        return Enqueue(message);
    }

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

    private bool Enqueue(WsMessage message)
    {
        if (_queue.Writer.TryWrite(message))
        {
            return true;
        }

        LogOverflow(_logger);
        return false;
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiting))
            {
                waiting.TrySetException(new KalshiWsException("connection closed"));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kalshi WebSocket using PRODUCTION environment (read-only)")]
    private static partial void LogProduction(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "dropping unparseable WebSocket message: {Text}")]
    private static partial void LogDropped(ILogger logger, string text);

    [LoggerMessage(Level = LogLevel.Debug, Message = "unmatched WebSocket reply: {Type} (id {Id})")]
    private static partial void LogUnmatched(ILogger logger, string type, int? id);

    [LoggerMessage(Level = LogLevel.Error, Message = "WebSocket receive queue full; closing the connection (the consumer must catch up and reconnect)")]
    private static partial void LogOverflow(ILogger logger);
}
