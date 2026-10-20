using System.Globalization;
using KalTerm.Client;
using KalTerm.Kalshi.Configuration;
using KalTerm.Trading.Account;
using KalTerm.Trading.Keys;
using KalTerm.Trading.Session;
using Spectre.Console;

namespace KalTerm.Cli;

/// <summary>Everything the commands touch outside themselves, so tests can replace it: the two consoles, how to reach the API, the clock and the environment variables.</summary>
internal sealed class CliEnvironment
{
    /// <summary>The name of the variable that sets the API address when <c>--api</c> is not given.</summary>
    public const string ApiVariable = "KALTERM_API_URL";

    /// <summary>The name of the variable that supplies the API token when none is stored (for scripts).</summary>
    public const string TokenVariable = "KALTERM_API_TOKEN";

    /// <summary>The name of the variable that sets the time zone when neither <c>--utc</c> nor <c>--tz</c> is given.</summary>
    public const string TimeZoneVariable = "KALTERM_TZ";

    /// <summary>The width used when the output is redirected (for example through PowerShell remoting) and no width is known.</summary>
    public const int RedirectedWidth = 120;

    public required IAnsiConsole Out { get; init; }

    public required IAnsiConsole Error { get; init; }

    public required Func<Uri, IKalTermApi> CreateApi { get; init; }

    /// <summary>Makes a client that sends a given token (for <c>login</c>, to try a token before it is stored).</summary>
    public required Func<Uri, string, IKalTermApi> CreateApiWithToken { get; init; }

    /// <summary>The servers whose certificates this user trusts.</summary>
    public required PinStore Pins { get; init; }

    /// <summary>The tokens of this user.</summary>
    public required TokenStore Tokens { get; init; }

    /// <summary>Reads a server's certificate (for <c>trust</c>).</summary>
    public required Func<string, int, CancellationToken, Task<ServerIdentity>> Probe { get; init; }

    /// <summary>Whether standard input is a person at a keyboard.</summary>
    public bool InteractiveInput { get; init; }

    /// <summary>Reads a line of standard input.</summary>
    public Func<string?> ReadLine { get; init; } = () => null;

    /// <summary>Reads a secret without showing it.</summary>
    public Func<string> ReadSecret { get; init; } = () => string.Empty;

    public required TimeProvider Time { get; init; }

    /// <summary>Whether a token is known for a server, so the caller's stored settings can be asked for without a pointless refused request.</summary>
    public Func<Uri, bool> HasToken { get; init; } = _ => false;

    /// <summary>The computer's own time zone: where times are shown when nothing else is chosen.</summary>
    public TimeZoneInfo SystemZone { get; init; } = TimeZoneInfo.Utc;

    public required Func<string, string?> GetVariable { get; init; }

    /// <summary>Opens what a trading command needs. Trading talks to Kalshi directly and never to the KalTerm server.</summary>
    public Func<TradingMode, CancellationToken, Task<TradingSession>> OpenTrading { get; init; } = (_, _) => throw new InvalidOperationException("trading is not available here");

    /// <summary>Where the trading keys are kept (opened only when a command needs them).</summary>
    public Func<ITradingKeyVault> OpenVault { get; init; } = () => new CredentialManagerKeyVault();

    /// <summary>Reads the trading state file (for <c>resume</c>).</summary>
    public Func<TradingState> LoadTradingState { get; init; } = () => TradingState.Load();

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

        var pins = new PinStore(PinStore.DefaultPath());
        var tokens = new TokenStore(TokenStore.DefaultPath());
        return new CliEnvironment
        {
            Out = output,
            Error = error,
            CreateApi = address => PinnedTransport.CreateClient(address, pins, Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment : tokens.Get(EndpointKey.Of(address))),
            CreateApiWithToken = (address, token) => PinnedTransport.CreateClient(address, pins, token),
            Pins = pins,
            Tokens = tokens,
            HasToken = address => Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } || tokens.Get(EndpointKey.Of(address)) is not null,
            Probe = TrustProbe.FetchAsync,
            InteractiveInput = !Console.IsInputRedirected,
            ReadLine = Console.In.ReadLine,
            ReadSecret = ReadHidden,
            Time = TimeProvider.System,
            SystemZone = TimeZoneInfo.Local,
            GetVariable = Environment.GetEnvironmentVariable,
            OpenTrading = (mode, ct) => TradingSessions.OpenAsync(mode, new TradingSessionOptions { Settings = KalshiSettings.FromEnvironment() }, ct),
        };
    }

    private static string ReadHidden()
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
}
