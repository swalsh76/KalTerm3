namespace KalTerm.Kalshi.WebSocket;

/// <summary>
/// A WebSocket command failed or the link was lost. When Kalshi refused a command, <see cref="Code"/> is its error code and
/// the message reads <c>[code] text</c>; for transport trouble (closed, timed out, not connected) there is no code.
/// </summary>
public sealed class KalshiWsException : Exception
{
    public KalshiWsException()
    {
    }

    public KalshiWsException(string message)
        : base(message)
    {
    }

    public KalshiWsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public KalshiWsException(string message, int? code)
        : base(code is null ? message : $"[{code}] {message}")
    {
        Code = code;
    }

    /// <summary>Kalshi's error code, or <see langword="null"/> for transport trouble.</summary>
    public int? Code { get; }
}
