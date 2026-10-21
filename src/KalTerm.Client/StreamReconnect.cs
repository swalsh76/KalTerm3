using System.Text.Json;

namespace KalTerm.Client;

/// <summary>What the stream watchers share when a connection ends: how a failure is told to the user, and how long to wait before opening the stream again (1 s, doubling to 30 s).</summary>
internal static class StreamReconnect
{
    /// <summary>The first wait after a failure.</summary>
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(30);

    /// <summary>Whether a failure is one that waiting and reconnecting answers, and the line to tell the user.</summary>
    /// <param name="ex">The failure.</param>
    /// <param name="notice">The line for the user.</param>
    /// <param name="longWait">Whether the server asked for patience: wait the longest time at once.</param>
    /// <returns>False for a failure waiting will not cure.</returns>
    public static bool TryDescribe(Exception ex, out string notice, out bool longWait)
    {
        longWait = false;
        switch (ex)
        {
            case ApiUnavailableException:
                notice = ex.Message;
                return true;
            case IOException:
                notice = "the connection dropped: " + ex.Message;
                return true;
            case ApiException { Code: "too_many_streams" or "http_error" } api:
                notice = api.Code == "too_many_streams" ? "the server has too many streams open for you; waiting" : $"the server answered {(api.Status is { } status ? (int)status : 0)}; waiting";
                longWait = true;
                return true;
            case JsonException:
                notice = "the server sent something unreadable; reconnecting";
                return true;
            default:
                notice = string.Empty;
                return false;
        }
    }

    /// <summary>Waits, and says whether the wait ran its course.</summary>
    /// <param name="wait">How long.</param>
    /// <param name="time">The clock.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>False when the watch was cancelled during the wait.</returns>
    public static async Task<bool> WaitAsync(TimeSpan wait, TimeProvider time, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The wait after this one: double, up to the longest.</summary>
    /// <param name="wait">This wait.</param>
    /// <returns>The next.</returns>
    public static TimeSpan Next(TimeSpan wait) => TimeSpan.FromSeconds(Math.Min(LongestWait.TotalSeconds, wait.TotalSeconds * 2));
}
