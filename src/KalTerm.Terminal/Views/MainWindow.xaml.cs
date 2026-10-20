using System.Windows;
using KalTerm.Terminal.Shell;

namespace KalTerm.Terminal.Views;

internal sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as MainViewModel)?.StartCommand.Execute(null);
    }
}
