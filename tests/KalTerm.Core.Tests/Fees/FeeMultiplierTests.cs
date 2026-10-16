using KalTerm.Core.Fees;
using KalTerm.Core.Money;

namespace KalTerm.Core.Tests.Fees;

public class FeeMultiplierTests
{
    [Fact]
    public void DefaultIsZeroAndOneIsAMillionMicros()
    {
        Assert.Equal(0, default(FeeMultiplier).Micros);
        Assert.Equal(FeeMultiplier.Zero, default);
        Assert.Equal(1_000_000, FeeMultiplier.One.Micros);
    }

    [Theory]
    [InlineData("0.07", 70_000)]
    [InlineData("1", 1_000_000)]
    [InlineData("0.5", 500_000)]
    [InlineData("0", 0)]
    [InlineData("0.000001", 1)]
    public void ParseIsExact(string text, long micros)
    {
        Assert.Equal(micros, FeeMultiplier.Parse(text).Micros);
        Assert.True(FeeMultiplier.TryParse(text, out var result));
        Assert.Equal(micros, result.Micros);
    }

    [Theory]
    [InlineData("0.0000001")]
    [InlineData("abc")]
    [InlineData("")]
    public void ParseRefusesWhatItCannotRepresent(string text)
    {
        Assert.Throws<PrecisionException>(() => FeeMultiplier.Parse(text));
        Assert.False(FeeMultiplier.TryParse(text, out _));
    }

    [Fact]
    public void NullIsRefused()
    {
        Assert.Throws<PrecisionException>(() => FeeMultiplier.Parse(null));
        Assert.False(FeeMultiplier.TryParse(null, out _));
    }

    [Fact]
    public void ToStringIsFixedPointInvariant()
    {
        Assert.Equal("0.070000", FeeMultiplier.Parse("0.07").ToString());
        Assert.Equal("1.000000", FeeMultiplier.One.ToString());
    }
}
