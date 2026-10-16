using KalTerm.Core.Clock;
using KalTerm.Core.Money;
using KalTerm.Kalshi.Auth;
using KalTerm.Kalshi.Configuration;
using KalTerm.Kalshi.Models;
using KalTerm.Kalshi.Rest;

namespace KalTerm.Kalshi.Tests.Live;

/// <summary>Whether the live tests may run: only when asked for explicitly.</summary>
public static class LiveGate
{
    public static bool Enabled => Environment.GetEnvironmentVariable("KALSHI_INTEGRATION") == "1";
}

/// <summary>
/// Opt-in integration tests against the real Kalshi API, using a read-only key. Skipped by default and in CI.
///
/// Run (PowerShell), with the key's id and file path already in <c>KALSHI_KEY_ID</c> and <c>KALSHI_PRIVATE_KEY_PATH</c>:
/// <c>$env:KALSHI_INTEGRATION='1'; $env:KALSHI_ENV='production'; dotnet test --solution KalTerm.sln -c Release --filter-class *LiveKalshiTests</c>.
///
/// They only read: GET requests, about a dozen of them in total (two of up to 1,000 markets). The client has no write
/// methods and refuses any non-GET, so these tests cannot place or change an order. They never print the key id, the key
/// path or any key material. What they read is summarised in <c>live-summary.txt</c> next to the test assembly.
/// </summary>
public class LiveKalshiTests
{
    private const string Why = "set KALSHI_INTEGRATION=1 (with KALSHI_ENV, KALSHI_KEY_ID and KALSHI_PRIVATE_KEY_PATH) to run read-only tests against the real Kalshi API";

    private static (KalshiSettings Settings, KalshiSigner Signer) Credentials()
    {
        var settings = KalshiSettings.FromEnvironment();
        if (settings.KeyId is null || settings.PrivateKeyPath is null)
        {
            Assert.Skip("KALSHI_KEY_ID / KALSHI_PRIVATE_KEY_PATH are not configured");
        }

        Say($"target: {settings.Environment}, rest {settings.RestUrl.Host}");
        return (settings, KalshiSigner.FromSettings(settings));
    }

    private static KalshiRestClient Client(KalshiSettings settings, KalshiSigner signer) =>
        new(settings, new KalshiRestClientOptions { Signer = signer });

