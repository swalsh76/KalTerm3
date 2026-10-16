namespace KalTerm.Kalshi.WebSocket;

/// <summary>
/// One open WebSocket, reduced to what the client needs: send a text message, receive the next whole text message, close.
/// The seam that lets the client be tested against a scripted fake instead of a network.
/// </summary>
internal interface IWsConnection : IAsyncDisposable
{
    /// <summary>Sends one text message. Throws <see cref="KalshiWsException"/> if the connection is closed.</summary>
    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>The next whole text message, or <see langword="null"/> once the peer has closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Closes the connection politely and quietly; safe to call more than once.</summary>
    Task CloseAsync();
}

/// <summary>Opens connections. A seam like <see cref="IWsConnection"/>.</summary>
internal interface IWsConnector
{
    /// <summary>Opens a connection, sending <paramref name="headers"/> with the upgrade request.</summary>
    Task<IWsConnection> ConnectAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}
