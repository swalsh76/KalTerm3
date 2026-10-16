using System.Globalization;

namespace KalTerm.Core.Clock;

/// <summary>
/// One round trip to the server: the local time just before the request, just after the response, and the earliest
/// server time the response implies (its <c>Date</c> plus its <c>Age</c>).
/// </summary>
/// <param name="Sent">Local time just before the request was sent.</param>
/// <param name="Received">Local time just after the response arrived.</param>
/// <param name="ServerTime">The server time the response stamped, in whole seconds.</param>
public readonly record struct ClockSample(DateTimeOffset Sent, DateTimeOffset Received, DateTimeOffset ServerTime)
{
    /// <summary>
    /// Builds a sample from a response's headers. A cached reply is old by its <c>Age</c> seconds, so the age is added
    /// to the stamped <c>Date</c>; an age that is not plain digits counts as zero. Throws <see cref="ClockCheckException"/>
    /// if there is no <c>Date</c> header or it cannot be parsed.
    /// </summary>
    public static ClockSample FromHeaders(DateTimeOffset sent, DateTimeOffset received, string? dateHeader, string? ageHeader)
    {
        if (dateHeader is null)
        {
            throw new ClockCheckException("response had no Date header");
        }

        var server = HttpDate.Parse(dateHeader);
        if (IsPlainDigits(ageHeader) && long.TryParse(ageHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var ageSeconds))
        {
            try
            {
                server = server.AddSeconds(ageSeconds);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new ClockCheckException($"Age header is out of range: '{ageHeader}'", ex);
            }
        }

        return new ClockSample(sent, received, server);
    }

    private static bool IsPlainDigits(string? text) =>
        !string.IsNullOrEmpty(text) && text.All(c => c is >= '0' and <= '9');
}
