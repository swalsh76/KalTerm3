using System.Windows;
using KalTerm.Terminal.Services;

namespace KalTerm.Terminal;

/// <summary>The application: builds the real services, shows the main window, and keeps a stray exception from ending the program unseen.</summary>
internal sealed partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "KalTerm", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        Composition.Create(ClientServices.Real(), new TerminalSettingsStore(TerminalSettingsStore.DefaultPath())).Show();
    }
}
