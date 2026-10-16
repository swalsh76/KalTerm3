namespace KalTerm.Core.Clock;

/// <summary>The clock could not be measured, for example because a response had no usable <c>Date</c> header.</summary>
public sealed class ClockCheckException : Exception
{
    public ClockCheckException()
    {
    }

    public ClockCheckException(string message)
        : base(message)
    {
    }

    public ClockCheckException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
