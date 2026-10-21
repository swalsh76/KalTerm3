using System.Text.Json;
using KalTerm.Contracts;

namespace KalTerm.Client;

/// <summary>What a market stream should watch.</summary>
/// <param name="Ticks">The markets whose ticker updates are wanted.</param>
/// <param name="Books">The markets whose order books are wanted.</param>
/// <param name="Depth">How many price levels each side of a book carries; the server's default when <see langword="null"/>.</param>
/// <param name="ResumeFrom">Replay the ticks since this moment first (a tick's id or the stream's <c>ResumeFrom</c>); <see langword="null"/> for new ticks only.</param>
public sealed record MarketStreamRequest(IReadOnlyList<string> Ticks, IReadOnlyList<string> Books, int? Depth = null, string? ResumeFrom = null);

/// <summary>Something a market stream says.</summary>
public abstract record MarketStreamEvent;

/// <summary>The stream is open: what the server accepted and where live data begins.</summary>
/// <param name="Ready">The details.</param>
public sealed record ReadyEvent(StreamReadyDto Ready) : MarketStreamEvent;

/// <summary>A book changed. It is the whole book: replace what you hold.</summary>
/// <param name="Book">The book.</param>
public sealed record BookEvent(OrderBookDto Book) : MarketStreamEvent;

/// <summary>A ticker update.</summary>
/// <param name="Tick">The update.</param>
/// <param name="Id">Its id: the moment of the update, usable as a resume point.</param>
public sealed record TickEvent(MarketTickDto Tick, string Id) : MarketStreamEvent;

/// <summary>What a market subscription is for.</summary>
public enum StreamKind
{
    /// <summary>Ticker updates.</summary>
    Ticks,

    /// <summary>Order books.</summary>
    Books,
}

/// <summary>A market was added to the stream's watch list (the confirmation of a live add). For ticks, <see cref="StreamSubscriptionDto.From"/> says where its live ticks begin; load earlier ones over REST.</summary>
/// <param name="Change">The details.</param>
public sealed record SubscribedEvent(StreamSubscriptionDto Change) : MarketStreamEvent;

/// <summary>A market was removed from the stream's watch list: nothing more arrives for it.</summary>
/// <param name="Change">The details.</param>
public sealed record UnsubscribedEvent(StreamSubscriptionDto Change) : MarketStreamEvent;

/// <summary>The server cannot continue from where the client was: reload over REST, then carry on.</summary>
/// <param name="Reason">Why.</param>
public sealed record ResetEvent(string Reason) : MarketStreamEvent;

/// <summary>
/// Keeps a market stream going: it opens the stream, hands every event over, and when the connection drops or the server cannot be reached it waits (1 s, doubling to 30 s, starting over after anything arrives) and opens
/// it again from where it was, so no tick is missed and none is repeated. When the server says it cannot continue (a reset) the caller is told, and the stream is opened afresh. A refusal that waiting will not cure
/// (a token the server does not accept) ends it.
/// </summary>
/// <param name="open">Opens one stream.</param>
/// <param name="time">The clock the waits use.</param>
/// <param name="change">Adds (true) or removes (false) one market of an open stream: the stream's id, what for, the ticker. Without it a change waits for the next connection.</param>
public sealed class MarketWatcher(Func<MarketStreamRequest, CancellationToken, IAsyncEnumerable<MarketStreamEvent>> open, TimeProvider? time = null, Func<string, StreamKind, string, bool, CancellationToken, Task>? change = null)
{

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _sync = new();
    private readonly HashSet<string> _ticks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _books = new(StringComparer.Ordinal);
    private string? _streamId;

