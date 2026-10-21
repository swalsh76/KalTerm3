using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KalTerm.Terminal.Markets;

namespace KalTerm.Terminal.Views;

/// <summary>Hosts the chart control: draws what the view-model says (every time it says it changed) and tells it where the pointer is. Everything else is in <see cref="CandleChartViewModel"/> and <see cref="CandlePlotBuilder"/>.</summary>
internal sealed partial class CandleChartView : UserControl
{
    /// <summary>The earliest and latest dates a chart position can be turned into a date.</summary>
    private const double FirstDay = 1;
    private const double LastDay = 2_958_000;

    private CandleChartViewModel? _model;

    public CandleChartView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Chart.MouseMove += OnMouseMove;
        Chart.MouseLeave += (_, _) => _model?.HoverAt(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.SeriesChanged -= Redraw;
        }

        _model = e.NewValue as CandleChartViewModel;
        if (_model is not null)
        {
            _model.SeriesChanged += Redraw;
        }

        Redraw();
    }

    private void Redraw()
    {
        if (_model is null)
        {
            return;
        }

        CandlePlotBuilder.Draw(Chart.Plot, _model.Series, _model.Zone, _model.LastPrice);
        Chart.Refresh();
    }

    private void OnResetView(object sender, RoutedEventArgs e) => Redraw();

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(Chart);
        var coordinates = Chart.Plot.GetCoordinates(new ScottPlot.Pixel((float)(at.X * Chart.DisplayScale), (float)(at.Y * Chart.DisplayScale)));
        _model?.HoverAt(coordinates.X is > FirstDay and < LastDay ? DateTime.FromOADate(coordinates.X) : null);
    }
}
