using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KalTerm.Client;
using KalTerm.Contracts;
using KalTerm.Security;
using KalTerm.Terminal.Connect;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal.Tests;

/// <summary>Runs code on a thread of its own with a single-threaded apartment, the way WPF needs; whatever it throws comes back to the test.</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return failure is null ? result : throw new InvalidOperationException("the STA work failed: " + failure.Message, failure);
    }

    public static void Run(Action work) => Run(() =>
    {
        work();
        return 0;
    });
}

/// <summary>Collects WPF's binding complaints (a wrong property name is otherwise silent), so a test can insist there are none.</summary>
internal sealed class BindingErrors : TraceListener
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages => _messages;

    public static BindingErrors Listen()
    {
        var listener = new BindingErrors();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
        return listener;
    }

    public override void Write(string? message) => _messages.Add(message ?? string.Empty);

    public override void WriteLine(string? message) => _messages.Add(message ?? string.Empty);

    protected override void Dispose(bool disposing)
    {
        PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
        base.Dispose(disposing);
    }
}

internal static class VisualTreeKit
{
    /// <summary>Forces templates, bindings and layout to happen without showing the element.</summary>
    public static void Realize(FrameworkElement element)
    {
        var target = Root(element);
        target.Measure(new Size(1000, 800));
        target.Arrange(new Rect(0, 0, 1000, 800));
        target.UpdateLayout();
    }

    /// <summary>A window that is not shown has no visual tree of its own; its content is what gets laid out.</summary>
    private static FrameworkElement Root(FrameworkElement element) => element is Window { Content: FrameworkElement content } ? content : element;

    public static IEnumerable<T> All<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is Window { Content: DependencyObject content })
        {
            root = content;
            if (content is T self)
            {
                yield return self;
            }
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in All<T>(child))
            {
                yield return inner;
            }
        }
    }

    private static bool Shown(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    public static IEnumerable<string> Texts(DependencyObject root) => All<TextBlock>(root).Where(Shown).Select(t => t.Text);
}

/// <summary>An API that answers with what a test gave it. Only the calls the terminal makes so far are real; the rest refuse.</summary>
internal sealed class FakeApi : IKalTermApi, IDisposable
{
    public StatusDto Status { get; set; } = Samples.Status();

    public Exception? StatusFails { get; set; }

    public Exception? MarketsFail { get; set; }

    public string? StoredZone { get; set; }

    public Exception? ZoneFails { get; set; }

    public List<string?> ZonesStored { get; } = [];

    public int StatusCalls { get; private set; }

    public bool Disposed { get; private set; }

