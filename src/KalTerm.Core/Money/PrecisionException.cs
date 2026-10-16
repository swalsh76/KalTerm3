namespace KalTerm.Core.Money;

/// <summary>
/// A value cannot be represented exactly at the scale of a fixed-point type: it is not a number,
/// it has more precision than the scale, or it does not fit in a <see cref="long"/>.
/// Values are refused, never rounded.
/// </summary>
public sealed class PrecisionException : ArgumentException
{
    public PrecisionException()
    {
    }

    public PrecisionException(string message)
        : base(message)
    {
    }

    public PrecisionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
