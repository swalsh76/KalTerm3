using KalTerm.Admin;
using KalTerm.Data.Auth;
using KalTerm.Security;

namespace KalTerm.Admin.Tests;

internal sealed class MemoryUsers : IUserStore
{
    private readonly List<AppUser> _users = [];
    private readonly List<(ApiTokenInfo Info, string Token)> _tokens = [];

    public List<string> Tokens => [.. _tokens.Select(t => t.Token)];

    public Task<int> AddUserAsync(string name, string role, DateTime now, CancellationToken cancellationToken = default)
    {
        if (!UserStore.IsValidName(name))
        {
            throw new InvalidOperationException("a user name is 2 to 32 lower-case letters, digits, dots, dashes or underscores, starting with a letter");
        }

        if (!UserRoles.IsValid(role))
        {
            throw new InvalidOperationException("the role must be read or admin");
        }

        if (_users.Any(u => u.Name == name))
        {
            throw new InvalidOperationException($"there is already a user named {name}");
        }

        _users.Add(new AppUser(_users.Count + 1, name, role, now, null));
        return Task.FromResult(_users.Count);
    }

    public Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AppUser>>([.. _users.OrderBy(u => u.Name)]);

    public Task<bool> DisableUserAsync(string name, DateTime now, CancellationToken cancellationToken = default)
    {
        var index = _users.FindIndex(u => u.Name == name && u.DisabledAt is null);
        if (index >= 0)
        {
            _users[index] = _users[index] with { DisabledAt = now };
        }

        return Task.FromResult(index >= 0);
    }

    public Task<(int Id, string Token)> CreateTokenAsync(string userName, string? label, DateTime? expiresAt, DateTime now, CancellationToken cancellationToken = default)
    {
        if (!_users.Any(u => u.Name == userName && u.DisabledAt is null))
        {
            throw new InvalidOperationException($"there is no enabled user named {userName}");
        }

        var token = ApiTokens.Generate();
        var id = _tokens.Count + 1;
        _tokens.Add((new ApiTokenInfo(id, userName, label, now, expiresAt, null, null), token));
        return Task.FromResult((id, token));
    }

