using System.Globalization;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Security;
using KalTerm.Terminal.Mvvm;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Connect;

/// <summary>Where the connect dialog is.</summary>
internal enum ConnectStep
{
    /// <summary>Asking for the server's address.</summary>
    Address,

    /// <summary>Showing the server's certificate to be confirmed.</summary>
    Trust,

    /// <summary>Asking for the token.</summary>
    Login,
}

/// <summary>
/// Connecting to a server, as <c>kterm trust</c> and <c>kterm login</c> do it, in one dialog. The certificate is only trusted after the person types the first characters of its fingerprint, which they compare with the one
/// the server's operator gave them (<c>kalterm-admin cert show</c>); the token is only stored after the server accepted it. The pins and tokens are the same files <c>kterm</c> uses.
/// </summary>
internal sealed class ConnectViewModel : ObservableObject
{
    /// <summary>How many characters of the fingerprint the person types to confirm it.</summary>
    public const int ConfirmCharacters = 8;

    private readonly ClientServices _services;
    private ConnectStep _step = ConnectStep.Address;
    private string _address = string.Empty;
    private string _message = string.Empty;
    private string _warning = string.Empty;
    private string _confirmText = string.Empty;
    private string _token = string.Empty;
    private CertificateInfo? _certificate;
    private Uri? _server;

    public ConnectViewModel(ClientServices services, Uri? server = null)
    {
        _services = services;
        _address = server is null ? string.Empty : server.GetLeftPart(UriPartial.Authority);
        NextCommand = new AsyncCommand(NextAsync, Fail, () => _address.Trim().Length > 0);
        TrustCommand = new RelayCommand(Trust, () => Confirmed);
        LoginCommand = new AsyncCommand(LoginAsync, Fail, () => _token.Trim().Length > 0);
        CancelCommand = new RelayCommand(() => Closed?.Invoke());
    }

    /// <summary>The dialog is done (connected or cancelled); the window closes.</summary>
    public event Action? Closed;

    public AsyncCommand NextCommand { get; }

    public RelayCommand TrustCommand { get; }

    public AsyncCommand LoginCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>The server that was connected; <see langword="null"/> when the person cancelled.</summary>
    public Uri? Result { get; private set; }

    public ConnectStep Step
    {
        get => _step;
        private set
        {
            if (Set(ref _step, value))
            {
                Raise(nameof(OnAddress));
                Raise(nameof(OnTrust));
                Raise(nameof(OnLogin));
                Raise(nameof(Heading));
            }
        }
    }

    public bool OnAddress => _step == ConnectStep.Address;

    public bool OnTrust => _step == ConnectStep.Trust;

    public bool OnLogin => _step == ConnectStep.Login;

    public string Heading => _step switch
    {
        ConnectStep.Address => "Connect to a KalTerm server",
        ConnectStep.Trust => "Trust this server's certificate",
        _ => "Log in",
    };

