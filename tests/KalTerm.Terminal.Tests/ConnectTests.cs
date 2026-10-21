using KalTerm.Client;
using KalTerm.Terminal.Connect;

namespace KalTerm.Terminal.Tests;

public sealed class ConnectViewModelTests : IDisposable
{
    private readonly TestServices _t = new();

    public void Dispose() => _t.Dispose();

    private ConnectViewModel Model(Uri? server = null) => new(_t.Services, server);

    private static string Start => Samples.Fingerprint[..8];

    [Fact]
    public void ItStartsAtTheAddressAndRemembersTheServerGiven()
    {
        var model = Model(new Uri("https://srv01:5280/"));

        Assert.True(model.OnAddress);
        Assert.Equal("https://srv01:5280", model.Address);
        Assert.Equal("Connect to a KalTerm server", model.Heading);
        Assert.True(model.NextCommand.CanExecute(null));
        Assert.False(Model().NextCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("srv01")]
    [InlineData("http://example.invalid:5280")]
    [InlineData("srv01:99999")]
    public async Task AnAddressThatIsNotAServerIsRefusedWithAnExample(string address)
    {
        var model = Model();
        model.Address = address;

        await model.NextCommand.RunAsync();

        Assert.True(model.HasMessage);
        Assert.Contains("srv01:5280", model.Message, StringComparison.Ordinal);
        Assert.Empty(_t.Probed);
    }

    [Fact]
    public async Task AServerNotTrustedYetShowsItsCertificateAndOnlyTheTypedStartTrustsIt()
    {
        var model = Model();
        model.Address = "srv01:5280";

        await model.NextCommand.RunAsync();

        Assert.True(model.OnTrust);
        Assert.Equal([("srv01", 5280)], _t.Probed);
        Assert.Equal("srv01:5280", model.ServerName);
        Assert.Equal("CN=srv01, O=KalTerm", model.Subject);
        Assert.Equal("valid 2026-10-19 to 2028-10-18", model.Validity);
        Assert.Equal("srv01, 203.0.113.20", model.Names);
        Assert.StartsWith("AB:CD:EF:01:23:45:67:89", model.Fingerprint, StringComparison.Ordinal);
        Assert.Contains(Start, model.ConfirmHint, StringComparison.Ordinal);
        Assert.False(model.TrustCommand.CanExecute(null));

        model.ConfirmText = "ABCDEF00";
        Assert.False(model.Confirmed);
        model.TrustCommand.Execute(null);
        Assert.Null(_t.Pins.Get(TestServices.Key));

        model.ConfirmText = " " + Start.ToLowerInvariant() + " ";
        Assert.True(model.Confirmed);
        model.TrustCommand.Execute(null);

        Assert.Equal(Samples.Fingerprint, _t.Pins.Get(TestServices.Key)!.Fingerprint);
        Assert.True(model.OnLogin);
    }

    [Fact]
    public async Task ATrustedServerWithAStoredTokenConnectsAtOnce()
    {
        _t.Pins.Set(TestServices.Key, Samples.Fingerprint, Samples.Now.UtcDateTime);
        _t.Tokens.Set(TestServices.Key, Samples.Token);
        var model = Model();
        model.Address = "https://srv01:5280";
        var closed = 0;
        model.Closed += () => closed++;

        await model.NextCommand.RunAsync();

        Assert.Equal(new Uri("https://srv01:5280"), model.Result);
        Assert.Equal(1, closed);
        Assert.Empty(_t.Probed);
    }

    [Fact]
    public async Task ATrustedServerWithoutATokenAsksForOne()
    {
        _t.Pins.Set(TestServices.Key, Samples.Fingerprint, Samples.Now.UtcDateTime);
        var model = Model();
        model.Address = "srv01:5280";

        await model.NextCommand.RunAsync();

        Assert.True(model.OnLogin);
        Assert.Null(model.Result);
    }

    [Fact]
    public async Task ALoopbackHttpServerNeedsNoCertificate()
    {
        var model = Model();
        model.Address = "http://127.0.0.1:5280";

        await model.NextCommand.RunAsync();

        Assert.True(model.OnLogin);
        Assert.Empty(_t.Probed);
        Assert.Equal("127.0.0.1:5280", model.ServerName);
    }

    [Fact]
    public async Task ATokenIsStoredOnlyAfterTheServerAcceptsIt()
    {
        var model = Model();
        model.Address = "http://127.0.0.1:5280";
        await model.NextCommand.RunAsync();
        model.Token = Samples.Token;
        Assert.True(model.LoginCommand.CanExecute(null));

        await model.LoginCommand.RunAsync();

        Assert.Equal(Samples.Token, _t.Tokens.Get("127.0.0.1:5280"));
        Assert.Equal(new Uri("http://127.0.0.1:5280"), model.Result);
        Assert.Equal(string.Empty, model.Token);
        Assert.Equal([(new Uri("http://127.0.0.1:5280"), Samples.Token)], _t.TokenTries);
        Assert.True(_t.TokenApi.Disposed);
    }

    [Fact]
    public async Task ATokenTheServerRefusesIsNotStored()
    {
        _t.TokenApi.MarketsFail = Samples.Unauthorized();
        var model = Model();
        model.Address = "http://127.0.0.1:5280";
        await model.NextCommand.RunAsync();
        model.Token = Samples.Token;

        await model.LoginCommand.RunAsync();

        Assert.Null(_t.Tokens.Get("127.0.0.1:5280"));
        Assert.Null(model.Result);
        Assert.Contains("did not accept the token; nothing was stored", model.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("kt_short")]
    [InlineData("xx_0123456789abcdef0123456789abcdef0123456789")]
    public async Task SomethingThatIsNotATokenIsRefusedWithoutAskingTheServer(string token)
    {
        var model = Model(new Uri("http://127.0.0.1:5280"));
        model.StartAtLogin(new Uri("http://127.0.0.1:5280"), string.Empty);
        model.Token = token;

        await model.LoginCommand.RunAsync();

        Assert.Contains("does not look like a token", model.Message, StringComparison.Ordinal);
        Assert.Empty(_t.TokenTries);
    }

    [Fact]
    public async Task AServerThatCannotBeReachedSaysSoAndLeavesTheDialogOpen()
    {
        _t.ProbeFails = new HttpRequestException("no route to host");
        var model = Model();
        model.Address = "srv01:5280";

        await model.NextCommand.RunAsync();

        Assert.Equal("Cannot reach the server: no route to host", model.Message);
        Assert.True(model.OnAddress);
    }

    [Fact]
    public async Task AFailureWhileTryingATokenIsShown()
    {
        _t.TokenApi.MarketsFail = new ApiUnavailableException("could not reach 127.0.0.1:5280");
        var model = Model();
        model.StartAtLogin(new Uri("http://127.0.0.1:5280"), "log in");
        model.Token = Samples.Token;

        await model.LoginCommand.RunAsync();

        Assert.Equal("could not reach 127.0.0.1:5280", model.Message);
    }

    [Fact]
    public async Task ACertificateThatChangedStartsAtTheTrustStepWithAWarning()
    {
        _t.Pins.Set(TestServices.Key, new string('1', 64), Samples.Now.UtcDateTime);
        _t.Tokens.Set(TestServices.Key, Samples.Token);
        var model = Model();

        await model.StartAtTrustAsync(new Uri("https://srv01:5280"), "WARNING: the certificate CHANGED");

        Assert.True(model.OnTrust);
        Assert.True(model.HasWarning);
        Assert.Equal("WARNING: the certificate CHANGED", model.Warning);
        model.ConfirmText = Start;
        model.TrustCommand.Execute(null);
        Assert.Equal(Samples.Fingerprint, _t.Pins.Get(TestServices.Key)!.Fingerprint);
        Assert.Equal(new Uri("https://srv01:5280"), model.Result);   // the token was already stored: connected
        Assert.False(model.HasWarning);
    }

    [Fact]
    public async Task IfTheCertificateCannotBeReadTheTrustStepSaysWhy()
    {
        _t.ProbeFails = new IOException("connection reset");
        var model = Model();

        await model.StartAtTrustAsync(new Uri("https://srv01:5280"), string.Empty);

        Assert.Equal("Cannot reach the server: connection reset", model.Message);
        Assert.False(model.OnTrust);
    }

    [Fact]
    public void LoggingInAgainStartsAtTheLoginStepWithItsReason()
    {
        var model = Model();

        model.StartAtLogin(new Uri("https://srv01:5280"), "The server did not accept the stored token.");

        Assert.True(model.OnLogin);
        Assert.Equal("Log in", model.Heading);
        Assert.Equal("The server did not accept the stored token.", model.Message);
        Assert.Equal("https://srv01:5280", model.Address);
    }

    [Fact]
    public void CancellingClosesWithNoServer()
    {
        var model = Model();
        var closed = 0;
        model.Closed += () => closed++;

        model.CancelCommand.Execute(null);

        Assert.Equal(1, closed);
        Assert.Null(model.Result);
    }

    [Fact]
    public async Task AnUnreadableStoreIsShownNotCrashedOn()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kalterm-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var bad = Path.Combine(folder, "known-hosts.json");
        await File.WriteAllTextAsync(bad, "not json", TestContext.Current.CancellationToken);
        try
        {
            var services = new KalTerm.Terminal.Services.ClientServices
            {
                Pins = new PinStore(bad),
                Tokens = _t.Tokens,
                Probe = _t.Services.Probe,
                CreateApi = _t.Services.CreateApi,
                CreateApiWithToken = _t.Services.CreateApiWithToken,
                Time = _t.Time,
                SystemZone = TimeZoneInfo.Utc,
            };
            var model = new ConnectViewModel(services);
            model.Address = "srv01:5280";

            await model.NextCommand.RunAsync();

            Assert.Contains("not a valid list of trusted servers", model.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
