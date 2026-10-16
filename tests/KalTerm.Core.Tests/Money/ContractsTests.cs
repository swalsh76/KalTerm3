using KalTerm.Core.Money;

namespace KalTerm.Core.Tests.Money;

public class ContractsTests
{
    [Fact]
    public void OneContractIsAHundredHundredths()
    {
        Assert.Equal(100, Contracts.One.Hundredths);
        Assert.Equal(Contracts.One, Contracts.FromWhole(1));
        Assert.Equal(Contracts.Zero, default);
    }

    [Theory]
    [InlineData("104.24", 10_424)]
    [InlineData("0.03", 3)]
    [InlineData("7", 700)]
    [InlineData("-2.5", -250)]
    public void ParseIsExact(string text, long hundredths)
    {
        Assert.Equal(hundredths, Contracts.Parse(text).Hundredths);
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("1e-3")]
    [InlineData("abc")]
    [InlineData("92233720368547758.08")]
    public void ParseRefusesWhatItCannotRepresent(string text)
    {
        Assert.Throws<PrecisionException>(() => Contracts.Parse(text));
        Assert.False(Contracts.TryParse(text, out _));
    }

    [Fact]
    public void FromDecimalIsExactOrRefused()
    {
        Assert.Equal(10_424, Contracts.FromDecimal(104.24m).Hundredths);
        Assert.Throws<PrecisionException>(() => Contracts.FromDecimal(0.001m));
    }

    [Fact]
    public void ToDecimalAndToStringRoundTrip()
    {
        var value = Contracts.Parse("104.24");
        Assert.Equal(104.24m, value.ToDecimal());
        Assert.Equal("104.24", value.ToString());
        Assert.Equal("0.03", Contracts.FromHundredths(3).ToString());
        Assert.Equal("-2.50", Contracts.Parse("-2.5").ToString());
    }

    [Fact]
    public void ArithmeticIsExactAndChecked()
    {
        Assert.Equal(Contracts.Parse("3.5"), Contracts.Parse("1.25") + Contracts.Parse("2.25"));
        Assert.Equal(Contracts.Parse("-1"), Contracts.Parse("1.25") - Contracts.Parse("2.25"));
        Assert.Equal(Contracts.Parse("-1.25"), -Contracts.Parse("1.25"));
        Assert.Throws<OverflowException>(() => Contracts.FromHundredths(long.MaxValue) + Contracts.FromHundredths(1));
        Assert.Throws<OverflowException>(() => Contracts.FromHundredths(long.MinValue));
    }

    [Fact]
    public void ComparisonsFollowTheCount()
    {
        Assert.True(Contracts.Parse("1.00") < Contracts.Parse("1.01"));
        Assert.True(Contracts.Parse("2") > Contracts.Parse("1.99"));
        Assert.True(Contracts.Parse("1") <= Contracts.Parse("1.00"));
        Assert.True(Contracts.Parse("1") >= Contracts.Parse("1.00"));
    }
}
