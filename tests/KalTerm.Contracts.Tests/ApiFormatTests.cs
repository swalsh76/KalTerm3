using System.Text.Json;
using KalTerm.Contracts;

namespace KalTerm.Contracts.Tests;

/// <summary>The formatter against the reference's own output (golden vectors), plus the edges the reference's unbounded integers cannot show.</summary>
public class ApiFormatTests
{
    private static JsonElement Vectors => VectorFile.Load("api_format.json");

    [Fact]
    public void DollarsMatchTheReference()
    {
        var cases = Vectors.GetProperty("dollars").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var input = c.GetProperty("input").ValueKind == JsonValueKind.Null ? (long?)null : long.Parse(c.GetProperty("input").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal(c.GetProperty("expected").GetString(), ApiFormat.Dollars(input));
        }
    }

    [Fact]
    public void CountsMatchTheReference()
    {
        var cases = Vectors.GetProperty("counts").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var input = c.GetProperty("input").ValueKind == JsonValueKind.Null ? (long?)null : long.Parse(c.GetProperty("input").GetString()!, CultureInfo.InvariantCulture);
            Assert.Equal(c.GetProperty("expected").GetString(), ApiFormat.Count(input));
        }
    }

    [Fact]
    public void TheSmallestLongStillFormatsExactly()
    {
        Assert.Equal("-9223372036854.775808", ApiFormat.Dollars(long.MinValue));
        Assert.Equal("-92233720368547758.08", ApiFormat.Count(long.MinValue));
        Assert.Equal("9223372036854.775807", ApiFormat.Dollars(long.MaxValue));
    }

    [Fact]
    public void MomentsMatchTheReferenceInUtc()
    {
        foreach (var c in Vectors.GetProperty("moments").EnumerateArray())
        {
            var moment = DateTimeOffset.Parse(c.GetProperty("input").GetString()!, CultureInfo.InvariantCulture).UtcDateTime;
            Assert.Equal(c.GetProperty("expected").GetString(), ApiFormat.Moment(moment));
        }
    }

    [Fact]
    public void AnUnspecifiedKindIsTakenAsUtcAndALocalOneIsConverted()
    {
        var unspecified = new DateTime(2026, 10, 18, 22, 23, 48, 123, DateTimeKind.Unspecified);
        var local = new DateTime(2026, 10, 18, 22, 23, 48, 123, DateTimeKind.Utc).ToLocalTime();

        Assert.Equal("2026-10-18T22:23:48.123000+00:00", ApiFormat.Moment(unspecified));
        Assert.Equal("2026-10-18T22:23:48.123000+00:00", ApiFormat.Moment(local));
        Assert.Null(ApiFormat.Moment((DateTime?)null));
        Assert.Equal("2026-10-18T22:23:48.123000+00:00", ApiFormat.Moment((DateTime?)unspecified));
    }

    [Fact]
    public void CursorsMatchTheReferenceBothWays()
    {
        foreach (var c in Vectors.GetProperty("cursorEncodings").EnumerateArray())
        {
            Assert.Equal(c.GetProperty("cursor").GetString(), ApiFormat.EncodeCursor(c.GetProperty("text").GetString()!));
        }

        foreach (var c in Vectors.GetProperty("cursorDecodings").EnumerateArray())
        {
            var expected = c.GetProperty("text").ValueKind == JsonValueKind.Null ? null : c.GetProperty("text").GetString();
            Assert.Equal(expected, ApiFormat.DecodeCursor(c.GetProperty("cursor").GetString()!));
        }
    }

    [Theory]
    [InlineData("KXHIGHNY-26OCT18-T70")]
    [InlineData("2026-10-18T22:23:48.123000+00:00|trade-1")]
    [InlineData("ünï|côde")]
    public void WhatIsEncodedComesBack(string text)
    {
        var cursor = ApiFormat.EncodeCursor(text);

        Assert.DoesNotContain('=', cursor);
        Assert.DoesNotContain('+', cursor);
        Assert.DoesNotContain('/', cursor);
        Assert.Equal(text, ApiFormat.DecodeCursor(cursor));
    }

    [Theory]
    [InlineData("QQ QQ")]       // whitespace inside is not ignored
    [InlineData("QQ\n")]
    [InlineData("QU+D")]        // the standard alphabet is not ours
    [InlineData("QU/D")]
    [InlineData("QQ=A")]        // padding only at the end
    [InlineData("")]
    public void AnythingThatIsNotOurCursorDecodesToNothing(string cursor)
    {
        Assert.Null(ApiFormat.DecodeCursor(cursor));
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => ApiFormat.EncodeCursor(null!));
        Assert.Throws<ArgumentNullException>(() => ApiFormat.DecodeCursor(null!));
    }
}
