using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KalTerm.Terminal.Layouts;
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
                old.ColumnsApplied -= OnColumnsApplied;
            }

            if (e.NewValue is MarketsViewModel model)
            {
                model.PropertyChanged += OnModelChanged;
                model.ColumnsApplied += OnColumnsApplied;
                ShowRanking(model);
                OnColumnsApplied();
            }
        };
        Grid.ColumnReordered += (_, _) => ReportColumns();
        Grid.PreviewMouseLeftButtonUp += (_, _) => ReportColumns();   // the end of dragging a column border: a resize
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

    /// <summary>A layout set the columns: take their widths and order. A column the layout does not know, or a width of 0 (a column that stretches), is left as it is.</summary>
    private void OnColumnsApplied()
    {
        if (DataContext is not MarketsViewModel { Columns.Count: > 0 } model)
        {
            return;
        }

        var saved = model.Columns;
        foreach (var column in Grid.Columns)
        {
            if (saved.FirstOrDefault(c => c.Key == column.Header as string) is { Width: > 0 } width)
            {
                column.Width = new DataGridLength(width.Width);
            }
        }

        var order = saved.Where(c => Grid.Columns.Any(g => g.Header as string == c.Key)).OrderBy(c => c.Order).ToList();
        if (order.Count == Grid.Columns.Count)
        {
            for (var i = 0; i < order.Count; i++)
            {
                Grid.Columns.Single(g => g.Header as string == order[i].Key).DisplayIndex = i;
            }
        }
    }

    /// <summary>The person moved or resized a column: the view-model remembers it for the layout. A column that stretches is reported with a width of 0.</summary>
    private void ReportColumns()
    {
        if (DataContext is MarketsViewModel model)
        {
            model.SetColumns([.. Grid.Columns.Select(c => new ColumnLayout(c.Header as string ?? string.Empty, c.Width.IsStar ? 0 : c.ActualWidth, c.DisplayIndex))]);
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
