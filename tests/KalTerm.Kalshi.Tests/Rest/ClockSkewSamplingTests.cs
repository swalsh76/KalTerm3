using System.Net;
using System.Text;
using KalTerm.Core.Clock;
using KalTerm.Kalshi.Rest;
using static KalTerm.Kalshi.Tests.Rest.RestTestKit;

namespace KalTerm.Kalshi.Tests.Rest;

/// <summary>The reference's clock-skew REST tests (test_clock.py), ported, plus the timing of the winning attempt.</summary>
public class ClockSkewSamplingTests
{
    /// <summary>A 200 whose <c>Date</c> is the server's clock: our clock minus how far ahead of the server we are, truncated to seconds.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> ServerDate(AutoAdvanceTimeProvider time, TimeSpan localAhead, string? age = null) =>
        _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("Date", (time.GetUtcNow() - localAhead).ToString("r", CultureInfo.InvariantCulture));
            if (age is not null)
            {
                response.Headers.TryAddWithoutValidation("Age", age);
            }

            return response;
        };

    private static StubHttpMessageHandler Script(int count, Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        var stub = new StubHttpMessageHandler();
        for (var i = 0; i < count; i++)
        {
            stub.RespondWith(response);
        }

        return stub;
    }

    [Fact]
    public async Task ASkewedLocalClockIsMeasured()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = Script(5, ServerDate(time, TimeSpan.FromSeconds(4)));   // the local clock is 4 s ahead of the server
        using var client = Client(stub, time);

        var skew = await client.ClockSkewAsync(samples: 5, spacing: TimeSpan.FromMilliseconds(250), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(skew.Consistent);
        Assert.Equal(5, skew.Samples);
        Assert.InRange(TimeSpan.FromSeconds(4), skew.Low, skew.High);
        Assert.Equal(ClockSkewStatus.Skewed, skew.Status);
        Assert.Contains("ahead of Kalshi", skew.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGoodClockIsOkAndSamplesAreSpacedBetweenReadingsOnly()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = Script(4, ServerDate(time, TimeSpan.Zero));
        using var client = Client(stub, time);

        var skew = await client.ClockSkewAsync(samples: 4, spacing: TimeSpan.FromMilliseconds(300), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ClockSkewStatus.Ok, skew.Status);
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(300), 3), time.Delays);   // between samples only
        Assert.All(stub.Requests, r => Assert.Equal(Base + "/exchange/status", r.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task TheDefaultsAreFiveSamplesQuarterOfASecondApart()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = Script(5, ServerDate(time, TimeSpan.Zero));
        using var client = Client(stub, time);

        var skew = await client.ClockSkewAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, skew.Samples);
        Assert.Equal(5, stub.Requests.Count);
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(250), 4), time.Delays);
        Assert.Equal(ClockSkewEstimator.DefaultThreshold, skew.Threshold);
    }

    [Fact]
    public async Task TheAgeHeaderIsAddedToTheServerTime()
    {
        var time = new AutoAdvanceTimeProvider();
        // A cached reply generated 3 s ago: Date is 3 s old and Age says so. Without Age this would look 3 s skewed.
        var stub = Script(1, ServerDate(time, TimeSpan.FromSeconds(3), age: "3"));
        using var client = Client(stub, time);

        var skew = await client.ClockSkewAsync(samples: 1, threshold: TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ClockSkewStatus.Ok, skew.Status);
    }

    [Fact]
    public async Task WithoutTheAgeHeaderTheSameReplyLooksSkewed()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = Script(1, ServerDate(time, TimeSpan.FromSeconds(3)));
        using var client = Client(stub, time);

        var skew = await client.ClockSkewAsync(samples: 1, threshold: TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ClockSkewStatus.Skewed, skew.Status);   // the control for the test above
    }

    [Fact]
    public async Task AMissingDateHeaderIsAClearError()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{}");
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        var error = await Assert.ThrowsAsync<ClockCheckException>(() => client.ClockSkewAsync(samples: 1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Date", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnparseableDateHeaderIsAClearError()
    {
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.OK, "{}", new Dictionary<string, string> { ["Date"] = "yesterday" });
        using var client = Client(stub, new AutoAdvanceTimeProvider());

        await Assert.ThrowsAsync<ClockCheckException>(() => client.ClockSkewAsync(samples: 1, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheDateHeaderIsReadExactlyAsSentNotReformatted()
    {
        var time = new AutoAdvanceTimeProvider();
        var stub = new StubHttpMessageHandler().Respond(
            HttpStatusCode.OK, "{}", new Dictionary<string, string> { ["Date"] = "Tue, 14 Nov 2023 22:13:20 GMT" });   // the clock's own start instant
        using var client = Client(stub, time);

        var skew = await client.ClockSkewAsync(samples: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.InRange(TimeSpan.Zero, skew.Low, skew.High);   // local and server agree to the second
    }

    [Fact]
    public async Task ASampleIsTimedAroundTheAttemptThatSucceededNotTheRetriesBeforeIt()
    {
        var time = new AutoAdvanceTimeProvider();
        var start = time.GetUtcNow();
        var stub = new StubHttpMessageHandler().Respond(HttpStatusCode.ServiceUnavailable).RespondWith(ServerDate(time, TimeSpan.Zero));
        using var client = Client(stub, time, jitter: 1.0);

        var response = await client.GetResponseAsync("/exchange/status", null, TestContext.Current.CancellationToken);

        Assert.Equal(start + TimeSpan.FromMilliseconds(500), response.Sent);   // after the 500 ms backoff, not before the first try
        Assert.Equal(response.Sent, response.Received);                          // nothing passes on the fake clock during the request
        Assert.NotNull(response.DateHeader);
        Assert.Null(response.AgeHeader);
    }

    [Fact]
    public async Task ArgumentsAreValidated()
    {
        using var client = Client(new StubHttpMessageHandler(), new AutoAdvanceTimeProvider());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ClockSkewAsync(samples: 0, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ClockSkewAsync(spacing: TimeSpan.FromMilliseconds(-1), cancellationToken: TestContext.Current.CancellationToken));
    }
}