    public Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        StatusCalls++;
        return StatusFails is null ? Task.FromResult(Status) : Task.FromException<StatusDto>(StatusFails);
    }

    public Task<MarketPage> GetMarketsAsync(MarketsQuery query, CancellationToken cancellationToken = default) =>
        MarketsFail is null ? Task.FromResult(new MarketPage([], null)) : Task.FromException<MarketPage>(MarketsFail);

    public Task<SettingDto> GetTimeZoneSettingAsync(CancellationToken cancellationToken = default) =>
        ZoneFails is null ? Task.FromResult(new SettingDto("time_zone", StoredZone)) : Task.FromException<SettingDto>(ZoneFails);

    public Task SetTimeZoneSettingAsync(string? zone, CancellationToken cancellationToken = default)
    {
        ZonesStored.Add(zone);
        return ZoneFails is null ? Task.CompletedTask : Task.FromException(ZoneFails);
    }

    public void Dispose() => Disposed = true;

    public Task<MarketDto> GetMarketAsync(string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ScreenerPage> GetScreenerAsync(ScreenQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<WatchlistListDto> GetWatchlistsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<WatchlistDto> GetWatchlistAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<WatchlistDto> CreateWatchlistAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteWatchlistAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task AddToWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RemoveFromWatchlistAsync(string name, string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlertListDto> GetAlertsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlertDto> CreateAlertAsync(CreateAlertRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SetAlertPausedAsync(int id, bool paused, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeleteAlertAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlertFiresPage> GetAlertFiresAsync(long? after = null, int? limit = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IAsyncEnumerable<AlertStreamEvent> StreamAlertFiresAsync(long? after, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IAsyncEnumerable<MarketStreamEvent> StreamMarketsAsync(MarketStreamRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<MetricsDto> GetMetricsAsync(string ticker, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<TickPage> GetTicksAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CandlePage> GetCandlesAsync(string ticker, string interval, WindowQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<TradePage> GetTradesAsync(string ticker, WindowQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<OrderBookPage> GetOrderBooksAsync(int? depth = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<OrderBookDto> GetOrderBookAsync(string ticker, int? depth = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<GapPage> GetGapsAsync(WindowQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal static class Samples
{
    public static readonly DateTimeOffset Now = new(2026, 10, 18, 23, 0, 0, TimeSpan.Zero);

    public const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    public static StatusDto Status(params string[] problems) =>
        new(
            "2026-10-18T23:00:00.000000+00:00",
            problems,
            new IngestStateDto("running", "2026-10-18T22:59:57.000000+00:00", 3),
            new StreamsDto(new StreamDto("2026-10-18T22:59:59.000000+00:00", 1), new StreamDto("2026-10-18T22:59:58.000000+00:00", 2)),
            new StorageDto(276_824_064, 107_374_182_400, 988_000_000_000, 264_000_000, 0, "2026-10-17", "ok used=0% dropped=", 40),
            new Dictionary<string, long> { ["active"] = 120018, ["closed"] = 288, ["unlisted"] = 428 },
            new GapSummaryDto(24, 2, new Dictionary<string, long> { ["ingest_down"] = 2 }, "Ticker updates in a gap are lost."),
            new BooksStatusDto(2, 0, 0, 3));

    public static ServerIdentity Identity(string fingerprint = Fingerprint) =>
        new(new CertificateInfo("CN=kaldb, O=KalTerm", fingerprint, new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc), new DateTime(2028, 10, 18, 0, 0, 0, DateTimeKind.Utc), ["kaldb", "192.168.1.20"]));

    public const string Token = "kt_0123456789abcdef0123456789abcdef0123456789a";

    public static ApiException Unauthorized() => new(HttpStatusCode.Unauthorized, "unauthorized");
}

/// <summary>The terminal's services built from fakes and stores in a folder of the test's own.</summary>
internal sealed class TestServices : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kalterm-terminal-test-" + Guid.NewGuid().ToString("N"));

    public TestServices()
    {
        Pins = new PinStore(Path.Combine(_folder, "known-hosts.json"));
        Tokens = new TokenStore(Path.Combine(_folder, "credentials.bin"));
        Settings = new TerminalSettingsStore(Path.Combine(_folder, "terminal.json"));
        Services = new ClientServices
        {
            Pins = Pins,
            Tokens = Tokens,
            Probe = (host, port, _) =>
            {
                Probed.Add((host, port));
                return ProbeFails is null ? Task.FromResult(Identity) : Task.FromException<ServerIdentity>(ProbeFails);
            },
            CreateApi = address =>
            {
                Created.Add(address);
                return CreateApiFails is null ? Api : throw CreateApiFails;
            },
            CreateApiWithToken = (address, token) =>
            {
                TokenTries.Add((address, token));
                return TokenApi;
            },
            Time = Time,
            SystemZone = SystemZone,
        };
    }

    public PinStore Pins { get; }

    public TokenStore Tokens { get; }

    public TerminalSettingsStore Settings { get; }

    public ClientServices Services { get; }

    public FakeApi Api { get; } = new();

    /// <summary>The client that carries a token being tried; refuses it when <see cref="FakeApi.MarketsFail"/> is set.</summary>
    public FakeApi TokenApi { get; } = new();

    public ServerIdentity Identity { get; set; } = Samples.Identity();

    public Exception? ProbeFails { get; set; }

    public Exception? CreateApiFails { get; set; }

    public List<(string Host, int Port)> Probed { get; } = [];

    public List<Uri> Created { get; } = [];

    public List<(Uri Address, string Token)> TokenTries { get; } = [];

    public AutoAdvanceTimeProvider Time { get; } = new(autoFire: false, utcStart: Samples.Now);

    public TimeZoneInfo SystemZone { get; set; } = TimeZoneInfo.Utc;

    public static string Key => "kaldb:5280";

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}

/// <summary>Dialogs a test controls: what the connect dialog returns, and what it was shown.</summary>
internal sealed class FakeDialogs : IDialogs
{
    public List<ConnectViewModel> Shown { get; } = [];

    public Func<ConnectViewModel, Uri?> Answer { get; set; } = _ => null;

    /// <summary>When set, the dialog stays open until the test completes it.</summary>
    public TaskCompletionSource<Uri?>? Pending { get; set; }

    public Task<Uri?> ShowConnectAsync(ConnectViewModel connect)
    {
        Shown.Add(connect);
        return Pending is { } pending ? pending.Task : Task.FromResult(Answer(connect));
    }
}
