using System.CommandLine;
using System.Globalization;
using KalTerm.Backup;

namespace KalTerm.Admin;

/// <summary>What the admin commands work with; the real ones are built from the environment, tests give fakes.</summary>
/// <param name="Backups">Takes backups.</param>
/// <param name="Restores">Restores and verifies backups.</param>
/// <param name="Target">The backup target.</param>
/// <param name="Credentials">Where the share credential is kept.</param>
/// <param name="Connector">Connects to a network share.</param>
internal sealed record AdminServices(IBackupService Backups, IRestoreService Restores, BackupTarget Target, ShareCredentialStore Credentials, IShareConnector Connector);

/// <summary>The outside world of the admin tool.</summary>
internal sealed class AdminEnvironment
{
    /// <summary>Normal output.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Error output.</summary>
    public required TextWriter Error { get; init; }

    /// <summary>Standard input, for a password given on a pipe.</summary>
    public required TextReader In { get; init; }

    /// <summary>Whether standard input is a person typing.</summary>
    public bool InteractiveInput { get; init; }

    /// <summary>Asks a person for a password without showing it.</summary>
    public Func<string> ReadPassword { get; init; } = () => string.Empty;

    /// <summary>Builds the backup services, or explains why they cannot be built (for example no backup target is configured).</summary>
    public required Func<AdminServices> CreateServices { get; init; }

    /// <summary>Builds what the user, token and certificate commands need; these never need a backup target.</summary>
    public required Func<AccessServices> CreateAccess { get; init; }
}

/// <summary>
/// <c>kalterm-admin</c>: the commands that change things on the server. Unlike <c>kterm</c> (which only reads) this runs on the server itself, with the server's own database access.
/// Output is plain ASCII. Exit codes: 0 fine, 1 failed or the command line was wrong.
/// </summary>
internal static class AdminApp
{
    public const int Ok = 0;
    public const int Failed = 1;

