using System.Text.Json;
using System.Threading.Channels;
using KalTerm.Kalshi.WebSocket;

namespace KalTerm.Kalshi.Tests.WebSocket;

/// <summary>A scriptable in-memory connection: the test pushes what the "server" sends and sees what the client sent.</summary>
internal sealed class FakeWsConnection : IWsConnection
{
    private readonly Channel<object?> _inbox = Channel.CreateUnbounded<object?>();
    private readonly List<string> _sent = [];
    private readonly Lock _sync = new();
    private volatile bool _closed;

    /// <summary>Called with each sent text, so a test can answer the way the server would.</summary>
    public Action<string>? OnSend { get; set; }

    public bool CloseCalled { get; private set; }

    public IReadOnlyList<string> Sent
    {
        get
        {
            lock (_sync)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>The id of a sent command.</summary>
    public static int IdOf(string sent) => JsonDocument.Parse(sent).RootElement.GetProperty("id").GetInt32();

    public static string CommandOf(string sent) => JsonDocument.Parse(sent).RootElement.GetProperty("cmd").GetString()!;

    /// <summary>A server text message.</summary>
    public void Push(string text) => _inbox.Writer.TryWrite(text);

    /// <summary>The server closes the connection.</summary>
    public void PushClose() => _inbox.Writer.TryWrite(null);

    /// <summary>The next receive throws, as a real connection would on a protocol violation.</summary>
    public void PushFailure(Exception exception) => _inbox.Writer.TryWrite(exception);

    /// <summary>Answers every <c>subscribe</c> with a <c>subscribed</c> reply carrying <paramref name="sid"/> (and a sequence number if given).</summary>
    public void AutoSubscribe(int sid, int? seq = null) =>
        OnSend = text =>
        {
            if (CommandOf(text) == "subscribe")
            {
                var seqPart = seq is null ? string.Empty : $",\"seq\":{seq}";
                Push($$$"""{"id":{{{IdOf(text)}}},"type":"subscribed","sid":{{{sid}}}{{{seqPart}}},"msg":{"channel":"ticker","sid":{{{sid}}}}}""");
            }
        };

    public Task SendAsync(string text, CancellationToken cancellationToken)
    {
        if (_closed)
        {
            throw new KalshiWsException("connection closed");
        }

        lock (_sync)
        {
            _sent.Add(text);
        }

        OnSend?.Invoke(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            object? item;
            try
            {
                item = await _inbox.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }

            switch (item)
            {
                case string text:
                    return text;
                case Exception exception:
                    throw exception;
                default:
                    _closed = true;
                    return null;
            }
        }
    }

    public Task CloseAsync()
    {
        CloseCalled = true;
        _closed = true;
        _inbox.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

/// <summary>
/// Hands out scripted connections, one per successful connect: <see cref="At"/> creates them on demand, and
/// <see cref="OnOpen"/> lets a test script each one as it is opened (answer subscribes, then drop, and so on).
/// </summary>
internal sealed class FakeWsConnector : IWsConnector
{
    private readonly List<FakeWsConnection> _connections = [];
    private readonly List<IReadOnlyDictionary<string, string>> _headers = [];
    private int _opened;

    /// <summary>The first connection.</summary>
    public FakeWsConnection Connection => At(0);

    /// <summary>Makes every connect attempt fail with this.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Makes the attempt with this zero-based number fail (return null to let it through).</summary>
    public Func<int, Exception?>? Fail { get; set; }

    /// <summary>Scripts connection n as it is opened.</summary>
    public Action<int, FakeWsConnection>? OnOpen { get; set; }

    /// <summary>Connect attempts so far, failed ones included.</summary>
    public int ConnectCount { get; private set; }

    public Uri? Url { get; private set; }

    public IReadOnlyDictionary<string, string>? Headers => _headers.Count == 0 ? null : _headers[^1];

    /// <summary>The headers of every successful connect, in order.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> HeadersSeen => _headers;

    public FakeWsConnection At(int index)
    {
        while (_connections.Count <= index)
        {
            _connections.Add(new FakeWsConnection());
        }

        return _connections[index];
    }

    public Task<IWsConnection> ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var attempt = ConnectCount++;
        Url = url;
        if ((FailWith ?? Fail?.Invoke(attempt)) is { } failure)
        {
            return Task.FromException<IWsConnection>(failure);
        }

        _headers.Add(headers);
        var index = _opened++;
        var connection = At(index);
        OnOpen?.Invoke(index, connection);
        return Task.FromResult<IWsConnection>(connection);
    }
}
