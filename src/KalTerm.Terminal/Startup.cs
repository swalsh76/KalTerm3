using KalTerm.Terminal.Services;
using KalTerm.Terminal.Shell;
using KalTerm.Terminal.Status;
using KalTerm.Terminal.Views;

namespace KalTerm.Terminal;

/// <summary>Puts the pieces together: the formatting, the status page, the dialogs, the main view-model and the main window.</summary>
internal static class Composition
{
    /// <summary>Builds the main window with its view-model; the window connects when it is shown.</summary>
    public static MainWindow Create(ClientServices services, TerminalSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        var format = new Formatting(services.SystemZone);
        var status = new StatusViewModel(format, services.Time);
        MainWindow? window = null;
        var model = new MainViewModel(services, settings, new WpfDialogs(() => window), format, status);
        window = new MainWindow { DataContext = model };
        return window;
    }
}