    public Task<IReadOnlyList<ApiTokenInfo>> ListTokensAsync(string? userName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApiTokenInfo>>([.. _tokens.Select(t => t.Info).Where(t => userName is null || t.UserName == userName).OrderByDescending(t => t.Id)]);

    public Task<bool> RevokeTokenAsync(int tokenId, DateTime now, CancellationToken cancellationToken = default)
    {
        var index = _tokens.FindIndex(t => t.Info.Id == tokenId && t.Info.RevokedAt is null);
        if (index >= 0)
        {
            _tokens[index] = (_tokens[index].Info with { RevokedAt = now }, _tokens[index].Token);
        }

        return Task.FromResult(index >= 0);
    }

    public Task<AuthenticatedUser?> AuthenticateAsync(string token, DateTime now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public sealed class AdminAccessTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kalterm-admin-access-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly MemoryUsers _users = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        _out.Dispose();
        _err.Dispose();
    }

    private async Task<int> RunAsync(params string[] args)
    {
        var env = new AdminEnvironment
        {
            Out = _out,
            Error = _err,
            In = new StringReader(string.Empty),
            CreateServices = () => throw new InvalidOperationException("backups are not needed here"),
            CreateAccess = () => new AccessServices(_users, Path.Combine(_dir, "tls"), new FixedClock(Now)),
        };
        return await AdminApp.RunAsync(args, env, TestContext.Current.CancellationToken);
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    [Fact]
    public async Task UsersAreAddedListedAndDisabledAndBadInputIsExplained()
    {
        var none = await RunAsync("user", "list");
        var ann = await RunAsync("user", "add", "ann");
        var bob = await RunAsync("user", "add", "bob", "--role", "admin");
        var again = await RunAsync("user", "add", "ann");
        var badName = await RunAsync("user", "add", "Bad Name");
        var badRole = await RunAsync("user", "add", "cat", "--role", "root");
        var disabled = await RunAsync("user", "disable", "bob");
        var disabledAgain = await RunAsync("user", "disable", "bob");
        var list = await RunAsync("user", "list");

        Assert.Equal((0, 0, 0, 1, 1, 1, 0, 1, 0), (none, ann, bob, again, badName, badRole, disabled, disabledAgain, list));
        var output = _out.ToString();
        Assert.Contains("no users yet", output, StringComparison.Ordinal);
        Assert.Contains("user ann added (id 1); make a token with: token create --user ann", output, StringComparison.Ordinal);
        Assert.Contains("bob", output, StringComparison.Ordinal);
        Assert.Contains("2026-10-19 12:00:00", output, StringComparison.Ordinal);
        var errors = _err.ToString();
        Assert.Contains("already a user named ann", errors, StringComparison.Ordinal);
        Assert.Contains("lower-case letters", errors, StringComparison.Ordinal);
        Assert.Contains("the role must be read or admin", errors, StringComparison.Ordinal);
        Assert.Contains("no enabled user named bob", errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenIsPrintedOnceWithAWarningAndTheListNeverShowsIt()
    {
        await RunAsync("user", "add", "ann");

        var created = await RunAsync("token", "create", "--user", "ann", "--label", "laptop with a very long label indeed", "--expires-days", "30");
        var token = _users.Tokens.Single();
        var listed = await RunAsync("token", "list");
        var revoked = await RunAsync("token", "revoke", "1");
        var revokedAgain = await RunAsync("token", "revoke", "1");

        Assert.Equal((0, 0, 0, 1), (created, listed, revoked, revokedAgain));
        var output = _out.ToString();
        Assert.Contains("token 1 for ann:", output, StringComparison.Ordinal);
        Assert.Equal(2, output.Split(token).Length);   // the token text appears exactly once in everything printed
        Assert.Contains("only time the token is shown", output, StringComparison.Ordinal);
        Assert.Contains("laptop with a very ", output, StringComparison.Ordinal);
        Assert.Contains("2026-11-18 12:00:00", output, StringComparison.Ordinal);   // expiry
        Assert.Contains("token 1 revoked", output, StringComparison.Ordinal);
        Assert.Contains("there is no active token 1", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokensForUnknownUsersOrWithABadLifetimeAreRefusedAndTheListCanBeFiltered()
    {
        await RunAsync("user", "add", "ann");
        await RunAsync("user", "add", "bob");
        await RunAsync("token", "create", "--user", "ann");
        await RunAsync("token", "create", "--user", "bob", "--label", "b");

        var unknown = await RunAsync("token", "create", "--user", "nobody");
        var badDays = await RunAsync("token", "create", "--user", "ann", "--expires-days", "0");
        _out.GetStringBuilder().Clear();
        var onlyBob = await RunAsync("token", "list", "--user", "bob");
        var noneFor = await RunAsync("token", "list", "--user", "cat");

        Assert.Equal((1, 1, 0, 0), (unknown, badDays, onlyBob, noneFor));
        Assert.Contains("no enabled user named nobody", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("--expires-days must be positive", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("bob", _out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ann", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("no tokens", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACertificateIsMadeShownAndNeverSilentlyReplacedByInit()
    {
        var noneYet = await RunAsync("cert", "show");
        var init = await RunAsync("cert", "init", "--host", "kaldb", "--ip", "192.168.1.20");
        var show = await RunAsync("cert", "show");
        var again = await RunAsync("cert", "init", "--host", "kaldb");

        Assert.Equal((1, 0, 0, 1), (noneYet, init, show, again));
        Assert.Contains("cert init", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("there is already a certificate", _err.ToString(), StringComparison.Ordinal);
        var output = _out.ToString();
        Assert.Contains("names:       kaldb, 192.168.1.20, 127.0.0.1", output, StringComparison.Ordinal);
        Assert.Contains("validity:    valid 2026-10-19 to 2028-10-18", output, StringComparison.Ordinal);
        var fingerprints = output.Split('\n').Where(l => l.StartsWith("fingerprint: ", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
        Assert.Equal(2, fingerprints.Count);
        Assert.Equal(fingerprints[0], fingerprints[1]);   // what init printed is what show prints
        Assert.Contains("Restart the API", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadAddressOrLifetimeIsRefusedBeforeAnythingIsWritten()
    {
        var badIp = await RunAsync("cert", "init", "--host", "kaldb", "--ip", "not-an-ip");
        var badDays = await RunAsync("cert", "init", "--host", "kaldb", "--days", "0");
        var noHost = await RunAsync("cert", "init");

        Assert.Equal((1, 1, 1), (badIp, badDays, noHost));
        Assert.Contains("'not-an-ip' is not an IP address", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("--days must be positive", _err.ToString(), StringComparison.Ordinal);
        Assert.False(ServerCertificate.Exists(Path.Combine(_dir, "tls")));
    }

    [Fact]
    public async Task RotatingKeepsTheNamesGivesANewFingerprintAndSaysTheOldOne()
    {
        await RunAsync("cert", "init", "--host", "kaldb", "--ip", "192.168.1.20");
        using var before = ServerCertificate.Load(Path.Combine(_dir, "tls"));
        _out.GetStringBuilder().Clear();

        var rotate = await RunAsync("cert", "rotate");
        using var after = ServerCertificate.Load(Path.Combine(_dir, "tls"));

        Assert.Equal(0, rotate);
        Assert.NotEqual(CertificateFingerprint.Of(before), CertificateFingerprint.Of(after));
        Assert.Equal(CertificateInfo.Of(before).Names, CertificateInfo.Of(after).Names);
        var output = _out.ToString();
        Assert.Contains("old fingerprint: " + CertificateFingerprint.Format(CertificateFingerprint.Of(before)), output, StringComparison.Ordinal);
        Assert.Contains("fingerprint: " + CertificateFingerprint.Format(CertificateFingerprint.Of(after)), output, StringComparison.Ordinal);
        Assert.Equal(1, await RunAsync("cert", "rotate", "--days", "0"));
    }

    [Fact]
    public async Task RotatingWithoutACertificateSaysHowToMakeOne()
    {
        var exit = await RunAsync("cert", "rotate");

        Assert.Equal(1, exit);
        Assert.Contains("cert init", _err.ToString(), StringComparison.Ordinal);
    }
}
