using KalTerm.Client;

namespace KalTerm.Terminal.Services;

/// <summary>Everything the terminal touches outside itself, so tests can replace it: the trusted certificates, the tokens, how to reach a server, the clock.</summary>
internal sealed class ClientServices
{
    /// <summary>The servers whose certificates this user trusts (shared with <c>kterm</c>).</summary>
    public required PinStore Pins { get; init; }

    /// <summary>The tokens of this user (shared with <c>kterm</c>).</summary>
    public required TokenStore Tokens { get; init; }

    /// <summary>Reads a server's certificate, for the user to confirm.</summary>
    public required Func<string, int, CancellationToken, Task<ServerIdentity>> Probe { get; init; }

    /// <summary>Makes a client that sends the stored token for the address.</summary>
    public required Func<Uri, IKalTermApi> CreateApi { get; init; }

    /// <summary>Makes a client that sends a given token (to try one before it is stored).</summary>
    public required Func<Uri, string, IKalTermApi> CreateApiWithToken { get; init; }

    public required TimeProvider Time { get; init; }

    /// <summary>The computer's own time zone: where times are shown when nothing else is chosen.</summary>
    public required TimeZoneInfo SystemZone { get; init; }

    /// <summary>The real thing: the profile's stores, the pinned transport, the system clock.</summary>
    public static ClientServices Real()
    {
        var pins = new PinStore(PinStore.DefaultPath());
        var tokens = new TokenStore(TokenStore.DefaultPath());
        return new ClientServices
        {
            Pins = pins,
            Tokens = tokens,
            Probe = TrustProbe.FetchAsync,
            CreateApi = address => PinnedTransport.CreateClient(address, pins, tokens.Get(EndpointKey.Of(address))),
            CreateApiWithToken = (address, token) => PinnedTransport.CreateClient(address, pins, token),
            Time = TimeProvider.System,
            SystemZone = TimeZoneInfo.Local,
        };
    }
}
