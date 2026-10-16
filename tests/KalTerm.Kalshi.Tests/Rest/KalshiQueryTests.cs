using KalTerm.Kalshi.Rest;

namespace KalTerm.Kalshi.Tests.Rest;

public class KalshiQueryTests
{
    [Fact]
    public void EmptyHasNoParametersAndNoQueryString()
    {
        Assert.Empty(KalshiQuery.Empty.Items);
        Assert.Equal(string.Empty, KalshiQuery.Empty.ToQueryString());
    }

    [Fact]
    public void ParametersKeepTheirOrderAndAreEscaped()
    {
        var query = KalshiQuery.Empty.With("status", "open").With("limit", 5).With("tickers", "A B&C,d");

        Assert.Equal("?status=open&limit=5&tickers=A%20B%26C%2Cd", query.ToQueryString());
        Assert.Equal(["status", "limit", "tickers"], query.Items.Select(i => i.Key));
    }

    [Fact]
    public void NullValuesAreDropped()
    {
        var query = KalshiQuery.Empty.With("cursor", (string?)null).With("limit", (long?)null).With("flag", (bool?)null)
            .With("price", (decimal?)null).With("since", (DateTimeOffset?)null);

        Assert.Empty(query.Items);
        Assert.Equal(string.Empty, query.ToQueryString());
    }

    [Fact]
    public void ValuesAreFormattedForKalshi()
    {
        var query = KalshiQuery.Empty
            .With("with_nested_markets", true)
            .With("exclude", false)
            .With("min_ts", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))
            .With("ratio", 1.5m)
            .With("big", 9_000_000_000L);

        Assert.Equal("?with_nested_markets=true&exclude=false&min_ts=1700000000&ratio=1.5&big=9000000000", query.ToQueryString());
    }

    [Fact]
    public void NumbersIgnoreTheCurrentCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("?ratio=1.5", KalshiQuery.Empty.With("ratio", 1.5m).ToQueryString());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void AddingAnExistingNameReplacesItAtTheEnd()
    {
        var query = KalshiQuery.Empty.With("a", "1").With("b", "2").With("a", "3");

        Assert.Equal("?b=2&a=3", query.ToQueryString());
    }

    [Fact]
    public void QueriesAreImmutable()
    {
        var baseQuery = KalshiQuery.Empty.With("status", "open");

        var paged = baseQuery.With("cursor", "c1");

        Assert.Equal("?status=open", baseQuery.ToQueryString());
        Assert.Equal("?status=open&cursor=c1", paged.ToQueryString());
        Assert.NotSame(baseQuery, paged);
        Assert.Same(baseQuery, baseQuery.With("cursor", (string?)null));   // a dropped value changes nothing
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ANameIsRequired(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => KalshiQuery.Empty.With(name!, "x"));
    }
}
