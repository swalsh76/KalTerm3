using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace KalTerm.Kalshi.WebSocket;

/// <summary>The real connector, over <see cref="ClientWebSocket"/>.</summary>
internal sealed class ClientWebSocketConnector(int maxMessageBytes) : IWsConnector
{
    public async Task<IWsConnection> ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            foreach (var (name, value) in headers)
            {
                socket.Options.SetRequestHeader(name, value);
            }

            await socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
            return new Connection(socket, maxMessageBytes);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class Connection(ClientWebSocket socket, int maxMessageBytes) : IWsConnection
    {
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            try
            {
                // ClientWebSocket allows one send at a time.
                await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                throw new KalshiWsException("connection closed", ex);
            }
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var message = new ArrayBufferWriter<byte>();
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(message.GetMemory(16 * 1024), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await CloseWithAsync(WebSocketCloseStatus.InvalidMessageType).ConfigureAwait(false);
                        throw new KalshiWsException("the server sent a binary message; only text is supported");
                    }

                    message.Advance(result.Count);
                    if (message.WrittenCount > maxMessageBytes)
                    {
                        await CloseWithAsync(WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                        throw new KalshiWsException($"a message is larger than {maxMessageBytes} bytes");
                    }

                    if (result.EndOfMessage)
                    {
                        return Encoding.UTF8.GetString(message.WrittenSpan);
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                return null; // the link dropped without a close handshake
            }
        }

        public Task CloseAsync() => CloseWithAsync(WebSocketCloseStatus.NormalClosure);

        public async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
            socket.Dispose();
            _sendLock.Dispose();
        }

        private async Task CloseWithAsync(WebSocketCloseStatus status)
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                return;
            }

            using var timeout = new CancellationTokenSource(CloseTimeout);
            try
            {
                await socket.CloseOutputAsync(status, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Closing is best effort: the link may already be gone.
            }
        }
    }
}
