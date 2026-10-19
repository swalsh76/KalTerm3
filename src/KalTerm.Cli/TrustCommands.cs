using KalTerm.Client;
using KalTerm.Security;
using Spectre.Console;
using static KalTerm.Cli.Display;

namespace KalTerm.Cli;

/// <summary>
/// <c>trust</c>, <c>login</c> and <c>logout</c>: how this computer comes to talk to a server. <c>trust</c> reads the server's certificate and, only when the person confirms its fingerprint
/// (by typing the start of it, or by giving it with <c>--fingerprint</c>), remembers it; from then on only that exact certificate is accepted. <c>login</c> stores the person's token.
/// </summary>
internal static class TrustCommands
{
    /// <summary>How much of the fingerprint a person types to confirm it: enough that pressing Enter without reading cannot be mistaken for a confirmation.</summary>
    public const int ConfirmCharacters = 8;

    public static async Task<int> TrustAsync(CliEnvironment env, Uri address, string? givenFingerprint, CancellationToken cancellationToken)
    {
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            return CliApp.Fail(env, $"{address} is not an https address; only https servers have a certificate to trust");
        }

        string? expected = null;
        if (givenFingerprint is not null && !CertificateFingerprint.TryNormalize(givenFingerprint, out expected))
        {
            return CliApp.Fail(env, "--fingerprint must be the 64 hex digits of the certificate's SHA-256 fingerprint (colons and spaces are fine)");
        }

        var identity = await env.Probe(address.Host, address.Port, cancellationToken).ConfigureAwait(false);
        var key = EndpointKey.Of(address);
        var info = identity.Info;
        var shown = CertificateFingerprint.Format(info.Fingerprint);
        env.Out.WriteLine($"{key} presents this certificate:");
        env.Out.WriteLine($"  subject:     {info.Subject}");
        env.Out.WriteLine($"  names:       {string.Join(", ", info.Names)}");
        env.Out.WriteLine($"  validity:    {ServerCertificate.Validity(info)}");
        env.Out.WriteLine($"  fingerprint: {shown}");

        var existing = env.Pins.Get(key);
        if (existing is not null && existing.Fingerprint == info.Fingerprint)
        {
            env.Out.WriteLine("This certificate is already trusted.");
            return CliApp.Ok;
        }

        if (existing is not null)
        {
            env.Out.WriteLine($"WARNING: you trusted a DIFFERENT certificate for {key} before ({CertificateFingerprint.Format(existing.Fingerprint)}). Only replace it if the server's certificate was rotated on purpose.");
        }

        if (expected is not null)
        {
            if (expected != info.Fingerprint)
            {
                return CliApp.Fail(env, $"the server's fingerprint is not the one you gave ({CertificateFingerprint.Format(expected)}); nothing was trusted. Something else may be answering.");
            }
        }
        else if (!env.InteractiveInput)
        {
            return CliApp.Fail(env, "cannot ask for confirmation here; compare the fingerprint above with the one the server's operator gave you, then run again with --fingerprint <value>");
        }
        else
        {
            var start = info.Fingerprint[..ConfirmCharacters];
            env.Out.WriteLine($"Compare the fingerprint with the one the server's operator gave you. To trust it, type its first {ConfirmCharacters} characters ({start}); anything else cancels.");
            if (!string.Equals(env.ReadLine()?.Trim(), start, StringComparison.OrdinalIgnoreCase))
            {
                return CliApp.Fail(env, "not confirmed; nothing was trusted");
            }
        }

        env.Pins.Set(key, info.Fingerprint, env.Time.GetUtcNow().UtcDateTime);
        env.Out.WriteLine($"{key} is trusted. Next: kterm login --api {address.GetLeftPart(UriPartial.Authority)}");
        return CliApp.Ok;
    }

    public static async Task<int> LoginAsync(CliEnvironment env, Uri address, bool tokenFromInput, CancellationToken cancellationToken)
    {
        string? token;
        if (env.InteractiveInput && !tokenFromInput)
        {
            env.Out.Write("Token: ");
            token = env.ReadSecret();
            env.Out.WriteLine(string.Empty);
        }
        else
        {
            token = env.ReadLine();
        }

        token = token?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return CliApp.Fail(env, "no token was given; ask the server's operator for one (kalterm-admin token create)");
        }

        if (!token.StartsWith("kt_", StringComparison.Ordinal) || token.Length != 46)
        {
            return CliApp.Fail(env, "that does not look like a token (they start with kt_ and are 46 characters); nothing was stored");
        }

        var key = EndpointKey.Of(address);
        var client = env.CreateApiWithToken(address, token);
        try
        {
            _ = await client.GetMarketsAsync(new MarketsQuery(Limit: 1), cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.Code == "unauthorized")
        {
            return CliApp.Fail(env, $"{key} did not accept the token; nothing was stored");
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }

        env.Tokens.Set(key, token);
        env.Out.WriteLine($"logged in to {key}; the token is stored for this Windows user only");
        return CliApp.Ok;
    }

    public static int Logout(CliEnvironment env, Uri address)
    {
        var key = EndpointKey.Of(address);
        env.Out.WriteLine(env.Tokens.Remove(key) ? $"the stored token for {key} was removed" : $"there was no stored token for {key}");
        return CliApp.Ok;
    }
}
