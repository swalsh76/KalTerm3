using System.Windows;
using KalTerm.Terminal.Shell;

namespace KalTerm.Terminal.Views;

internal sealed partial class MainWindow : Window
{
    /// <summary>A toast was clicked: show the window if it is minimised and bring it to the front.</summary>
    private void BringForward()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as MainViewModel)?.StartCommand.Execute(null);
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel model)
            {
                model.ActivationRequested += BringForward;
            }
        };
    }
}
