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

/// <summary>Hands out one <see cref="FakeWsConnection"/> and remembers how it was asked for.</summary>
internal sealed class FakeWsConnector : IWsConnector
{
    public FakeWsConnection Connection { get; } = new();

    public Exception? FailWith { get; set; }

    public int ConnectCount { get; private set; }

    public Uri? Url { get; private set; }

    public IReadOnlyDictionary<string, string>? Headers { get; private set; }

    public Task<IWsConnection> ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        ConnectCount++;
        Url = url;
        Headers = headers;
        return FailWith is null ? Task.FromResult<IWsConnection>(Connection) : Task.FromException<IWsConnection>(FailWith);
    }
}
