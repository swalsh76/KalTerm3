using KalTerm.Admin;

// kalterm-admin: backup and restore on the server. Everything of substance is in KalTerm.Backup and AdminApp (tested there); this file only wires the console to it.
// It reads the same settings as the ingest host: KALTERM_Ingest__Server, __Database, __BackupTarget, __BackupStaging, __BackupCredentialPath and the retention settings.
return await AdminApp.RunAsync(
    args,
    new AdminEnvironment
    {
        Out = Console.Out,
        Error = Console.Error,
        In = Console.In,
        InteractiveInput = !Console.IsInputRedirected,
        ReadPassword = ReadHidden,
        CreateServices = () => AdminComposition.Create(name => Environment.GetEnvironmentVariable("KALTERM_Ingest__" + name)),
    });

static string ReadHidden()
{
    var typed = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            if (typed.Length > 0)
            {
                typed.Length--;
            }
        }
        else
        {
            typed.Append(key.KeyChar);
        }
    }

    return typed.ToString();
}
