using System.Text.Json;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.WebSocket;

namespace KalTerm.Kalshi.Tests.WebSocket;

/// <summary>What the WebSocket client tests share: scripted server replies and bounded waits.</summary>
internal static class WsTestKit
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A server <c>trade</c> message.</summary>
    internal static string Trade(int sid, int seq) =>
        $$$"""{"type":"trade","sid":{{{sid}}},"seq":{{{seq}}},"msg":{"trade_id":"t{{{seq}}}","market_ticker":"M","yes_price_dollars":"0.5","no_price_dollars":"0.5","count_fp":"1.00"}}""";

    /// <summary>Answers a sent command with a <c>subscribed</c> reply carrying <paramref name="sid"/>.</summary>
    internal static void Subscribed(FakeWsConnection connection, string sent, int sid) =>
        connection.Push($$$"""{"id":{{{FakeWsConnection.IdOf(sent)}}},"type":"subscribed","msg":{"channel":"x","sid":{{{sid}}}}}""");

    /// <summary>Answers a sent command with an error reply.</summary>
    internal static void Error(FakeWsConnection connection, string sent, int code, string text) =>
        connection.Push($$$"""{"id":{{{FakeWsConnection.IdOf(sent)}}},"type":"error","msg":{"code":{{{code}}},"msg":"{{{text}}}"}}""");

    /// <summary>The <c>params</c> object of a sent command, as text.</summary>
    internal static string ParamsOf(string sent) => JsonDocument.Parse(sent).RootElement.GetProperty("params").GetRawText();

    /// <summary>Waits (in real time, bounded) for a condition.</summary>
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    /// <summary>The next message, failing instead of hanging if none comes.</summary>
    internal static async Task<WsMessage> NextAsync(IAsyncEnumerator<WsMessage> stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await stream.MoveNextAsync().AsTask().WaitAsync(timeout.Token) ? stream.Current : throw new InvalidOperationException("the stream ended");
    }

    /// <summary>Reads the stream until it ends, returning what arrived and how it ended (null: cleanly).</summary>
    internal static async Task<(List<WsMessage> Messages, Exception? End)> DrainAsync(KalshiWebSocket client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var messages = new List<WsMessage>();
        try
        {
            await foreach (var message in client.Messages(timeout.Token))
            {
                messages.Add(message);
            }

            return (messages, null);
        }
        catch (KalshiWsException ex)
        {
            return (messages, ex);
        }
    }
}
