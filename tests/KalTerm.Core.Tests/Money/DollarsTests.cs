using KalTerm.Core.Money;

namespace KalTerm.Core.Tests.Money;

public class DollarsTests
{
    [Fact]
    public void DefaultIsZero()
    {
        Assert.Equal(0, default(Dollars).Micros);
        Assert.Equal(Dollars.Zero, default);
    }

    [Fact]
    public void OneDollarIsAMillionMicros()
    {
        Assert.Equal(1_000_000, Dollars.One.Micros);
        Assert.Equal(Dollars.One, Dollars.FromWhole(1));
    }

    [Theory]
    [InlineData("0.4100", 410_000)]
    [InlineData("0.000001", 1)]
    [InlineData("-3.5", -3_500_000)]
    [InlineData("118.7499", 118_749_900)]
    public void ParseIsExact(string text, long micros)
    {
        Assert.Equal(micros, Dollars.Parse(text).Micros);
    }

    [Theory]
    [InlineData("0.1234567")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("9223372036854.775808")]
    public void ParseRefusesWhatItCannotRepresent(string text)
    {
        Assert.Throws<PrecisionException>(() => Dollars.Parse(text));
        Assert.False(Dollars.TryParse(text, out var result));
        Assert.Equal(Dollars.Zero, result);
    }

    [Fact]
    public void NullIsRefused()
    {
        Assert.Throws<PrecisionException>(() => Dollars.Parse(null));
        Assert.False(Dollars.TryParse(null, out _));
    }

    [Fact]
    public void FromDecimalIsExactOrRefused()
    {
        Assert.Equal(410_000, Dollars.FromDecimal(0.41m).Micros);
        Assert.Throws<PrecisionException>(() => Dollars.FromDecimal(0.0000001m));
        Assert.Throws<PrecisionException>(() => Dollars.FromDecimal(decimal.MaxValue));
    }

    [Fact]
    public void ToDecimalAndToStringRoundTrip()
    {
        var value = Dollars.Parse("-118.749900");
        Assert.Equal(-118.7499m, value.ToDecimal());
        Assert.Equal("-118.749900", value.ToString());
        Assert.Equal("0.000001", Dollars.FromMicros(1).ToString());
        Assert.Equal("0.000000", Dollars.Zero.ToString());
    }

    [Fact]
    public void ToStringIgnoresTheCurrentCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.500000", Dollars.Parse("1.5").ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void ArithmeticIsExactAndChecked()
    {
        var a = Dollars.Parse("0.41");
        var b = Dollars.Parse("0.59");
        Assert.Equal(Dollars.One, a + b);
        Assert.Equal(Dollars.Parse("-0.18"), a - b);
        Assert.Equal(Dollars.Parse("-0.41"), -a);

        var max = Dollars.FromMicros(long.MaxValue);
        Assert.Throws<OverflowException>(() => max + Dollars.FromMicros(1));
        Assert.Throws<OverflowException>(() => Dollars.FromMicros(-long.MaxValue) - Dollars.FromMicros(2));
    }

    [Fact]
    public void LongMinValueIsNotAValidAmount()
    {
        Assert.Throws<OverflowException>(() => Dollars.FromMicros(long.MinValue));
        Assert.Throws<OverflowException>(() => Dollars.FromMicros(-long.MaxValue) - Dollars.FromMicros(1));
    }

    [Fact]
    public void FromWholeDetectsOverflow()
    {
        Assert.Equal(9_000_000_000_000_000_000, Dollars.FromWhole(9_000_000_000_000).Micros);
        Assert.Throws<OverflowException>(() => Dollars.FromWhole(10_000_000_000_000));
    }

    [Fact]
    public void ComparisonsFollowTheAmount()
    {
        var low = Dollars.Parse("0.40");
        var high = Dollars.Parse("0.41");
        Assert.True(low < high);
        Assert.True(high > low);
        var equalToLow = Dollars.Parse("0.400000");
        var equalToHigh = Dollars.Parse("0.410000");
        Assert.True(low <= equalToLow);
        Assert.True(high >= equalToHigh);
        Assert.Equal(-1, low.CompareTo(high));
        Assert.Equal(low, equalToLow);
    }
}
