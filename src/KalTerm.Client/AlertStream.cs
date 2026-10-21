using KalTerm.Contracts;

namespace KalTerm.Client;

/// <summary>One thing the alert stream said: where it stands (a fire id to resume from) and, for a fire, the fire.</summary>
/// <param name="Id">The fire id the stream is at after this event; resume from it.</param>
/// <param name="Fire">The fire; <see langword="null"/> for the event that says the stream is open.</param>
public sealed record AlertStreamEvent(long Id, AlertFireDto? Fire);

/// <summary>Reads Server-Sent Events: lines of <c>field: value</c>, a blank line ending each event, lines that start with a colon ignored.</summary>
internal static class ServerSentEvents
{
    /// <summary>The events of a stream, as (event type, id, data), until it ends.</summary>
    public static async IAsyncEnumerable<(string Type, string? Id, string Data)> ReadAsync(TextReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var type = "message";
        string? id = null;
        var data = new System.Text.StringBuilder();
        var any = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (any)
                {
                    yield return (type, id, data.ToString());
                }

                type = "message";
                id = null;
                data.Clear();
                any = false;
                continue;
            }

            if (line[0] == ':')
            {
                continue;   // a comment: the keep-alive
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..].TrimStart(' ');
            switch (field)
            {
                case "event":
                    type = value;
                    any = true;
                    break;
                case "id":
                    id = value;
                    any = true;
                    break;
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    any = true;
                    break;
            }
        }
    }
}

/// <summary>
/// Keeps an alert stream going: it opens the stream, hands every fire over, and when the connection drops or the server cannot be reached it waits (1 s, doubling to 30 s, starting over after anything arrives)
/// and opens it again from the last id it saw, so nothing is missed and nothing is repeated. A refusal that waiting will not cure (a token the server does not accept) ends it.
/// </summary>
/// <param name="open">Opens one stream from an id (<see langword="null"/> for the newest).</param>
/// <param name="time">The clock the waits use.</param>
public sealed class AlertWatcher(Func<long?, CancellationToken, IAsyncEnumerable<AlertStreamEvent>> open, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Watches until cancelled.</summary>
    /// <param name="after">Replay fires after this id first; <see langword="null"/> for new fires only.</param>
    /// <param name="onFire">Called with every fire, in order, once.</param>
    /// <param name="onNotice">Called with a line of text when the stream dropped and is being reopened.</param>
    /// <param name="cancellationToken">Stops the watch.</param>
    /// <returns>A task that completes when the watch is cancelled.</returns>
    /// <exception cref="ApiException">The server refused in a way waiting will not cure.</exception>
    public async Task RunAsync(long? after, Func<AlertFireDto, Task> onFire, Action<string> onNotice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onFire);
        ArgumentNullException.ThrowIfNull(onNotice);
        var last = after;
        var wait = StreamReconnect.FirstWait;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var item in open(last, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    last = item.Id;
                    wait = StreamReconnect.FirstWait;
                    if (item.Fire is { } fire)
                    {
                        await onFire(fire).ConfigureAwait(false);
                    }
                }

                onNotice("the server closed the stream; reconnecting");
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

            if (!await StreamReconnect.WaitAsync(wait, _time, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            wait = StreamReconnect.Next(wait);
        }
    }
}
