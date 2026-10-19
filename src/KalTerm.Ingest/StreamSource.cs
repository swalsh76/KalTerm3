using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.WebSocket;

namespace KalTerm.Ingest;

/// <summary>One open, subscribed WebSocket connection (with its own reconnecting) that the runner reads until it ends.</summary>
public interface IStreamSession : IAsyncDisposable
{
    /// <summary>True while the link is up.</summary>
    bool IsConnected { get; }

    /// <summary>Queue health of the underlying client.</summary>
    WsStats Stats { get; }

    /// <summary>The order book subscription's id on the current connection (it changes after a reconnect), or <see langword="null"/> when the stream has none.</summary>
    int? OrderBookSid { get; }

    /// <summary>Asks for a fresh in-stream snapshot of one watched market; the reply arrives in <see cref="Messages"/>.</summary>
    /// <param name="sid">The order book subscription.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The command id, which the reply echoes.</returns>
    Task<int> RequestSnapshotAsync(int sid, string ticker, CancellationToken cancellationToken = default);

    /// <summary>The messages in order; ends normally when the session is closed and throws <c>KalshiWsException</c> when the link is lost for good. Enumerate once.</summary>
    /// <param name="cancellationToken">Stops the enumeration.</param>
    /// <returns>The messages.</returns>
    IAsyncEnumerable<WsMessage> Messages(CancellationToken cancellationToken = default);
}

/// <summary>Opens a subscribed stream; a seam so the runner can be tested without a network.</summary>
public interface IStreamSource
{
    /// <summary>Connects and subscribes. Throws if it cannot; the caller retries.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>The session; the caller disposes it.</returns>
    Task<IStreamSession> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The Kalshi public data channels: ticker and trade for every market (a subscription with no market list), plus the order book deltas of the watch list. The
/// order book subscription always names its markets: an empty list would be read by Kalshi as every market, so with no watch list there is no such subscription.
/// </summary>
/// <param name="settings">Where to connect (demo by default).</param>
/// <param name="signer">Signs the upgrade request; the caller owns it and disposes it after the last session.</param>
/// <param name="options">Client options, for example reconnect limits; <see langword="null"/> for the defaults.</param>
/// <param name="watchList">The markets whose order books are built; none for no order book subscription.</param>
public sealed class KalshiStreamSource(KalshiSettings settings, KalshiSigner signer, KalshiWebSocketOptions? options = null, IReadOnlyList<string>? watchList = null) : IStreamSource
{
    /// <summary>The channel of order book snapshots and deltas.</summary>
    public const string OrderBookChannel = "orderbook_delta";

    /// <summary>The channels subscribed for every market, in order: the latest top of book per market, and every executed trade.</summary>
    public static IReadOnlyList<string> Channels { get; } = ["ticker", "trade"];

    /// <inheritdoc/>
    public async Task<IStreamSession> OpenAsync(CancellationToken cancellationToken = default)
    {
        var socket = new KalshiWebSocket(settings, signer, options);
        try
        {
            await socket.ConnectAsync(cancellationToken).ConfigureAwait(false);
            foreach (var channel in Channels)
            {
                await socket.SubscribeAsync(channel, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (watchList is { Count: > 0 })
            {
                await socket.SubscribeAsync(OrderBookChannel, watchList, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return new Session(socket);
        }
        catch
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Session(KalshiWebSocket socket) : IStreamSession
    {
        public bool IsConnected => socket.IsConnected;

        public WsStats Stats => socket.Stats;

        public int? OrderBookSid => socket.Subscriptions.FirstOrDefault(s => s.Channel == OrderBookChannel)?.Sid;

        public Task<int> RequestSnapshotAsync(int sid, string ticker, CancellationToken cancellationToken = default) =>
            socket.RequestSnapshotAsync(sid, ticker, cancellationToken);

        public IAsyncEnumerable<WsMessage> Messages(CancellationToken cancellationToken = default) => socket.Messages(cancellationToken);

        public ValueTask DisposeAsync() => socket.DisposeAsync();
    }
}