    private static void Say(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "live-summary.txt"), line + Environment.NewLine);
    }

    private static string Show(Dollars? price) => price is null ? "-" : price.Value.ToString();

    /// <summary>A real contract price is between $0 and $1 inclusive.</summary>
    private static void AssertIsAPrice(Dollars price, string ticker) =>
        Assert.True(price >= Dollars.Zero && price <= Dollars.One, $"{ticker}: {price} is not a price between $0 and $1");

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task ASignedReadOfTheExchangeStatusWorks()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = Client(settings, signer))
        {
            var status = await client.ExchangeStatusAsync(TestContext.Current.CancellationToken);

            Say($"exchange_active={status.ExchangeActive} trading_active={status.TradingActive}");
            Assert.True(status.ExchangeActive);
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task OrdinaryMarketsParseExactlyOnALargeRealSample()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = Client(settings, signer))
        {
            var query = KalshiQuery.Empty.With("limit", 1000).With("status", "open").With("mve_filter", "exclude");

            var page = await client.MarketsPageAsync(query, TestContext.Current.CancellationToken);

            Say($"ordinary open markets parsed: {page.Markets.Count}");
            foreach (var m in page.Markets.Take(3))
            {
                Say($"  {m.Ticker}: {m.Status} yes bid={Show(m.YesBidDollars)} ask={Show(m.YesAskDollars)} volume_24h={m.Volume24hFp}");
            }

            Assert.True(page.Markets.Count > 100);
            Assert.DoesNotContain(page.Markets, m => m.IsMultivariate);
            foreach (var m in page.Markets)
            {
                foreach (var price in new[] { m.YesBidDollars, m.YesAskDollars, m.NoBidDollars, m.NoAskDollars, m.LastPriceDollars })
                {
                    if (price is not null)
                    {
                        AssertIsAPrice(price.Value, m.Ticker);
                    }
                }
            }
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task ComboMarketsAreMultivariateAndTheirLegsParse()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = Client(settings, signer))
        {
            var query = KalshiQuery.Empty.With("limit", 200).With("status", "open").With("mve_filter", "only");

            var page = await client.MarketsPageAsync(query, TestContext.Current.CancellationToken);

            Say($"combo open markets parsed: {page.Markets.Count}");
            Assert.NotEmpty(page.Markets);
            Assert.All(page.Markets, m => Assert.True(m.IsMultivariate));
            Assert.All(page.Markets, m => Assert.NotEmpty(m.MveSelectedLegs!));
            Say($"  first combo: {page.Markets[0].Ticker}, {page.Markets[0].MveSelectedLegs!.Count} legs");
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task SeriesAndComboEventsParse()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = Client(settings, signer))
        {
            var series = await client.SeriesListAsync(cancellationToken: TestContext.Current.CancellationToken);
            var events = await client.MultivariateEventsPageAsync(KalshiQuery.Empty.With("limit", 5), TestContext.Current.CancellationToken);

            Say($"series: {series.Count}; with a fee schedule: {series.Count(s => s.FeeMultiplier is not null)}");
            Say($"combo events: {string.Join(" | ", events.Events.Take(3).Select(e => e.Title))}");
            Assert.NotEmpty(series);
            Assert.NotEmpty(events.Events);
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task TheBusiestMarketsOrderBookIsSane()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = Client(settings, signer))
        {
            var page = await client.MarketsPageAsync(
                KalshiQuery.Empty.With("limit", 1000).With("status", "open").With("mve_filter", "exclude"), TestContext.Current.CancellationToken);
            var busiest = page.Markets.OrderByDescending(m => m.Volume24hFp ?? Contracts.Zero).First();

            var book = await client.OrderBookAsync(busiest.Ticker, 10, TestContext.Current.CancellationToken);

            Say($"busiest market: {busiest.Ticker} (24h volume {busiest.Volume24hFp}); book {book.Yes.Count} yes / {book.No.Count} no levels");
            Say($"  top yes bid: {(book.Yes.Count > 0 ? $"{book.Yes.Max(l => l.Price)} for {book.Yes.OrderByDescending(l => l.Price).First().Quantity}" : "none")}");
            foreach (var level in book.Yes.Concat(book.No))
            {
                AssertIsAPrice(level.Price, busiest.Ticker);
                Assert.True(level.Quantity > Contracts.Zero, "a resting level has a positive quantity");
            }
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task RecentTradesHaveYesAndNoPricesThatSumToOneDollar()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = Client(settings, signer))
        {
            var query = KalshiQuery.Empty.With("min_ts", DateTimeOffset.UtcNow.AddMinutes(-60)).With("limit", 100);
            var seen = new List<Trade>();

            await foreach (var trade in client.IterateTradesAsync(query, TestContext.Current.CancellationToken))
            {
                seen.Add(trade);
                if (seen.Count >= 200)
                {
                    break;
                }
            }

            Say($"trades read from the last hour: {seen.Count}; newest: {(seen.Count > 0 ? seen.Max(t => t.CreatedTime).ToString("O", CultureInfo.InvariantCulture) : "none")}");
            Assert.NotEmpty(seen);
            Assert.All(seen, t => Assert.Equal(Dollars.One, t.YesPriceDollars + t.NoPriceDollars));
        }
    }

    [Fact(Skip = Why, SkipUnless = nameof(LiveGate.Enabled), SkipType = typeof(LiveGate))]
    public async Task ThisMachinesClockIsCloseEnoughToKalshis()
    {
        var (settings, signer) = Credentials();
        using (signer)
        using (var client = Client(settings, signer))
        {
            var skew = await client.ClockSkewAsync(cancellationToken: TestContext.Current.CancellationToken);

            Say(skew.Describe());
            Assert.NotEqual(ClockSkewStatus.Skewed, skew.Status);
        }
    }
}