    /// <summary>Starts watching a market on the open stream, or on the next connection when there is none. A market already on the list is left as it is.</summary>
    /// <param name="kind">Ticks or books.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the server has taken the change (or there is no open stream to tell).</returns>
    /// <exception cref="ApiException">The server would not take it (<c>too_many_markets</c>, <c>invalid_parameter</c>); the list is left as it was.</exception>
    public async Task AddAsync(StreamKind kind, string ticker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        string? id;
        lock (_sync)
        {
            if (!Set(kind).Add(ticker))
            {
                return;
            }

            id = _streamId;
        }

        if (id is null || change is null)
        {
            return;
        }

        try
        {
            await change(id, kind, ticker, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Gone(ex))
        {
            // The stream is gone: the next connection carries the list.
        }
        catch (ApiException)
        {
            lock (_sync)
            {
                Set(kind).Remove(ticker);
            }

            throw;
        }
    }

    /// <summary>Stops watching a market on the open stream, or on the next connection when there is none.</summary>
    /// <param name="kind">Ticks or books.</param>
    /// <param name="ticker">The market.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the server has taken the change (or there is no open stream to tell).</returns>
    public async Task RemoveAsync(StreamKind kind, string ticker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        string? id;
        lock (_sync)
        {
            if (!Set(kind).Remove(ticker))
            {
                return;
            }

            id = _streamId;
        }

        if (id is null || change is null)
        {
            return;
        }

        try
        {
            await change(id, kind, ticker, false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Gone(ex))
        {
            // The stream is gone: the next connection carries the list.
        }
    }

    private static bool Gone(Exception ex) => ex is ApiUnavailableException || ex is ApiException { Code: "not_found" };

    private HashSet<string> Set(StreamKind kind) => kind == StreamKind.Books ? _books : _ticks;

    /// <summary>Watches until cancelled.</summary>
    /// <param name="ticks">The markets whose ticks are wanted.</param>
    /// <param name="books">The markets whose books are wanted.</param>
    /// <param name="depth">The book depth, or <see langword="null"/> for the default.</param>
    /// <param name="onEvent">Called with every event, in order. A tick is delivered once, even across a reconnect.</param>
    /// <param name="onNotice">Called with a line of text when the stream dropped and is being reopened.</param>
    /// <param name="cancellationToken">Stops the watch.</param>
    /// <returns>A task that completes when the watch is cancelled.</returns>
    /// <exception cref="ApiException">The server refused in a way waiting will not cure.</exception>
    public async Task RunAsync(IReadOnlyList<string> ticks, IReadOnlyList<string> books, int? depth, Func<MarketStreamEvent, Task> onEvent, Action<string> onNotice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        ArgumentNullException.ThrowIfNull(onNotice);
        lock (_sync)
        {
            _ticks.UnionWith(ticks);
            _books.UnionWith(books);
        }

        string? resume = null;
        var atResume = new HashSet<string>(StringComparer.Ordinal);   // ticks already delivered at the resume moment, which the server sends again (it resumes inclusively)
        var wait = StreamReconnect.FirstWait;
        while (!cancellationToken.IsCancellationRequested)
        {
            string[] askedTicks;
            string[] askedBooks;
            lock (_sync)
            {
                askedTicks = [.. _ticks];
                askedBooks = [.. _books];
            }

            if (askedTicks.Length == 0 && askedBooks.Length == 0)
            {
                // Nothing is watched (every market was removed): the server refuses an empty stream, so wait for a market to be added.
                try
                {
                    await Task.Delay(StreamReconnect.FirstWait, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            var reset = false;
            try
            {
                await foreach (var item in open(new MarketStreamRequest(askedTicks, askedBooks, depth, resume), cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    wait = StreamReconnect.FirstWait;
                    switch (item)
                    {
                        case ReadyEvent ready:
                            resume ??= ready.Ready.ResumeFrom;
                            await ReconcileAsync(ready.Ready.StreamId, askedTicks, askedBooks, onNotice, cancellationToken).ConfigureAwait(false);
                            break;
                        case TickEvent tick:
                            var key = JsonSerializer.Serialize(tick.Tick, ApiJson.Wire.MarketTickDto);
                            if (string.Equals(tick.Id, resume, StringComparison.Ordinal))
                            {
                                if (!atResume.Add(key))
                                {
                                    continue;   // sent before the connection dropped
                                }
                            }
                            else
                            {
                                resume = tick.Id;
                                atResume.Clear();
                                atResume.Add(key);
                            }

                            break;
                        case ResetEvent:
                            reset = true;
                            resume = null;
                            atResume.Clear();
                            break;
                    }

                    await onEvent(item).ConfigureAwait(false);
                    if (reset)
                    {
                        onNotice("the server asked for a reload; reconnecting");
                        break;
                    }
                }

                if (!reset)
                {
                    onNotice("the server closed the stream; reconnecting");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (StreamReconnect.TryDescribe(ex, out var notice, out var longWait))
            {
                onNotice(notice);
                if (longWait)
                {
                    wait = StreamReconnect.LongestWait;
                }
            }
            finally
            {
                lock (_sync)
                {
                    _streamId = null;
                }
            }

            if (reset)
            {
                continue;   // a reset is answered at once: the caller reloads while the new stream opens
            }

            if (!await StreamReconnect.WaitAsync(wait, _time, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            wait = StreamReconnect.Next(wait);
        }
    }

    /// <summary>Records the new stream's id and tells it about any change made to the list after the request was built (it would otherwise be missed).</summary>
    private async Task ReconcileAsync(string streamId, string[] askedTicks, string[] askedBooks, Action<string> onNotice, CancellationToken cancellationToken)
    {
        List<(StreamKind Kind, string Ticker, bool Add)> changes = [];
        lock (_sync)
        {
            _streamId = streamId;
            foreach (var (kind, asked, now) in new[] { (StreamKind.Ticks, askedTicks, _ticks), (StreamKind.Books, askedBooks, _books) })
            {
                changes.AddRange(now.Except(asked, StringComparer.Ordinal).Select(t => (kind, t, true)));
                changes.AddRange(asked.Except(now, StringComparer.Ordinal).Select(t => (kind, t, false)));
            }
        }

        if (change is null)
        {
            return;
        }

        foreach (var (kind, ticker, add) in changes)
        {
            try
            {
                await change(streamId, kind, ticker, add, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
            {
                onNotice($"could not change the watch list ({ticker}): {ex.Message}");
            }
        }
    }
}
