using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KalTerm.Terminal.Views;

internal sealed partial class MarketsView : UserControl
{
    public MarketsView() => InitializeComponent();

    /// <summary>A right click does not select a row by itself; the watchlist menu is for the selected market, so the row under the pointer is selected first.</summary>
    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not DataGridRow)
        {
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        if (element is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }
}