    public string Address
    {
        get => _address;
        set
        {
            if (Set(ref _address, value))
            {
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What went wrong, or what to do next.</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
            {
                Raise(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message.Length > 0;

    /// <summary>A warning shown on the trust step, for example that the certificate changed.</summary>
    public string Warning
    {
        get => _warning;
        private set
        {
            if (Set(ref _warning, value))
            {
                Raise(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => _warning.Length > 0;

    public string ServerName => _server is null ? string.Empty : EndpointKey.Of(_server);

    public string Fingerprint => _certificate is null ? string.Empty : CertificateFingerprint.Format(_certificate.Fingerprint);

    public string Subject => _certificate?.Subject ?? string.Empty;

    public string Validity => _certificate is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"valid {_certificate.NotBefore:yyyy-MM-dd} to {_certificate.NotAfter:yyyy-MM-dd}");

    public string Names => _certificate is null ? string.Empty : string.Join(", ", _certificate.Names);

    /// <summary>The first characters of the fingerprint the person has to type.</summary>
    public string ConfirmHint => _certificate is null ? string.Empty : $"Type the first {ConfirmCharacters} characters of the fingerprint ({_certificate.Fingerprint[..ConfirmCharacters]}) to trust it.";

    public string ConfirmText
    {
        get => _confirmText;
        set
        {
            if (Set(ref _confirmText, value))
            {
                Raise(nameof(Confirmed));
                TrustCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool Confirmed => _certificate is not null && string.Equals(_confirmText.Trim(), _certificate.Fingerprint[..ConfirmCharacters], StringComparison.OrdinalIgnoreCase);

    /// <summary>The token being typed. It is held only until it is tried and stored.</summary>
    public string Token
    {
        get => _token;
        set
        {
            if (Set(ref _token, value))
            {
                LoginCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Starts at the trust step for a certificate that is not trusted yet or has changed.</summary>
    /// <param name="server">The server.</param>
    /// <param name="warning">What to warn about; empty for a first connection.</param>
    public async Task StartAtTrustAsync(Uri server, string warning)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
        Raise(nameof(ServerName));
        Address = server.GetLeftPart(UriPartial.Authority);
        Warning = warning;
        try
        {
            await ReadCertificateAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex);
        }
    }

    /// <summary>Starts at the login step for a server whose certificate is already trusted.</summary>
    public void StartAtLogin(Uri server, string message)
    {
        ArgumentNullException.ThrowIfNull(server);
        _server = server;
        Raise(nameof(ServerName));
        Address = server.GetLeftPart(UriPartial.Authority);
        Message = message;
        Step = ConnectStep.Login;
    }

    private async Task NextAsync()
    {
        Message = string.Empty;
        if (!TryParseAddress(_address, out var server))
        {
            Message = "Give the server as host:port, for example kaldb:5280 (or an https:// address; a loopback http:// address is also accepted).";
            return;
        }

        _server = server;
        Raise(nameof(ServerName));
        var key = EndpointKey.Of(server);
        if (server.Scheme == Uri.UriSchemeHttps && _services.Pins.Get(key) is null)
        {
            await ReadCertificateAsync().ConfigureAwait(true);
            return;
        }

        if (_services.Tokens.Get(key) is null)
        {
            Step = ConnectStep.Login;
            return;
        }

        Finish(server);
    }

    private async Task ReadCertificateAsync()
    {
        var server = _server!;
        Message = string.Empty;
        var identity = await _services.Probe(server.Host, server.Port, CancellationToken.None).ConfigureAwait(true);
        _certificate = identity.Info;
        ConfirmText = string.Empty;
        foreach (var name in new[] { nameof(Fingerprint), nameof(Subject), nameof(Validity), nameof(Names), nameof(ConfirmHint) })
        {
            Raise(name);
        }

        Step = ConnectStep.Trust;
    }

    private void Trust()
    {
        if (!Confirmed || _server is null || _certificate is null)
        {
            return;
        }

        _services.Pins.Set(EndpointKey.Of(_server), _certificate.Fingerprint, _services.Time.GetUtcNow().UtcDateTime);
        Warning = string.Empty;
        Message = string.Empty;
        if (_services.Tokens.Get(EndpointKey.Of(_server)) is null)
        {
            Step = ConnectStep.Login;
        }
        else
        {
            Finish(_server);
        }
    }

    private async Task LoginAsync()
    {
        var token = _token.Trim();
        if (!token.StartsWith("kt_", StringComparison.Ordinal) || token.Length != 46)
        {
            Message = "That does not look like a token (they start with kt_ and are 46 characters). Ask the server's operator for one: kalterm-admin token create.";
            return;
        }

        var server = _server!;
        var client = _services.CreateApiWithToken(server, token);
        try
        {
            _ = await client.GetMarketsAsync(new MarketsQuery(Limit: 1)).ConfigureAwait(true);
        }
        catch (ApiException ex) when (ex.Code == "unauthorized")
        {
            Message = $"{EndpointKey.Of(server)} did not accept the token; nothing was stored.";
            return;
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }

        _services.Tokens.Set(EndpointKey.Of(server), token);
        Token = string.Empty;
        Finish(server);
    }

    private void Finish(Uri server)
    {
        Result = server;
        Closed?.Invoke();
    }

    private void Fail(Exception ex) => Message = ex switch
    {
        TlsTrustException or ApiUnavailableException or ApiException or InvalidDataException => ex.Message,
        HttpRequestException or IOException or System.Net.Sockets.SocketException or TimeoutException => $"Cannot reach the server: {ex.Message}",
        _ => ex.Message,
    };

    private static bool TryParseAddress(string text, out Uri server)
    {
        var trimmed = text.Trim();
        if (EndpointKey.TryParse(trimmed, out server))
        {
            return true;
        }

        // a loopback http address is plain; it has no certificate to trust
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttp && KalTermApiClient.IsSafeForToken(parsed) && (server = new Uri(parsed.GetLeftPart(UriPartial.Authority))) is not null;
    }
}
