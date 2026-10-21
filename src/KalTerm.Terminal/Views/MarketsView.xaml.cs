using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KalTerm.Terminal.Markets;

namespace KalTerm.Terminal.Views;

internal sealed partial class MarketsView : UserControl
{
    public MarketsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MarketsViewModel old)
            {
                old.PropertyChanged -= OnModelChanged;
            }

            if (e.NewValue is MarketsViewModel model)
            {
                model.PropertyChanged += OnModelChanged;
                ShowRanking(model);
            }
        };
    }

    /// <summary>A header was clicked: the server ranks the whole list, so the grid does not sort the 50 rows itself; the view-model is told which ranking was meant.</summary>
    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        (DataContext as MarketsViewModel)?.SortBy(e.Column.SortMemberPath);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MarketsViewModel model && e.PropertyName is nameof(MarketsViewModel.Sort) or nameof(MarketsViewModel.Descending))
        {
            ShowRanking(model);
        }
    }

    /// <summary>The arrow on the header of the column the list is ranked by.</summary>
    private void ShowRanking(MarketsViewModel model)
    {
        foreach (var column in Grid.Columns)
        {
            column.SortDirection = column.SortMemberPath == model.Sort.Key ? (model.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
        }
    }

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
