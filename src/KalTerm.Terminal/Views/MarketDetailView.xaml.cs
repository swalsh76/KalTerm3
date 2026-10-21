using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KalTerm.Terminal.Markets;

namespace KalTerm.Terminal.Views;

internal sealed partial class MarketDetailView : UserControl
{
    public MarketDetailView() => InitializeComponent();

    /// <summary>A button does not open a menu on a left click by itself: give the one it carries the market's lines and open it under the button.</summary>
    private void OnWatchlistsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu, DataContext: MarketDetailViewModel { Watchlists: { } watchlists } } button)
        {
            menu.ItemsSource = watchlists.Choices;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
