using System.ComponentModel;
using System.Windows;
using KalTerm.Terminal.Layouts;
using KalTerm.Terminal.Shell;

namespace KalTerm.Terminal.Views;

internal sealed partial class MainWindow : Window, IWindowPlacement
{
    private bool _flushed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as MainViewModel)?.StartCommand.Execute(null);
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel model)
            {
                model.ActivationRequested += BringForward;
                model.Placement = this;
            }
        };
        LocationChanged += (_, _) => Notify();
        SizeChanged += (_, _) => Notify();
        StateChanged += (_, _) => Notify();
    }

    /// <summary>Where the window is: the place it returns to when it is maximised or minimised.</summary>
    public WindowLayout Capture()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new WindowLayout(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
    }

    /// <summary>Puts the window where a layout says, moved onto the screens there are now.</summary>
    public void Apply(WindowLayout layout)
    {
        var fit = WindowPlacement.Fit(layout, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = fit.Left;
        Top = fit.Top;
        Width = fit.Width;
        Height = fit.Height;
        WindowState = fit.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    /// <summary>The layout is saved before the window goes: closing waits for it, but only a moment, and only once.</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_flushed || DataContext is not MainViewModel model)
        {
            return;
        }

        e.Cancel = true;
        _flushed = true;
        await model.FlushLayoutAsync();
        await System.Windows.Threading.Dispatcher.Yield();   // the window may not be closed from inside its own Closing event, and an answer that came at once would still be inside it
        Close();
    }

    private void Notify() => (DataContext as MainViewModel)?.NotifyLayoutChanged();

    /// <summary>A toast was clicked: show the window if it is minimised and bring it to the front.</summary>
    private void BringForward()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
    }
}
