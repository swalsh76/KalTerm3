using KalTerm.Terminal.Alerts;
using KalTerm.Terminal.Markets;
using KalTerm.Terminal.Services;
using KalTerm.Terminal.Shell;
using KalTerm.Terminal.Status;
using KalTerm.Terminal.Views;

namespace KalTerm.Terminal;

/// <summary>Puts the pieces together: the formatting, the status page, the dialogs, the main view-model and the main window.</summary>
internal static class Composition
{
    /// <summary>Builds the main window with its view-model; the window connects when it is shown.</summary>
    public static MainWindow Create(ClientServices services, TerminalSettingsStore settings, IToaster toaster)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(toaster);
        var format = new Formatting(services.SystemZone);
        var status = new StatusViewModel(format, services.Time);
        MainWindow? window = null;
        var dialogs = new WpfDialogs(() => window);
        var markets = new MarketsViewModel(format, services.Time, dialogs: dialogs);
        var alerts = new AlertsViewModel(format, dialogs);
        var notifier = new AlertNotifier(toaster, format, settings, services.Time, () => alerts.Silent);
        var model = new MainViewModel(services, settings, dialogs, format, status, markets, alerts, notifier, toaster);
        window = new MainWindow { DataContext = model };
        return window;
    }
}
