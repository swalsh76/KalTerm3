using KalTerm.Core.Money;

namespace KalTerm.Core.Tests.Money;

public class LevelTests
{
    [Theory]
    [InlineData("118.7499", 118_749_900)]   // a strike level, as in the reference's own fixed-point tests
    [InlineData("-3.5", -3_500_000)]
    [InlineData("0", 0)]
    [InlineData("0.000001", 1)]
    [InlineData("1e2", 100_000_000)]
    public void ParseIsExact(string text, long micros)
    {
        Assert.Equal(micros, Level.Parse(text).Micros);
        Assert.True(Level.TryParse(text, out var level));
        Assert.Equal(Level.FromMicros(micros), level);
    }

    [Theory]
    [InlineData("0.1234567")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("9223372036854.775808")]
    public void ParseRefusesWhatItCannotRepresent(string text)
    {
        Assert.Throws<PrecisionException>(() => Level.Parse(text));
        Assert.False(Level.TryParse(text, out var level));
        Assert.Equal(Level.Zero, level);
        Assert.False(Level.TryParse(null, out _));
    }

    [Fact]
    public void DecimalConversionsAreExact()
    {
        Assert.Equal(118_749_900, Level.FromDecimal(118.7499m).Micros);
        Assert.Equal(118.7499m, Level.FromMicros(118_749_900).ToDecimal());
        Assert.Throws<PrecisionException>(() => Level.FromDecimal(0.0000001m));
        Assert.Throws<OverflowException>(() => Level.FromMicros(long.MinValue));
    }

    [Fact]
    public void ToStringIsFixedPointInvariant()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("118.749900", Level.Parse("118.7499").ToString());
            Assert.Equal("-0.000001", Level.FromMicros(-1).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void LevelsOrderByValue()
    {
        var low = Level.Parse("1.5");
        var high = Level.Parse("1.500001");

        Assert.True(low < high);
        Assert.True(high > low);
        Assert.True(low <= Level.Parse("1.50"));
        Assert.True(high >= Level.Parse("1.500001"));
        Assert.Equal(-1, low.CompareTo(high));
    }

    [Fact]
    public void ALevelIsNotInterchangeableWithDollars()
    {
        // Same scale, different quantity: the types do not compare equal even for the same number.
        object level = Level.Parse("1");
        object dollars = Dollars.Parse("1");

        Assert.NotEqual(level, dollars);
    }
}
