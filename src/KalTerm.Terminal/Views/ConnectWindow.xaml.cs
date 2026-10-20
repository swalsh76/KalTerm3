using System.Windows;
using KalTerm.Terminal.Connect;

namespace KalTerm.Terminal.Views;

internal sealed partial class ConnectWindow : Window
{
    public ConnectWindow() => InitializeComponent();

    /// <summary>A password box cannot be bound; the token is handed to the view-model as it is typed.</summary>
    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectViewModel model)
        {
            model.Token = TokenBox.Password;
        }
    }
}
