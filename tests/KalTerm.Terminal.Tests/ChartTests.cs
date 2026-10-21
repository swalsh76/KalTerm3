using KalTerm.Contracts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;
using ScottPlot;
using ScottPlot.Plottables;

namespace KalTerm.Terminal.Tests;

public class BookFormattingTests
{
    [Theory]
    [InlineData("0.540000", "0.560000", "0.550000")]
    [InlineData("0.570000", "0.550000", "0.560000")]
    [InlineData("0.000001", "0.000002", "0.000001")]   // a half millionth is dropped, not rounded up
    [InlineData("1.000000", "0.000000", "0.500000")]
    [InlineData("0.5", "0.6", "0.550000")]
    public void TheMiddleOfTwoPricesIsWorkedOutInWholeMillionths(string first, string second, string expected) => Assert.Equal(expected, Formatting.Midpoint(first, second));

    [Theory]
    [InlineData(null, "0.5")]
    [InlineData("0.5", null)]
    [InlineData("abc", "0.5")]
    [InlineData("0.5", "0.1234567")]
    public void ThereIsNoMiddleWhenAPriceIsMissingOrNotAPrice(string? first, string? second) => Assert.Null(Formatting.Midpoint(first, second));

    [Fact]
    public void CountsAreAddedExactlyInHundredthsAndWhatIsNotACountAddsNothing()
    {
        Assert.Equal("12.75", Formatting.SumCounts(["10.50", "2.25"]));
        Assert.Equal("1234567.89", Formatting.SumCounts(["1234567.00", "0.89"]));
        Assert.Equal("5.00", Formatting.SumCounts(["5.00", null, "oops", "-3.00", "1.234"]));
        Assert.Equal("0.00", Formatting.SumCounts([]));
        Assert.Throws<ArgumentNullException>(() => Formatting.SumCounts(null!));
    }

    [Theory]
    [InlineData("10.50", 1050L)]
    [InlineData("10.5", 1050L)]
    [InlineData("0.01", 1L)]
    [InlineData("0.00", 0L)]
    public void CountTextIsReadAsWholeHundredths(string text, long expected) => Assert.Equal(expected, Formatting.Hundredths(text));

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-1.00")]
    [InlineData("1.234")]
    [InlineData("7")]
    [InlineData("99999999999999999.00")]
    public void ThingsThatAreNotCountsAreRefused(string? text) => Assert.Null(Formatting.Hundredths(text));
}

public class ChartSeriesTests
{
    [Fact]
    public void ACandleIsReadFromTheExactTextAndKeepsIt()
    {
        var candle = ChartCandle.From(Samples.Candle(14, partial: true));

        Assert.Equal(new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc), candle.Start);
        Assert.Equal((0.5, 0.55, 0.45, 0.52, 100.0), (candle.Open, candle.High, candle.Low, candle.Close, candle.Volume));
        Assert.True(candle.HasPrice);
        Assert.True(candle.Partial);
        Assert.Equal("0.500000", candle.Source.Open);   // what a person reads comes from here, not from the double
    }

    [Fact]
    public void ABucketWithNoTradesHasNoPriceToDrawAndABrokenNumberIsNoNumber()
    {
        var none = ChartCandle.From(Samples.Candle(15, open: null, high: null, low: null, close: null, volume: "0.00"));
        var broken = ChartCandle.From(Samples.Candle(16, open: "x", volume: "y"));

        Assert.False(none.HasPrice);
        Assert.Equal(0, none.Volume);
        Assert.False(broken.HasPrice);
        Assert.Equal(0, broken.Volume);
        Assert.Throws<ArgumentNullException>(() => ChartCandle.From(null!));
    }

    [Fact]
    public void TheCandleUnderAMomentIsTheOneWhoseBucketContainsItAndTheEndOfABucketBelongsToTheNext()
    {
        var series = new ChartSeries(3600, [ChartCandle.From(Samples.Candle(12)), ChartCandle.From(Samples.Candle(13))]);
        var noon = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(noon, series.At(noon)!.Start);
        Assert.Equal(noon, series.At(noon.AddMinutes(59))!.Start);
        Assert.Equal(noon.AddHours(1), series.At(noon.AddHours(1))!.Start);
        Assert.Null(series.At(noon.AddMinutes(-1)));
        Assert.Null(series.At(noon.AddHours(2)));
        Assert.Null(ChartSeries.Empty.At(noon));
    }
}

public class CandlePlotBuilderTests
{
    private static readonly TimeZoneInfo Denver = TimeZoneInfo.FindSystemTimeZoneById("Mountain Standard Time");

    private static ChartSeries Series(params CandleDto[] candles) => new(3600, [.. candles.Select(ChartCandle.From)]);

    private static List<T> Of<T>(Plot plot)
        where T : IPlottable => [.. plot.GetPlottables().OfType<T>()];

    [Fact]
    public void WholeCandlesGapCandlesVolumeAndTheLastPriceAreEachDrawnOnceAndANoTradeBucketOnlyShowsItsVolume()
    {
        var plot = new Plot();
        var series = Series(Samples.Candle(12), Samples.Candle(13, partial: true), Samples.Candle(14), Samples.Candle(15, open: null, high: null, low: null, close: null, volume: "40.00"));

        CandlePlotBuilder.Draw(plot, series, TimeZoneInfo.Utc, "0.55");

        var candles = Of<CandlestickPlot>(plot);
        Assert.Equal([2, 1], candles.Select(c => c.Data.GetOHLCs().Count));   // the whole candles, then the gap candle; the bucket with no trades has no candle
        Assert.Equal(4, Of<BarPlot>(plot).Single().Bars.Count);   // every bucket with volume, whether it had a price or not
        Assert.Single(Of<HorizontalLine>(plot));
    }

