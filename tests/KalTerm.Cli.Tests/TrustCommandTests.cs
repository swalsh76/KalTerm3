using System.Net;
using KalTerm.Client;

namespace KalTerm.Cli.Tests;

public sealed class TrustCommandTests : IDisposable
{
    private const string Fingerprint = "AAAAAAAABBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string Token = "kt_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Harness _cli = new();

    public void Dispose() => _cli.Dispose();

    /// <summary>Long error lines wrap at the console width; compare them as one line.</summary>
    private static string Flat(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    // ---- trust ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TrustShowsTheCertificateAndPinsItWhenTheGivenFingerprintMatches()
    {
        var exit = await _cli.RunAsync("trust", "srv01:5280", "--fingerprint", string.Join(':', Enumerable.Range(0, 32).Select(i => Fingerprint.Substring(i * 2, 2))).ToLowerInvariant());

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal([("srv01", 5280)], _cli.Probed);
        Assert.Equal(Fingerprint, _cli.Pins.Get("srv01:5280")!.Fingerprint);
        Assert.Contains("subject:     CN=srv01, O=KalTerm", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("names:       srv01, 203.0.113.20", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("valid 2026-10-19 to 2028-10-18", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("srv01:5280 is trusted", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFingerprintThatDoesNotMatchTrustsNothingAndSaysSomethingElseMayBeAnswering()
    {
        var exit = await _cli.RunAsync("trust", "srv01:5280", "--fingerprint", new string('0', 64));

        Assert.Equal(CliApp.Failed, exit);
        Assert.Null(_cli.Pins.Get("srv01:5280"));
        Assert.Contains("not the one you gave", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("Something else may be answering", Flat(_cli.Error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtAKeyboardThePersonConfirmsByTypingTheStartOfTheFingerprint()
    {
        _cli.Interactive = true;
        _cli.Type("aaaaaaaa");

        var exit = await _cli.RunAsync("trust", "srv01:5280");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal(Fingerprint, _cli.Pins.Get("srv01:5280")!.Fingerprint);
        Assert.Contains("type its first 8 characters (AAAAAAAA)", _cli.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("y")]
    [InlineData("yes")]
    [InlineData("AAAA")]
    public async Task AnythingButTheStartOfTheFingerprintCancels(string typed)
    {
        _cli.Interactive = true;
        _cli.Type(typed);

        var exit = await _cli.RunAsync("trust", "srv01:5280");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Null(_cli.Pins.Get("srv01:5280"));
        Assert.Contains("nothing was trusted", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAKeyboardAndWithoutAFingerprintTheAnswerIsNo()
    {
        var exit = await _cli.RunAsync("trust", "srv01:5280");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Null(_cli.Pins.Get("srv01:5280"));
        Assert.Contains("--fingerprint", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustingWhatIsAlreadyTrustedChangesNothingButAReplacementNeedsAFreshConfirmationAndAWarning()
    {
        _cli.Pins.Set("srv01:5280", Fingerprint, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var same = await _cli.RunAsync("trust", "srv01:5280");
        Assert.Equal(CliApp.Ok, same);
        Assert.Contains("already trusted", _cli.Output, StringComparison.Ordinal);

        _cli.Pins.Set("srv01:5280", new string('C', 64), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var differentWithoutConfirmation = await _cli.RunAsync("trust", "srv01:5280");
        Assert.Equal(CliApp.Failed, differentWithoutConfirmation);
        Assert.Equal(new string('C', 64), _cli.Pins.Get("srv01:5280")!.Fingerprint);
        Assert.Contains("you trusted a DIFFERENT certificate", _cli.Output, StringComparison.Ordinal);

        var replaced = await _cli.RunAsync("trust", "srv01:5280", "--fingerprint", Fingerprint);
        Assert.Equal(CliApp.Ok, replaced);
        Assert.Equal(Fingerprint, _cli.Pins.Get("srv01:5280")!.Fingerprint);
    }

    [Fact]
    public async Task TheServerDefaultsToTheApiAddressAndOnlyHttpsServersCanBeTrusted()
    {
        var viaOption = await _cli.RunAsync("trust", "--api", "https://srv01:5280", "--fingerprint", Fingerprint);
        var http = await _cli.RunAsync("trust", "--api", "http://127.0.0.1:5280", "--fingerprint", Fingerprint);
        var noPort = await _cli.RunAsync("trust", "srv01");
        var badFingerprint = await _cli.RunAsync("trust", "srv01:5280", "--fingerprint", "xyz");
        var badApi = await _cli.RunAsync("trust", "--api", "ftp://x");

        Assert.Equal((0, 1, 1, 1, 1), (viaOption, http, noPort, badFingerprint, badApi));
        Assert.Contains("only https servers have a certificate", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("host:port", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("64 hex digits", _cli.Error, StringComparison.Ordinal);
        Assert.Contains("not an http or https address", _cli.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatCannotBeReachedIsReportedAsOneLine()
    {
        _cli.ProbeFails = new ApiUnavailableException("could not read the certificate of srv01:5280: connection refused");

        var exit = await _cli.RunAsync("trust", "srv01:5280", "--fingerprint", Fingerprint);

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("connection refused", _cli.Error, StringComparison.Ordinal);
        Assert.Null(_cli.Pins.Get("srv01:5280"));
    }

    // ---- login and logout -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATokenFromThePipeIsTriedAgainstTheServerAndStoredOnlyIfItWorks()
    {
        _cli.Type(Token);

        var exit = await _cli.RunAsync("login", "--api", "https://srv01:5280", "--token-stdin");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Equal([(new Uri("https://srv01:5280"), Token)], _cli.TokenTries);
        Assert.Equal(Token, _cli.Tokens.Get("srv01:5280"));
        Assert.DoesNotContain(Token, _cli.Output + _cli.Error, StringComparison.Ordinal);
        Assert.Contains("logged in to srv01:5280", _cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AtAKeyboardTheTokenIsAskedForWithoutEchoing()
    {
        _cli.Interactive = true;
        _cli.Secret = Token;

        var exit = await _cli.RunAsync("login", "--api", "https://srv01:5280");

        Assert.Equal(CliApp.Ok, exit);
        Assert.Contains("Token: ", _cli.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, _cli.Output, StringComparison.Ordinal);
        Assert.Equal(Token, _cli.Tokens.Get("srv01:5280"));
    }

    [Fact]
    public async Task ATokenTheServerRefusesIsNotStored()
    {
        _cli.Type(Token);
        _cli.Api.Throw = new ApiException(HttpStatusCode.Unauthorized, "unauthorized");

        var exit = await _cli.RunAsync("login", "--api", "https://srv01:5280", "--token-stdin");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Null(_cli.Tokens.Get("srv01:5280"));
        Assert.Contains("did not accept the token; nothing was stored", _cli.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a token")]
    [InlineData("kt_short")]
    public async Task SomethingThatCannotBeATokenIsRefusedBeforeTheServerIsAsked(string typed)
    {
        _cli.Type(typed);

        var exit = await _cli.RunAsync("login", "--api", "https://srv01:5280", "--token-stdin");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Empty(_cli.TokenTries);
        Assert.Null(_cli.Tokens.Get("srv01:5280"));
    }

    [Fact]
    public async Task AServerThatIsNotTrustedYetStopsLoginWithTheTrustInstruction()
    {
        _cli.Type(Token);
        _cli.Api.Throw = new TlsTrustException(TrustFailure.NotTrusted, "srv01:5280", null, Fingerprint, new InvalidOperationException());

        var exit = await _cli.RunAsync("login", "--api", "https://srv01:5280", "--token-stdin");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("kterm trust srv01:5280", Flat(_cli.Error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogoutForgetsTheStoredTokenAndSaysWhenThereWasNone()
    {
        _cli.Tokens.Set("srv01:5280", Token);

        var first = await _cli.RunAsync("logout", "--api", "https://srv01:5280");
        var second = await _cli.RunAsync("logout", "--api", "https://srv01:5280");
        var bad = await _cli.RunAsync("logout", "--api", "ftp://x");

        Assert.Equal((0, 0, 1), (first, second, bad));
        Assert.Null(_cli.Tokens.Get("srv01:5280"));
        Assert.Contains("stored token for srv01:5280 was removed", _cli.Output, StringComparison.Ordinal);
        Assert.Contains("no stored token", _cli.Output, StringComparison.Ordinal);
    }

    // ---- what the other commands say when trust or the token is missing -------------------------------------------------------------

    [Fact]
    public async Task ARefusedTokenOnAnyCommandPointsToLoginAndATooBusyCallerIsToldToWait()
    {
        _cli.Api.Throw = new ApiException(HttpStatusCode.Unauthorized, "unauthorized");
        var unauthorized = await _cli.RunAsync("status", "--api", "https://srv01:5280");
        var unauthorizedText = _cli.Error;
        using var second = new Harness();
        second.Api.Throw = new ApiException(HttpStatusCode.TooManyRequests, "too_many_attempts");
        var busy = await second.RunAsync("status", "--api", "https://srv01:5280");

        Assert.Equal((1, 1), (unauthorized, busy));
        Assert.Contains("did not accept the token", unauthorizedText, StringComparison.Ordinal);
        Assert.Contains("kterm login --api https://srv01:5280", Flat(unauthorizedText), StringComparison.Ordinal);
        Assert.Contains("too many failed attempts", second.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACertificateProblemOnAnyCommandIsShownInFullAndNeverAsAStackTrace()
    {
        _cli.Api.Throw = new TlsTrustException(TrustFailure.Changed, "srv01:5280", Fingerprint, new string('C', 64), new InvalidOperationException());

        var exit = await _cli.RunAsync("gaps", "--api", "https://srv01:5280");

        Assert.Equal(CliApp.Failed, exit);
        Assert.Contains("CHANGED", Flat(_cli.Error), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", _cli.Error, StringComparison.Ordinal);
    }
}
