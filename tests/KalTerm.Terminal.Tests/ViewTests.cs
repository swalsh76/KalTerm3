using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KalTerm.Client;
using KalTerm.Terminal.Connect;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Shell;
using KalTerm.Terminal.Status;
using KalTerm.Terminal.Views;

namespace KalTerm.Terminal.Tests;

/// <summary>The real windows and controls, built on an STA thread and laid out without being shown. A wrong binding name is silent in WPF, so each test insists there were none.</summary>
public sealed class ViewTests : IDisposable
{
    private readonly TestServices _t = new();

    public void Dispose() => _t.Dispose();

    private static StatusViewModel Loaded(TestServices t, Formatting format)
    {
        var model = new StatusViewModel(format, t.Time);
        model.Attach(t.Api);
        model.RefreshAsync().GetAwaiter().GetResult();
        return model;
    }

    [Fact]
    public void TheStatusViewShowsTheHeadingEveryLineAndTheFooter()
    {
        var texts = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var view = new StatusView { DataContext = Loaded(_t, new Formatting(TimeZoneInfo.Utc)) };
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return VisualTreeKit.Texts(view).ToList();
        });

        Assert.Contains("Server status 2026-10-18 23:00:00 UTC", texts);
        Assert.Contains("All checks passed.", texts);
        Assert.Contains("Ingest", texts);
        Assert.Contains("ticker 1s ago  |  trade 2s ago", texts);
        Assert.Contains("Ticker updates in a gap are lost.", texts);
        Assert.Contains("updated 2026-10-18 23:00:00 (0s ago)", texts);
        Assert.DoesNotContain("Loading…", texts);
        Assert.DoesNotContain("NEEDS ATTENTION", texts);
    }

    [Fact]
    public void TheStatusViewShowsProblemsAndWarnings()
    {
        _t.Api.Status = Samples.Status("disk almost full") with { Books = new KalTerm.Contracts.BooksStatusDto(2, 1, 1, 3) };

        var texts = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var view = new StatusView { DataContext = Loaded(_t, new Formatting(TimeZoneInfo.Utc)) };
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return VisualTreeKit.Texts(view).ToList();
        });

        Assert.Contains("NEEDS ATTENTION", texts);
        Assert.Contains("disk almost full", texts);
        Assert.DoesNotContain("All checks passed.", texts);
    }

    [Fact]
    public void BeforeTheFirstAnswerItSaysLoadingAndAfterAFailureItSaysWhy()
    {
        var (loading, failed) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var model = new StatusViewModel(new Formatting(TimeZoneInfo.Utc), _t.Time);
            var view = new StatusView { DataContext = model };
            VisualTreeKit.Realize(view);
            var first = VisualTreeKit.Texts(view).ToList();

            _t.Api.StatusFails = new ApiUnavailableException("could not reach kaldb:5280");
            model.Attach(_t.Api);
            model.RefreshAsync().GetAwaiter().GetResult();
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return (first, VisualTreeKit.Texts(view).ToList());
        });

        Assert.Contains("Loading…", loading);
        Assert.DoesNotContain("Loading…", failed);
        Assert.Contains("could not reach kaldb:5280", failed);
    }

    [Fact]
    public void ADatedPageIsDrawnGreyWithoutBindingErrors()
    {
        var opacity = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var model = Loaded(_t, new Formatting(TimeZoneInfo.Utc));
            _t.Time.Advance(StatusViewModel.Interval * 4);
            _t.Api.StatusFails = new ApiUnavailableException("gone");
            model.RefreshAsync().GetAwaiter().GetResult();
            var view = new StatusView { DataContext = model };
            VisualTreeKit.Realize(view);
            Assert.Empty(errors.Messages);
            return VisualTreeKit.All<StackPanel>(view).First(p => p.Margin.Left == 20).Opacity;
        });

        Assert.Equal(0.5, opacity);
    }

    [Fact]
    public void TheConnectDialogShowsEachStepWithoutBindingErrors()
    {
        var (address, trust, login) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var model = new ConnectViewModel(_t.Services);
            var window = new ConnectWindow { DataContext = model };
            VisualTreeKit.Realize(window);
            var first = VisualTreeKit.Texts(window).ToList();

            model.StartAtTrustAsync(new Uri("https://kaldb:5280"), "WARNING: the certificate CHANGED").GetAwaiter().GetResult();
            VisualTreeKit.Realize(window);
            var second = VisualTreeKit.Texts(window).ToList();

            model.StartAtLogin(new Uri("https://kaldb:5280"), "Paste a new token.");
            VisualTreeKit.Realize(window);
            var third = VisualTreeKit.Texts(window).ToList();
            Assert.Empty(errors.Messages);
            return (first, second, third);
        });

        Assert.Contains("Connect to a KalTerm server", address);
        Assert.Contains("Server address (host:port, for example kaldb:5280)", address);
        Assert.Contains("Trust this server's certificate", trust);
        Assert.Contains("WARNING: the certificate CHANGED", trust);
        Assert.Contains("CN=kaldb, O=KalTerm", trust);
        Assert.Contains("Log in", login);
        Assert.Contains("Paste a new token.", login);
    }

    [Fact]
    public void TypingATokenInThePasswordBoxReachesTheViewModelAndNothingElseDoes()
    {
        var token = Sta.Run(() =>
        {
            var model = new ConnectViewModel(_t.Services);
            var window = new ConnectWindow { DataContext = model };
            window.TokenBox.Password = Samples.Token;
            return model.Token;
        });

        Assert.Equal(Samples.Token, token);
    }

    [Fact]
    public void ATokenChangeWithoutAViewModelIsIgnored()
    {
        Sta.Run(() =>
        {
            var window = new ConnectWindow();
            window.TokenBox.Password = "anything";
        });
    }

    [Fact]
    public void TheMainWindowShowsTheStatusPageStartsWhenLoadedAndHasNoBindingErrors()
    {
        _t.Settings.SaveServer(new Uri("https://kaldb:5280"));

        var (texts, state, model) = Sta.Run(() =>
        {
            using var errors = BindingErrors.Listen();
            var window = Composition.Create(_t.Services, _t.Settings, new FakeToaster());
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            VisualTreeKit.Realize(window);
            Assert.Empty(errors.Messages);
            var vm = (MainViewModel)window.DataContext;
            return (VisualTreeKit.Texts(window).ToList(), vm.State, vm);
        });

        Assert.Equal(ConnectionState.Connected, state);
        Assert.Contains("kaldb:5280", texts);
        Assert.Contains("connected", texts);
        Assert.Contains("Server status 2026-10-18 23:00:00 UTC", texts);
        Assert.NotNull(model);
    }

    [Fact]
    public void TheMainWindowListsThePagesAndTheTimeZones()
    {
        var (pages, zones) = Sta.Run(() =>
        {
            var window = Composition.Create(_t.Services, _t.Settings, new FakeToaster());
            VisualTreeKit.Realize(window);
            return (VisualTreeKit.All<ListBox>(window).First().Items.Count, VisualTreeKit.All<ComboBox>(window).First().Items.Count);
        });

        Assert.Equal(3, pages);
        Assert.True(zones > 50);
    }

    [Fact]
    public void AClickOnAToastBringsAMinimisedWindowBack()
    {
        var state = Sta.Run(() =>
        {
            var toaster = new FakeToaster();
            var window = Composition.Create(_t.Services, _t.Settings, toaster);
            window.WindowState = WindowState.Minimized;

            toaster.Click(null);

            return window.WindowState;
        });

        Assert.Equal(WindowState.Normal, state);
    }

    [Fact]
    public void TheRealDialogsShowTheConnectWindowAndReturnWhatItDecided()
    {
        var (cancelled, connected) = Sta.Run(() =>
        {
            var dialogs = new WpfDialogs(() => null);

            var one = new ConnectViewModel(_t.Services);
            Dispatcher.CurrentDispatcher.BeginInvoke(() => one.CancelCommand.Execute(null));
            var first = dialogs.ShowConnectAsync(one).GetAwaiter().GetResult();

            _t.Pins.Set(TestServices.Key, Samples.Fingerprint, Samples.Now.UtcDateTime);
            _t.Tokens.Set(TestServices.Key, Samples.Token);
            var two = new ConnectViewModel(_t.Services, new Uri("https://kaldb:5280"));
            Dispatcher.CurrentDispatcher.BeginInvoke(() => two.NextCommand.Execute(null));
            var second = dialogs.ShowConnectAsync(two).GetAwaiter().GetResult();
            return (first, second);
        });

        Assert.Null(cancelled);
        Assert.Equal(new Uri("https://kaldb:5280"), connected);
    }

    [Fact]
    public void TheCompositionBuildsAWindowForTheRealServicesToo()
    {
        var model = Sta.Run(() => ((MainViewModel)Composition.Create(ClientServices.Real(), new TerminalSettingsStore(Path.Combine(Path.GetTempPath(), "kalterm-none-" + Guid.NewGuid().ToString("N") + ".json")), new FakeToaster()).DataContext).ServerText);

        Assert.Equal("no server", model);
    }
}
