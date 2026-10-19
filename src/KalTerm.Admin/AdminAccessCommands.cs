using System.CommandLine;
using System.Globalization;
using System.Net;
using KalTerm.Data.Auth;
using KalTerm.Security;

namespace KalTerm.Admin;

/// <summary>What the user, token and certificate commands work with.</summary>
/// <param name="Users">Users and their tokens.</param>
/// <param name="TlsDirectory">The folder with the server's TLS certificate.</param>
/// <param name="Time">The clock.</param>
internal sealed record AccessServices(IUserStore Users, string TlsDirectory, TimeProvider Time);

/// <summary>
/// The commands that manage who may use the API: <c>user add|list|disable</c>, <c>token create|list|revoke</c> and <c>cert init|show|rotate</c>. A token is shown once, when it is made;
/// only its hash is stored. Changing the certificate needs an API restart, and every client has to trust the new fingerprint again.
/// </summary>
internal static class AdminAccessCommands
{
    private const int DefaultCertificateDays = 730;

    public static void Add(RootCommand root, AdminEnvironment env)
    {
        root.Subcommands.Add(UserCommands(env));
        root.Subcommands.Add(TokenCommands(env));
        root.Subcommands.Add(CertificateCommands(env));
    }

    private static Command UserCommands(AdminEnvironment env)
    {
        var user = new Command("user", "Add, list and disable users");
        var name = new Argument<string>("name") { Description = "The user name: 2 to 32 lower-case letters, digits, dots, dashes or underscores, starting with a letter" };
        var role = new Option<string>("--role") { Description = "read (market data) or admin", DefaultValueFactory = _ => UserRoles.Read };
        var add = new Command("add", "Add a user");
        add.Arguments.Add(name);
        add.Options.Add(role);
        add.SetAction((parsed, ct) => Guarded(env, async services =>
        {
            var id = await services.Users.AddUserAsync(parsed.GetRequiredValue(name), parsed.GetRequiredValue(role), services.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"user {parsed.GetRequiredValue(name)} added (id {id}); make a token with: token create --user {parsed.GetRequiredValue(name)}"));
            return AdminApp.Ok;
        }));
        user.Subcommands.Add(add);

        var list = new Command("list", "List users");
        list.SetAction((_, ct) => Guarded(env, async services =>
        {
            var users = await services.Users.ListUsersAsync(ct).ConfigureAwait(false);
            if (users.Count == 0)
            {
                env.Out.WriteLine("no users yet");
                return AdminApp.Ok;
            }

            env.Out.WriteLine("Name                              Role   Added (UTC)          Disabled");
            foreach (var u in users)
            {
                env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{u.Name,-32}  {u.Role,-5}  {u.CreatedAt:yyyy-MM-dd HH:mm:ss}  {(u.DisabledAt is { } d ? d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-")}"));
            }

            return AdminApp.Ok;
        }));
        user.Subcommands.Add(list);

        var disable = new Command("disable", "Disable a user: all their tokens stop working (within half a minute)");
        disable.Arguments.Add(name);
        disable.SetAction((parsed, ct) => Guarded(env, async services =>
        {
            var done = await services.Users.DisableUserAsync(parsed.GetRequiredValue(name), services.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            return done ? Said(env, $"user {parsed.GetRequiredValue(name)} disabled") : AdminApp.Fail(env, $"there is no enabled user named {parsed.GetRequiredValue(name)}");
        }));
        user.Subcommands.Add(disable);
        return user;
    }

