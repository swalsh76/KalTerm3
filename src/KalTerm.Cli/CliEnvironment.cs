using System.Globalization;
using KalTerm.Client;
using Spectre.Console;

namespace KalTerm.Cli;

/// <summary>Everything the commands touch outside themselves, so tests can replace it: the two consoles, how to reach the API, the clock and the environment variables.</summary>
internal sealed class CliEnvironment
{
    /// <summary>The name of the variable that sets the API address when <c>--api</c> is not given.</summary>
    public const string ApiVariable = "KALTERM_API_URL";

    /// <summary>The width used when the output is redirected (for example through PowerShell remoting) and no width is known.</summary>
    public const int RedirectedWidth = 120;

    public required IAnsiConsole Out { get; init; }

    public required IAnsiConsole Error { get; init; }

    public required Func<Uri, IKalTermApi> CreateApi { get; init; }

    public required TimeProvider Time { get; init; }

    public required Func<string, string?> GetVariable { get; init; }

    /// <summary>The real thing: the process's console, the real client, the system clock and the real environment.</summary>
    public static CliEnvironment Real()
    {
        var output = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Out) });
        var error = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
        var columns = Environment.GetEnvironmentVariable("COLUMNS");
        if (int.TryParse(columns, NumberStyles.None, CultureInfo.InvariantCulture, out var width) && width > 0)
        {
            output.Profile.Width = width;
        }
        else if (Console.IsOutputRedirected)
        {
            output.Profile.Width = RedirectedWidth;
        }

        return new CliEnvironment
        {
            Out = output,
            Error = error,
            CreateApi = address => KalTermApiClient.Create(address),
            Time = TimeProvider.System,
            GetVariable = Environment.GetEnvironmentVariable,
        };
    }
}
