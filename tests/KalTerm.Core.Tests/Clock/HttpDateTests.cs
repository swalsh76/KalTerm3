using KalTerm.Core.Clock;

namespace KalTerm.Core.Tests.Clock;

public class HttpDateTests
{
    [Fact]
    public void TheReferencesOwnExample()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_791_334_891), HttpDate.Parse("Wed, 07 Oct 2026 01:01:31 GMT"));
    }

    [Theory]
    [InlineData("Sun, 06 Nov 1994 08:49:37 GMT")]    // the standard form
    [InlineData("Sunday, 06-Nov-94 08:49:37 GMT")]   // obsolete RFC 850
    [InlineData("Sun Nov  6 08:49:37 1994")]         // obsolete asctime
    [InlineData("Sun Nov 6 08:49:37 1994")]          // asctime with an unpadded day
    [InlineData("sun, 06 nov 1994 08:49:37 gmt")]    // case does not matter
    [InlineData("Wed, 06 Nov 1994 08:49:37 GMT")]    // the weekday is not cross-checked against the date
    public void TheThreeHttpFormatsGiveTheSameInstant(string text)
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(784_111_777), HttpDate.Parse(text));
    }

    [Fact]
    public void ParsedDatesAreUtcWithNoOffset()
    {
        Assert.Equal(TimeSpan.Zero, HttpDate.Parse("Wed, 07 Oct 2026 01:01:31 GMT").Offset);
    }

    [Theory]
    [InlineData("Sunday, 06-Nov-68 08:49:37 GMT", 2068)]   // two-digit years: up to 68 is 20xx ...
    [InlineData("Sunday, 06-Nov-69 08:49:37 GMT", 1969)]   // ... from 69 on is 19xx, as in the reference
    [InlineData("Sunday, 06-Nov-00 08:49:37 GMT", 2000)]
    [InlineData("Sunday, 06-Nov-99 08:49:37 GMT", 1999)]
    public void TwoDigitYearsPivotAtSixtyNine(string text, int year)
    {
        Assert.Equal(year, HttpDate.Parse(text).Year);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("2026-10-07T01:01:31Z")]                    // ISO 8601 is not an HTTP date
    [InlineData("Wed, 07 Oct 2026 01:01:31 +0000")]         // numeric zones are not HTTP dates
    [InlineData("Wed, 07 Oct 2026 01:01:31")]               // a zone is required
    [InlineData("Wed, 07 Oct 2026 01:01:31 UTC")]           // HTTP dates are GMT
    [InlineData(" Wed, 07 Oct 2026 01:01:31 GMT")]          // no surrounding whitespace
    [InlineData("Wed, 07 Oct 2026 01:01:31 GMT ")]
    [InlineData("Foo, 07 Oct 2026 01:01:31 GMT")]           // not a weekday name
    [InlineData("7 Oct 2026 01:01:31 GMT")]                 // a weekday is required
    [InlineData("Sun, 06 Nov 94 08:49:37 GMT")]             // the standard form has a four-digit year
    [InlineData("Wed, 32 Oct 2026 01:01:31 GMT")]           // not a real day
    [InlineData("Wed, 07 Foo 2026 01:01:31 GMT")]           // not a month
    [InlineData("Wed, 07 Oct 2026 25:01:31 GMT")]           // not a real hour
    [InlineData("Wed, 07 Oct 2026 23:59:60 GMT")]           // leap seconds are refused, as in the reference
    [InlineData("Wed, 30 Feb 2026 01:01:31 GMT")]           // not a real date
    public void AnythingElseIsAClearError(string text)
    {
        Assert.False(HttpDate.TryParse(text, out var value));
        Assert.Equal(default, value);
        var error = Assert.Throws<ClockCheckException>(() => HttpDate.Parse(text));
        Assert.Contains("Date header", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullIsRefused()
    {
        Assert.False(HttpDate.TryParse(null, out _));
        Assert.Throws<ClockCheckException>(() => HttpDate.Parse(null));
    }

    [Fact]
    public void ItRoundTripsTheFormatDotNetItselfWrites()
    {
        var now = new DateTimeOffset(2026, 10, 16, 23, 59, 59, TimeSpan.Zero);

        Assert.Equal(now, HttpDate.Parse(now.ToString("r", CultureInfo.InvariantCulture)));   // RFC 1123 "r" format
    }
}