    private static Command TokenCommands(AdminEnvironment env)
    {
        var token = new Command("token", "Make, list and revoke API tokens");
        var owner = new Option<string>("--user") { Description = "The user the token is for", Required = true };
        var label = new Option<string?>("--label") { Description = "What it is for, for example the computer" };
        var days = new Option<int?>("--expires-days") { Description = "Stop working after this many days (default: never)" };
        var create = new Command("create", "Make a token. It is shown once; only its hash is stored.");
        create.Options.Add(owner);
        create.Options.Add(label);
        create.Options.Add(days);
        create.SetAction((parsed, ct) => Guarded(env, async services =>
        {
            var now = services.Time.GetUtcNow().UtcDateTime;
            if (parsed.GetValue(days) is <= 0)
            {
                return AdminApp.Fail(env, "--expires-days must be positive");
            }

            var (id, text) = await services.Users.CreateTokenAsync(parsed.GetRequiredValue(owner), parsed.GetValue(label), parsed.GetValue(days) is { } d ? now.AddDays(d) : null, now, ct).ConfigureAwait(false);
            env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"token {id} for {parsed.GetRequiredValue(owner)}:"));
            env.Out.WriteLine(text);
            env.Out.WriteLine("This is the only time the token is shown. Keep it safe; 'kterm login' asks for it. If it is lost, make another and revoke this one.");
            return AdminApp.Ok;
        }));
        token.Subcommands.Add(create);

        var filter = new Option<string?>("--user") { Description = "Only this user's tokens" };
        var list = new Command("list", "List tokens (never the tokens themselves)");
        list.Options.Add(filter);
        list.SetAction((parsed, ct) => Guarded(env, async services =>
        {
            var tokens = await services.Users.ListTokensAsync(parsed.GetValue(filter), ct).ConfigureAwait(false);
            if (tokens.Count == 0)
            {
                env.Out.WriteLine("no tokens");
                return AdminApp.Ok;
            }

            env.Out.WriteLine("Id   User                              Label                 Made (UTC)           Last used            Expires              Revoked");
            foreach (var t in tokens)
            {
                env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{t.Id,-4} {t.UserName,-32}  {Cut(t.Label, 20),-20}  {Moment(t.CreatedAt)}  {Moment(t.LastUsedAt)}  {Moment(t.ExpiresAt)}  {Moment(t.RevokedAt)}"));
            }

            return AdminApp.Ok;
        }));
        token.Subcommands.Add(list);

        var id = new Argument<int>("id") { Description = "The token id from 'token list'" };
        var revoke = new Command("revoke", "Revoke a token (it stops working within half a minute)");
        revoke.Arguments.Add(id);
        revoke.SetAction((parsed, ct) => Guarded(env, async services =>
        {
            var done = await services.Users.RevokeTokenAsync(parsed.GetRequiredValue(id), services.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            return done ? Said(env, $"token {parsed.GetRequiredValue(id)} revoked") : AdminApp.Fail(env, $"there is no active token {parsed.GetRequiredValue(id)}");
        }));
        token.Subcommands.Add(revoke);
        return token;
    }

    private static Command CertificateCommands(AdminEnvironment env)
    {
        var cert = new Command("cert", "Make, show and rotate the server's TLS certificate");
        var hosts = new Option<string[]>("--host") { Description = "A host name the certificate is valid for (repeat for several)", Required = true, AllowMultipleArgumentsPerToken = true };
        var addresses = new Option<string[]>("--ip") { Description = "An IP address the certificate is valid for (repeat for several); 127.0.0.1 is always included", DefaultValueFactory = _ => [], AllowMultipleArgumentsPerToken = true };
        var validity = new Option<int>("--days") { Description = "How long it is valid", DefaultValueFactory = _ => DefaultCertificateDays };
        var init = new Command("init", "Make the server's self-signed certificate (refuses if there is one; use 'rotate' to replace it)");
        init.Options.Add(hosts);
        init.Options.Add(addresses);
        init.Options.Add(validity);
        init.SetAction((parsed, _) => Guarded(env, services =>
        {
            if (ServerCertificate.Exists(services.TlsDirectory))
            {
                return Task.FromResult(AdminApp.Fail(env, $"there is already a certificate in {services.TlsDirectory}; 'cert rotate' replaces it"));
            }

            if (!TryAddresses(parsed.GetValue(addresses) ?? [], out var ips, out var bad))
            {
                return Task.FromResult(AdminApp.Fail(env, $"'{bad}' is not an IP address"));
            }

            return Task.FromResult(Issue(env, services, [.. parsed.GetValue(hosts)!], [.. ips, IPAddress.Loopback], parsed.GetValue(validity)));
        }));
        cert.Subcommands.Add(init);

        var show = new Command("show", "Show the certificate: names, validity and the fingerprint clients must trust");
        show.SetAction((_, _) => Guarded(env, services =>
        {
            using var certificate = ServerCertificate.Load(services.TlsDirectory);
            Describe(env, CertificateInfo.Of(certificate));
            return Task.FromResult(AdminApp.Ok);
        }));
        cert.Subcommands.Add(show);

        var rotate = new Command("rotate", "Replace the certificate with a new one for the same names. Restart the API afterwards; every client must trust the new fingerprint again.");
        rotate.Options.Add(validity);
        rotate.SetAction((parsed, cancellationToken) => Guarded(env, services =>
        {
            using var current = ServerCertificate.Load(services.TlsDirectory);
            var info = CertificateInfo.Of(current);
            var names = info.Names.Where(n => !IPAddress.TryParse(n, out _)).ToList();
            var ips = info.Names.Select(n => IPAddress.TryParse(n, out var ip) ? ip : null).OfType<IPAddress>().ToList();
            env.Out.WriteLine("old fingerprint: " + CertificateFingerprint.Format(info.Fingerprint));
            return Task.FromResult(Issue(env, services, names, ips, parsed.GetValue(validity)));
        }));
        cert.Subcommands.Add(rotate);
        return cert;
    }

    private static int Issue(AdminEnvironment env, AccessServices services, IEnumerable<string> names, IEnumerable<IPAddress> ips, int days)
    {
        if (days <= 0)
        {
            return AdminApp.Fail(env, "--days must be positive");
        }

        using var certificate = ServerCertificate.Create(names, ips, TimeSpan.FromDays(days), services.Time.GetUtcNow());
        ServerCertificate.Save(services.TlsDirectory, certificate);
        Describe(env, CertificateInfo.Of(certificate));
        env.Out.WriteLine("Restart the API to use it. Clients trust it once with: kterm trust <host>:<port> (compare the fingerprint above).");
        return AdminApp.Ok;
    }

    private static void Describe(AdminEnvironment env, CertificateInfo info)
    {
        env.Out.WriteLine("subject:     " + info.Subject);
        env.Out.WriteLine("names:       " + string.Join(", ", info.Names));
        env.Out.WriteLine("validity:    " + ServerCertificate.Validity(info));
        env.Out.WriteLine("fingerprint: " + CertificateFingerprint.Format(info.Fingerprint));
    }

    private static bool TryAddresses(IEnumerable<string> texts, out List<IPAddress> addresses, out string bad)
    {
        addresses = [];
        bad = string.Empty;
        foreach (var text in texts)
        {
            if (!IPAddress.TryParse(text, out var address))
            {
                bad = text;
                return false;
            }

            addresses.Add(address);
        }

        return true;
    }

    private static int Said(AdminEnvironment env, string text)
    {
        env.Out.WriteLine(text);
        return AdminApp.Ok;
    }

    private static string Moment(DateTime? value) => value is { } moment ? moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-                  ";

    private static string Cut(string? text, int length) => text is null ? "-" : text.Length <= length ? text : text[..length];

    private static async Task<int> Guarded(AdminEnvironment env, Func<AccessServices, Task<int>> body)
    {
        try
        {
            return await body(env.CreateAccess()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or ArgumentException or Microsoft.Data.SqlClient.SqlException)
        {
            return AdminApp.Fail(env, ex.Message);
        }
    }
}
