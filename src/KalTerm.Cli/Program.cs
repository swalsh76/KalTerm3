using KalTerm.Cli;

// kterm: a read-only view of a KalTerm server. Everything of substance is in CliApp and Views (tested there).
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

return await CliApp.RunAsync(args, CliEnvironment.Real(), cancel.Token);