    public static async Task<int> RunAsync(string[] args, AdminEnvironment env, CancellationToken cancellationToken = default)
    {
        var configuration = new InvocationConfiguration { Output = env.Out, Error = env.Error, EnableDefaultExceptionHandler = true };
        return await BuildRoot(env).Parse(args).InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    private static RootCommand BuildRoot(AdminEnvironment env)
    {
        var root = new RootCommand("kalterm-admin: users, tokens, the TLS certificate, backup and restore for a KalTerm server (runs on the server)");
        AdminAccessCommands.Add(root, env);
        var backup = new Command("backup", "Take, list and verify backups");
        var initTarget = new Command("init-target", "Write the marker file in the backup target (run once, with the share present)");
        initTarget.SetAction((_, ct) => Guarded(env, async services =>
        {
            var written = await services.Target.InitAsync(ct).ConfigureAwait(false);
            env.Out.WriteLine(written ? $"marker written in {services.Target.Path}" : $"{services.Target.Path} already has its marker");
            return Ok;
        }, ConnectIfShare));
        backup.Subcommands.Add(initTarget);

        var user = new Option<string>("--user") { Description = "The account on the share, for example nas\\admin", Required = true };
        var passwordStdin = new Option<bool>("--password-stdin") { Description = "Read the password from standard input (one line) instead of asking" };
        var skipTest = new Option<bool>("--no-test") { Description = "Store the credential without trying it" };
        var setCredential = new Command("set-credential", "Store the share credential on this computer (protected for this machine; never printed)");
        setCredential.Options.Add(user);
        setCredential.Options.Add(passwordStdin);
        setCredential.Options.Add(skipTest);
        setCredential.SetAction((parsed, ct) => Guarded(env, services =>
        {
            var account = parsed.GetRequiredValue(user);
            var password = env.InteractiveInput && !parsed.GetValue(passwordStdin) ? ReadPassword(env) : env.In.ReadLine();
            if (string.IsNullOrEmpty(password))
            {
                return Task.FromResult(Fail(env, "no password was given"));
            }

            var credential = new ShareCredential(account, password);
            if (!parsed.GetValue(skipTest))
            {
                if (!services.Target.Path.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    return Task.FromResult(Fail(env, "the backup target is not a network share; nothing to try the credential on (use --no-test to store it anyway)"));
                }

                try
                {
                    services.Connector.Connect(services.Target.Path, credential).Dispose();
                }
                catch (IOException ex)
                {
                    return Task.FromResult(Fail(env, ex.Message + "; the credential was not stored"));
                }
            }

            services.Credentials.Save(credential);
            env.Out.WriteLine($"credential for {account} stored");
            return Task.FromResult(Ok);
        }, null));
        backup.Subcommands.Add(setCredential);

        var full = new Option<bool>("--full") { Description = "Take a full backup" };
        var diff = new Option<bool>("--diff") { Description = "Take a differential backup" };
        var run = new Command("run", "Take a backup now (the schedule decides full or differential unless told)");
        run.Options.Add(full);
        run.Options.Add(diff);
        run.SetAction((parsed, ct) => Guarded(env, async services =>
        {
            if (parsed.GetValue(full) && parsed.GetValue(diff))
            {
                return Fail(env, "use either --full or --diff, not both");
            }

            var result = await services.Backups.RunAsync(parsed.GetValue(full) ? BackupKind.Full : parsed.GetValue(diff) ? BackupKind.Differential : null, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return Fail(env, "backup failed: " + result.Problem);
            }

            env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{result.Kind} backup {result.Name}: {result.SizeBytes / (1024 * 1024)} MB in {(int)result.Elapsed.TotalSeconds} s; {result.Deleted} old backups deleted"));
            return Ok;
        }, null));
        backup.Subcommands.Add(run);

        var list = new Command("list", "List the backups on the target, newest first");
        list.SetAction((_, ct) => Guarded(env, async services =>
        {
            var manifests = await services.Target.ListAsync(ct).ConfigureAwait(false);
            if (manifests.Count == 0)
            {
                env.Out.WriteLine("no backups on the target");
                return Ok;
            }

            env.Out.WriteLine("Started (UTC)         Kind  Size MB  Name");
            foreach (var m in manifests)
            {
                env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{m.StartedAt:yyyy-MM-dd HH:mm:ss}  {(m.Kind == BackupKind.Full ? "full" : "diff")}  {m.SizeBytes / (1024 * 1024),7}  {m.Name}{(m.BaseFull is null ? string.Empty : " (on " + m.BaseFull + ")")}"));
            }

            return Ok;
        }, ConnectIfShare));
        backup.Subcommands.Add(list);

        var fullCheck = new Option<bool>("--full-check") { Description = "A complete DBCC CHECKDB instead of the physical-only check (slow on a big database)" };
        var verify = new Command("verify", "Prove the newest backup: restore it into a scratch database, check it, compare it with its manifest, drop it");
        verify.Options.Add(fullCheck);
        verify.SetAction((parsed, ct) => Guarded(env, async services =>
        {
            var good = await services.Restores.VerifyAsync(!parsed.GetValue(fullCheck), ct).ConfigureAwait(false);
            env.Out.WriteLine(good ? "the newest backup restores cleanly and agrees with its manifest" : "VERIFICATION FAILED; see the log or 'status' for why");
            return good ? Ok : Failed;
        }, null));
        backup.Subcommands.Add(verify);
        root.Subcommands.Add(backup);

        var database = new Option<string>("--database") { Description = "The name of the NEW database to create (it must not exist)", Required = true };
        var named = new Option<string?>("--backup") { Description = "The backup to restore (default: the newest, with the full backup it builds on)" };
        var keep = new Option<bool>("--keep") { Description = "Leave the restored database in place" };
        var restore = new Command("restore", "Restore a backup into a NEW database, check it and compare it with its manifest. The live database is never touched.");
        restore.Options.Add(database);
        restore.Options.Add(named);
        restore.Options.Add(keep);
        restore.Options.Add(fullCheck);
        restore.SetAction((parsed, ct) => Guarded(env, async services =>
        {
            var result = await services.Restores.RestoreAsync(parsed.GetRequiredValue(database), parsed.GetValue(named), parsed.GetValue(keep), !parsed.GetValue(fullCheck), ct).ConfigureAwait(false);
            env.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"restored {string.Join(" + ", result.Backups)} into {result.Database} in {(int)result.Elapsed.TotalSeconds} s; consistency check clean"));
            if (result.Comparison.Matches)
            {
                env.Out.WriteLine("row counts agree with the manifest");
            }
            else
            {
                foreach (var line in result.Comparison.Differences.Take(20))
                {
                    env.Error.WriteLine("DIFFERENCE: " + line);
                }
            }

            env.Out.WriteLine(result.Kept ? $"{result.Database} was kept; switching the server to it is a manual step" : $"{result.Database} was dropped again");
            return result.Comparison.Matches ? Ok : Failed;
        }, ConnectIfShare));
        root.Subcommands.Add(restore);
        return root;
    }

    private static IDisposable? ConnectIfShare(AdminServices services) => services.Target.Path.StartsWith(@"\\", StringComparison.Ordinal) ? Connect(services) : null;

    private static IDisposable? Connect(AdminServices services) =>
        services.Credentials.Load() is { } credential ? services.Connector.Connect(services.Target.Path, credential) : null;

    private static async Task<int> Guarded(AdminEnvironment env, Func<AdminServices, Task<int>> body, Func<AdminServices, IDisposable?>? connect)
    {
        try
        {
            var services = env.CreateServices();
            using var connection = connect?.Invoke(services);
            return await body(services).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or BackupTargetException or InvalidDataException or IOException or ArgumentException or Microsoft.Data.SqlClient.SqlException)
        {
            return Fail(env, ex.Message);
        }
    }

    internal static int Fail(AdminEnvironment env, string message)
    {
        env.Error.WriteLine("error: " + message);
        return Failed;
    }
    private static string ReadPassword(AdminEnvironment env)
    {
        env.Out.Write("Password: ");
        var typed = env.ReadPassword();
        env.Out.WriteLine();
        return typed;
    }
}