    [Fact]
    public void WithoutALastPriceOrWithOneThatIsNotANumberThereIsNoLine()
    {
        var plot = new Plot();

        CandlePlotBuilder.Draw(plot, Series(Samples.Candle(12)), TimeZoneInfo.Utc, null);
        Assert.Empty(Of<HorizontalLine>(plot));
        CandlePlotBuilder.Draw(plot, Series(Samples.Candle(12)), TimeZoneInfo.Utc, "n/a");
        Assert.Empty(Of<HorizontalLine>(plot));
    }

    [Fact]
    public void DrawingAgainStartsFromAnEmptyPlot()
    {
        var plot = new Plot();
        var series = Series(Samples.Candle(12), Samples.Candle(13));

        CandlePlotBuilder.Draw(plot, series, TimeZoneInfo.Utc, "0.5");
        var once = plot.GetPlottables().Count();
        CandlePlotBuilder.Draw(plot, series, TimeZoneInfo.Utc, "0.5");

        Assert.Equal(once, plot.GetPlottables().Count());
    }

    [Fact]
    public void AnEmptySeriesDrawsNothingAndShowsAnEmptyUnitView()
    {
        var plot = new Plot();
        CandlePlotBuilder.Draw(plot, Series(Samples.Candle(12)), TimeZoneInfo.Utc, "0.5");

        CandlePlotBuilder.Draw(plot, ChartSeries.Empty, TimeZoneInfo.Utc, "0.5");

        Assert.Empty(plot.GetPlottables());
        Assert.Equal((0, 1), (plot.Axes.Bottom.Min, plot.Axes.Bottom.Max));
    }

    [Fact]
    public void TheTimesAreDrawnAsTheClockOfTheChosenZoneShows()
    {
        var plot = new Plot();

        CandlePlotBuilder.Draw(plot, Series(Samples.Candle(12)), Denver, null);

        var drawn = Of<CandlestickPlot>(plot).Single().Data.GetOHLCs()[0].DateTime;
        Assert.Equal(new DateTime(2026, 10, 20, 6, 0, 0), drawn);   // 12:00 UTC is 06:00 in Denver in October
    }

    [Fact]
    public void TheStartingViewHoldsEveryCandleKeepsPricesInsideZeroToOneAndLeavesTheTopOfThePlotToThePrices()
    {
        var plot = new Plot();
        var series = Series(Samples.Candle(12, high: "0.990000", low: "0.020000"), Samples.Candle(13, high: "0.970000", low: "0.100000", volume: "500.00"));

        CandlePlotBuilder.Draw(plot, series, TimeZoneInfo.Utc, null);

        var first = new DateTime(2026, 10, 20, 12, 0, 0).ToOADate();
        var last = new DateTime(2026, 10, 20, 14, 0, 0).ToOADate();
        Assert.True(plot.Axes.Bottom.Min < first && plot.Axes.Bottom.Max > last);
        Assert.True(plot.Axes.Left.Min >= 0 && plot.Axes.Left.Max <= 1);
        Assert.True(plot.Axes.Left.Min <= 0.02 && plot.Axes.Left.Max >= 0.97);
        Assert.Equal(0, plot.Axes.Right.Min);
        Assert.True(plot.Axes.Right.Max > 500 * 4);   // the tallest bar takes a fifth of the height, not all of it
    }

    [Fact]
    public void ARangeOfOnePriceStillGetsRoomAroundIt()
    {
        var plot = new Plot();

        CandlePlotBuilder.Draw(plot, Series(Samples.Candle(12, open: "0.500000", high: "0.500000", low: "0.500000", close: "0.500000")), TimeZoneInfo.Utc, null);

        Assert.True(plot.Axes.Left.Max - plot.Axes.Left.Min >= 0.02);
    }

    [Fact]
    public void ASeriesWithVolumeButNoPriceStillGetsAViewAndTheItRendersToAPicture()
    {
        var plot = new Plot();

        CandlePlotBuilder.Draw(plot, Series(Samples.Candle(12, open: null, high: null, low: null, close: null, volume: "30.00")), TimeZoneInfo.Utc, null);
        var png = plot.GetImageBytes(600, 300, ImageFormat.Png);

        Assert.Empty(Of<CandlestickPlot>(plot));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Take(4).Select(b => (int)b));
    }

    [Fact]
    public void ARealisticSeriesRendersToAPictureWithoutAWindow()
    {
        var plot = new Plot();
        var series = Series([.. Enumerable.Range(0, 24).Select(h => Samples.Candle(h, partial: h is 5 or 6, volume: (50 + (h * 13 % 200)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))]);

        CandlePlotBuilder.Draw(plot, series, Denver, "0.52");
        var png = plot.GetImageBytes(800, 400, ImageFormat.Png);

        Assert.True(png.Length > 5_000);
    }

    [Fact]
    public void MissingArgumentsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => CandlePlotBuilder.Draw(null!, ChartSeries.Empty, TimeZoneInfo.Utc, null));
        Assert.Throws<ArgumentNullException>(() => CandlePlotBuilder.Draw(new Plot(), null!, TimeZoneInfo.Utc, null));
        Assert.Throws<ArgumentNullException>(() => CandlePlotBuilder.Draw(new Plot(), ChartSeries.Empty, null!, null));
    }
}
