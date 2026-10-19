namespace KalTerm.Cli.Tests;

public class DisplayTests
{
    [Theory]
    [InlineData(null, "-")]
    [InlineData("0.560000", "0.56")]
    [InlineData("1.000000", "1.00")]
    [InlineData("0.500000", "0.50")]
    [InlineData("0.123456", "0.123456")]
    [InlineData("0.010000", "0.01")]
    [InlineData("-0.010000", "-0.01")]
    [InlineData("12", "12")]
    [InlineData("abc", "abc")]
    public void DollarsKeepAtLeastTwoDecimalsAndDropTrailingZeros(string? value, string expected)
    {
        Assert.Equal(expected, Display.Dollars(value));
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData("1234.00", "1,234")]
    [InlineData("1234567.00", "1,234,567")]
    [InlineData("20.50", "20.50")]
    [InlineData("0.01", "0.01")]
    [InlineData("0.00", "0")]
    [InlineData("-5.00", "-5")]
    [InlineData("007.00", "7")]
    [InlineData("123456789012345678901234567890.00", "123,456,789,012,345,678,901,234,567,890")]
    [InlineData("x", "x")]
    public void CountsGetThousandsSeparatorsAndLoseAWholeNumbersDecimals(string? value, string expected)
    {
        Assert.Equal(expected, Display.Count(value));
    }

    [Theory]
    [InlineData("2026-10-18T22:23:48.123000+00:00", false, "2026-10-18 22:23:48")]
    [InlineData("2026-10-18T22:23:48.123000+00:00", true, "2026-10-18 22:23:48.123")]
    [InlineData("2026-10-18T16:23:48.500000-06:00", true, "2026-10-18 22:23:48.500")]
    [InlineData(null, false, "-")]
    [InlineData("not a time", false, "not a time")]
    public void TimesAreShownInUtc(string? iso, bool milliseconds, string expected)
    {
        Assert.Equal(expected, Display.Moment(iso, milliseconds));
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(0L, "0s ago")]
    [InlineData(119L, "119s ago")]
    [InlineData(120L, "2m ago")]
    [InlineData(7199L, "119m ago")]
    [InlineData(7200L, "2h ago")]
    [InlineData(172799L, "47h ago")]
    [InlineData(172800L, "2d ago")]
    public void AgesAreCoarse(long? seconds, string expected)
    {
        Assert.Equal(expected, Display.Age(seconds));
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(500L, "500 B")]
    [InlineData(1024L, "1.0 kB")]
    [InlineData(276_824_064L, "264.0 MB")]
    [InlineData(107_374_182_400L, "100.0 GB")]
    public void SizesUseTheLargestFittingUnit(long? bytes, string expected)
    {
        Assert.Equal(expected, Display.Size(bytes));
    }

    [Theory]
    [InlineData(276_824_064L, 107_374_182_400L, "0.2%")]
    [InlineData(50L, 100L, "50.0%")]
    [InlineData(1L, 3L, "33.3%")]
    [InlineData(5L, 0L, "-")]
    public void PercentagesUseIntegerMathWithOneDecimal(long part, long whole, string expected)
    {
        Assert.Equal(expected, Display.Percent(part, whole));
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(420, "7m 0s")]
    [InlineData(7_500, "2h 5m")]
    [InlineData(277_200, "3d 5h")]
    [InlineData(-30, "0s")]
    public void SpansNameTheirTwoLargestUnits(int seconds, string expected)
    {
        Assert.Equal(expected, Display.Span(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("15m", 900)]
    [InlineData("2h", 7_200)]
    [InlineData("7d", 604_800)]
    public void SinceTakesANumberAndAUnit(string text, int seconds)
    {
        Assert.True(Display.TryParseSince(text, out var span));
        Assert.Equal(TimeSpan.FromSeconds(seconds), span);
    }

    [Theory]
    [InlineData("0m")]
    [InlineData("m")]
    [InlineData("15")]
    [InlineData("15x")]
    [InlineData("-5m")]
    [InlineData("1.5h")]
    [InlineData("1000000d")]
    [InlineData("")]
    [InlineData(null)]
    public void SinceRefusesAnythingElse(string? text)
    {
        Assert.False(Display.TryParseSince(text, out _));
    }

    [Theory]
    [InlineData("2026-10-18T22:00:00Z", "2026-10-18T22:00:00")]
    [InlineData("2026-10-18T22:00:00+05:30", "2026-10-18T16:30:00")]
    [InlineData("2026-10-18T22:00:00.5-06:00", "2026-10-19T04:00:00.5")]
    public void TimesWithAnOffsetAreConvertedToUtc(string text, string expectedUtc)
    {
        Assert.True(CliApp.TryParseTime(text, out var utc));
        Assert.Equal(DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Theory]
    [InlineData("2026-10-18T22:00:00")]
    [InlineData("2026-10-18")]
    [InlineData("noon")]
    [InlineData("")]
    public void TimesWithoutAnOffsetAreRefused(string text)
    {
        Assert.False(CliApp.TryParseTime(text, out _));
    }

    [Fact]
    public void ApiTextIsEscapedSoBracketsCannotBreakTheMarkup()
    {
        Assert.Equal("a [[red]]b[[/]]", Display.Escape("a [red]b[/]"));
        Assert.Equal(string.Empty, Display.Escape(null));
    }
}
