using System.Net;

namespace KalTerm.Client.Tests;

public sealed class ValueTextTests
{
    [Theory]
    [InlineData("0.560000", "0.56")]
    [InlineData("1.000000", "1.00")]
    [InlineData("-0.050000", "-0.05")]
    [InlineData("not a number", "not a number")]
    [InlineData(null, "-")]
    public void DollarsShortenWithoutChangingTheValue(string? value, string expected) => Assert.Equal(expected, ValueText.Dollars(value));

    [Theory]
    [InlineData("1234.00", "1,234")]
    [InlineData("20.50", "20.50")]
    [InlineData("0.00", "0")]
    [InlineData("-1500.00", "-1,500")]
    [InlineData("x", "x")]
    [InlineData(null, "-")]
    public void CountsGetSeparatorsAndLoseAWholeNumbersFraction(string? value, string expected) => Assert.Equal(expected, ValueText.Count(value));

    [Theory]
    [InlineData(null, "-")]
    [InlineData(45L, "45s ago")]
    [InlineData(720L, "12m ago")]
    [InlineData(18_000L, "5h ago")]
    [InlineData(259_200L, "3d ago")]
    public void AgeIsCoarse(long? seconds, string expected) => Assert.Equal(expected, ValueText.Age(seconds));

    [Theory]
    [InlineData(null, "-")]
    [InlineData(12L, "12 B")]
    [InlineData(2048L, "2.0 kB")]
    [InlineData(276_824_064L, "264.0 MB")]
    [InlineData(3_221_225_472L, "3.0 GB")]
    public void SizeNamesTheUnit(long? bytes, string expected) => Assert.Equal(expected, ValueText.Size(bytes));

    [Fact]
    public void OnlyFailuresThatWaitingCuresAreDescribed()
    {
        Assert.True(StreamReconnect.TryDescribe(new IOException("reset"), out var dropped, out var dropLong));
        Assert.Equal("the connection dropped: reset", dropped);
        Assert.False(dropLong);

        Assert.True(StreamReconnect.TryDescribe(new ApiException(HttpStatusCode.TooManyRequests, "too_many_streams"), out var many, out var manyLong));
        Assert.Contains("too many streams", many, StringComparison.Ordinal);
        Assert.True(manyLong);

        Assert.True(StreamReconnect.TryDescribe(new System.Text.Json.JsonException(), out _, out _));
        Assert.False(StreamReconnect.TryDescribe(new ApiException(HttpStatusCode.Unauthorized, "unauthorized"), out _, out _));
        Assert.False(StreamReconnect.TryDescribe(new InvalidOperationException(), out var none, out _));
        Assert.Equal(string.Empty, none);
    }

    [Fact]
    public void TheWaitDoublesUpToTheLongest()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), StreamReconnect.Next(StreamReconnect.FirstWait));
        Assert.Equal(StreamReconnect.LongestWait, StreamReconnect.Next(StreamReconnect.LongestWait));
    }
}
