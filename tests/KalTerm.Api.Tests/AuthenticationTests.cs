using System.Net;
using System.Net.Http.Headers;
using KalTerm.Contracts;
using KalTerm.Data.Auth;

namespace KalTerm.Api.Tests;

/// <summary>Users and tokens in memory, counting how often the store was asked.</summary>
internal sealed class FakeUsers : IUserStore
{
    public Dictionary<string, AuthenticatedUser> Tokens { get; } = new(StringComparer.Ordinal);

    public int Lookups { get; private set; }

    public string Add(string name, string role = UserRoles.Read)
    {
        var token = ApiTokens.Generate();
        Tokens[token] = new AuthenticatedUser(Tokens.Count + 1, name, role, Tokens.Count + 1);
        return token;
    }

    public Task<AuthenticatedUser?> AuthenticateAsync(string token, DateTime now, CancellationToken cancellationToken = default)
    {
        Lookups++;
        return Task.FromResult(Tokens.GetValueOrDefault(token));
    }

    public Task<int> AddUserAsync(string name, string role, DateTime now, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> DisableUserAsync(string name, DateTime now, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<(int Id, string Token)> CreateTokenAsync(string userName, string? label, DateTime? expiresAt, DateTime now, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ApiTokenInfo>> ListTokensAsync(string? userName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> RevokeTokenAsync(int tokenId, DateTime now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public sealed class AuthenticationTests : IAsyncDisposable
{
    private readonly FakeUsers _users = new();
    private readonly AutoAdvanceTimeProvider _time = new(autoFire: false, utcStart: Samples.Now);
    private ApiTestHost? _api;

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }
    }

    private async Task<ApiTestHost> StartAsync(string remote = "192.168.1.50", string local = "192.168.1.20", bool https = true)
    {
        _api = await ApiTestHost.StartWithAuthAsync(_users, _time, context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            context.Connection.LocalIpAddress = IPAddress.Parse(local);
            context.Request.IsHttps = https;
        });
        return _api;
    }

    private static async Task<HttpResponseMessage> GetAsync(ApiTestHost api, string url, string? authorization = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return await api.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task ARequestWithoutATokenGetsABareUnauthorizedAndNothingElse()
    {
        var api = await StartAsync();

        var response = await GetAsync(api, "/v1/markets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("unauthorized", ApiJson.Deserialize<ErrorDto>(await BodyAsync(response))!.Error);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task AValidTokenReachesTheData()
    {
        var api = await StartAsync();
        api.Fakes.Markets.Page = new([Samples.Market("A-1")], null);
        var token = _users.Add("ann");

        var response = await GetAsync(api, "/v1/markets", "Bearer " + token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("A-1", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownTokenAnExpiredOneAndNoTokenAreIndistinguishable()
    {
        var api = await StartAsync();
        var none = await GetAsync(api, "/v1/markets");
        var unknown = await GetAsync(api, "/v1/markets", "Bearer " + ApiTokens.Generate());
        var garbage = await GetAsync(api, "/v1/markets", "Bearer not-a-token");

        Assert.All(new[] { none, unknown, garbage }, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        var bodies = new[] { await BodyAsync(none), await BodyAsync(unknown), await BodyAsync(garbage) };
        Assert.Single(bodies.Distinct());
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Token kt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("kt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task OnlyABearerHeaderCountsEvenWithATokenInIt(string header)
    {
        var api = await StartAsync();
        var token = _users.Add("ann");

        var response = await GetAsync(api, "/v1/markets", header.Replace("kt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", token, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheSchemeIsCaseInsensitiveButTwoAuthorizationHeadersAreRefused()
    {
        var api = await StartAsync();
        var token = _users.Add("ann");
        using var twice = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/markets", UriKind.Relative));
        twice.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + token, "Bearer " + token });

        var lower = await GetAsync(api, "/v1/markets", "bearer " + token);
        var both = await api.Client.SendAsync(twice, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, both.StatusCode);
    }

    [Fact]
    public async Task HealthAndReadinessNeedNoTokenFromAnywhere()
    {
        var api = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(api, "/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(api, "/readyz")).StatusCode);
        Assert.Equal(0, _users.Lookups);
    }

    [Fact]
    public async Task StatusNeedsNoTokenOnlyOnThePlainLoopbackListener()
    {
        var onLan = await StartAsync();
        var lan = await GetAsync(onLan, "/v1/status");
        await onLan.DisposeAsync();

        var onLoopback = await StartAsync("127.0.0.1", "127.0.0.1", https: false);
        var status = await GetAsync(onLoopback, "/v1/status");
        var markets = await GetAsync(onLoopback, "/v1/markets");
        await onLoopback.DisposeAsync();

        var loopbackHttps = await StartAsync("127.0.0.1", "127.0.0.1", https: true);
        var httpsStatus = await GetAsync(loopbackHttps, "/v1/status");
        await loopbackHttps.DisposeAsync();

        var forwarded = await StartAsync("192.168.1.50", "127.0.0.1", https: false);
        var fromElsewhere = await GetAsync(forwarded, "/v1/status");

        Assert.Equal(HttpStatusCode.Unauthorized, lan.StatusCode);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, markets.StatusCode);   // only status, not the data
        Assert.Equal(HttpStatusCode.Unauthorized, httpsStatus.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fromElsewhere.StatusCode);   // a loopback listener reached by a remote caller (a proxy) is not trusted
    }

    [Fact]
    public async Task TenFailuresBlockThatCallerEvenWithAGoodTokenAndOthersAreUnaffected()
    {
        var api = await StartAsync();
        var token = _users.Add("ann");
        for (var i = 0; i < AuthThrottle.MaxFailures; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(api, "/v1/markets", "Bearer " + ApiTokens.Generate())).StatusCode);
        }

        var blocked = await GetAsync(api, "/v1/markets", "Bearer " + token);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("60", blocked.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("too_many_attempts", ApiJson.Deserialize<ErrorDto>(await BodyAsync(blocked))!.Error);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(api, "/healthz")).StatusCode);   // health stays open
        await api.DisposeAsync();

        var other = await StartAsync("192.168.1.77");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(other, "/v1/markets", "Bearer " + token)).StatusCode);
    }

    [Fact]
    public async Task AKnownTokenIsRememberedForHalfAMinuteAndAskedAgainAfterwards()
    {
        var api = await StartAsync();
        var token = _users.Add("ann");

        await GetAsync(api, "/v1/markets", "Bearer " + token);
        await GetAsync(api, "/v1/markets", "Bearer " + token);
        var twice = _users.Lookups;
        _time.Advance(TokenAuthenticator.CacheFor + TimeSpan.FromSeconds(1));
        await GetAsync(api, "/v1/markets", "Bearer " + token);

        Assert.Equal(1, twice);
        Assert.Equal(2, _users.Lookups);
    }

    [Fact]
    public async Task ATokenThatWasRevokedStopsWorkingOnceTheCacheHasExpired()
    {
        var api = await StartAsync();
        var token = _users.Add("ann");
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(api, "/v1/markets", "Bearer " + token)).StatusCode);

        _users.Tokens.Remove(token);
        _time.Advance(TokenAuthenticator.CacheFor + TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(api, "/v1/markets", "Bearer " + token)).StatusCode);
    }
}

public class AuthThrottleTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheTenthFailureInAMinuteBlocksAndTheBlockLiftsAMinuteAfterTheLastFailure()
    {
        var throttle = new AuthThrottle();
        for (var i = 0; i < 9; i++)
        {
            throttle.RecordFailure("a", Now.AddSeconds(i));
        }

        Assert.False(throttle.IsBlocked("a", Now.AddSeconds(9)));
        throttle.RecordFailure("a", Now.AddSeconds(9));

        Assert.True(throttle.IsBlocked("a", Now.AddSeconds(10)));
        Assert.True(throttle.IsBlocked("a", Now.AddSeconds(60)));
        Assert.False(throttle.IsBlocked("a", Now.AddSeconds(70)));   // all ten are more than a minute old
        Assert.False(throttle.IsBlocked("b", Now));
        Assert.Throws<ArgumentNullException>(() => throttle.IsBlocked(null!, Now));
        Assert.Throws<ArgumentNullException>(() => throttle.RecordFailure(null!, Now));
    }

    [Fact]
    public void AFloodOfDifferentAddressesCannotGrowMemoryWithoutBound()
    {
        var throttle = new AuthThrottle();

        for (var i = 0; i < 20_000; i++)
        {
            throttle.RecordFailure("flood-" + i, Now);
        }

        throttle.RecordFailure("late", Now);
        Assert.False(throttle.IsBlocked("flood-1", Now));   // forgotten rather than remembered forever
    }
}

public class TokenAuthenticatorTests
{
    [Fact]
    public async Task ATextWithoutTheShapeOfATokenNeverReachesTheStoreAndMissesAreNotRemembered()
    {
        var users = new FakeUsers();
        var authenticator = new TokenAuthenticator(users, TimeProvider.System);
        var unknown = ApiTokens.Generate();

        Assert.Null(await authenticator.AuthenticateAsync("nope", TestContext.Current.CancellationToken));
        Assert.Null(await authenticator.AuthenticateAsync(unknown, TestContext.Current.CancellationToken));
        var token = users.Add("ann");
        var found = await authenticator.AuthenticateAsync(token, TestContext.Current.CancellationToken);

        Assert.Equal(("ann", 2), (found!.UserName, users.Lookups));
        Assert.Throws<ArgumentNullException>(() => new TokenAuthenticator(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new TokenAuthenticator(users, null!));
    }

    [Fact]
    public async Task ABusyServerDoesNotKeepEveryTokenItEverSaw()
    {
        var users = new FakeUsers();
        var authenticator = new TokenAuthenticator(users, TimeProvider.System);
        var tokens = Enumerable.Range(0, 1_100).Select(i => users.Add("user" + i)).ToList();

        foreach (var token in tokens)
        {
            await authenticator.AuthenticateAsync(token, TestContext.Current.CancellationToken);
        }

        var before = users.Lookups;
        await authenticator.AuthenticateAsync(tokens[^1], TestContext.Current.CancellationToken);
        await authenticator.AuthenticateAsync(tokens[0], TestContext.Current.CancellationToken);

        Assert.Equal(1_100, before);
        Assert.Equal(1_101, users.Lookups);   // the newest is still remembered, an old one was forgotten when the cache was cleared
    }
}
