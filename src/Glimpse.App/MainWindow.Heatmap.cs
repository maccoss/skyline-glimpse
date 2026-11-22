using Glimpse.App.Plotting;
using Glimpse.Core.Heatmaps;
using Glimpse.Core.Scans;
using ScottPlot;

namespace Glimpse.App;

public partial class MainWindow
{
    private ScottPlot.Plottables.VerticalLine? _heatMarker;
    private ScottPlot.Panels.ColorBar? _colorBar;

    private int HeatLevel => HeatLevelCombo.SelectedIndex == 1 ? 2 : 1;

    private void InitHeatmap()
    {
        StylePlot(HeatPlot.Plot, "Retention time (min)", "m/z");
        PlotClicks.OnClick(HeatPlot, c => OnHeatmapClick(c.X, c.Y));
        PlotClicks.OnReset(HeatPlot, ResetHeatZoom);
    }

    private void RefreshHeatmap(bool autoscale)
    {
        var plot = HeatPlot.Plot;
        var limits = plot.Axes.GetLimits();
        plot.Clear();
        if (_colorBar is not null)
        {
            plot.Axes.Remove(_colorBar);
            _colorBar = null;
        }
        _heatMarker = null;
        if (_run is null)
        {
            HeatPlot.Refresh();
            return;
        }

        var valueMode = Math.Max(0, HeatValueCombo.SelectedIndex);
        var signals = _run.Signals;
        HeatmapGrid grid;
        float[,] values;
        string label;
        var log = HeatLogCheck.IsChecked == true;
        switch (valueMode)
        {
            case 1 when signals is not null:
                grid = HeatLevel == 1 ? signals.Ms1Fraction : signals.Ms2Fraction;
                values = grid.Values;
                log = false;
                label = HeatLevel == 1 ? "fraction of binned signal assigned" : "fraction of scan signal assigned";
                break;
            case 2 when signals is not null:
                // MS2 only (OnHeatValueChanged): the peptides co-isolated in each window.
                grid = signals.Ms2Count;
                values = grid.Values;
                label = (log ? "log10 " : "") + "peptides per spectrum";
                break;
            case 1 or 2:
                plot.Title(_document.Precursors.Count == 0 ? "Needs a Skyline document with results" : "Measuring document signals...");
                HeatPlot.Refresh();
                return;
            default:
                grid = HeatLevel == 1 ? _run.Heatmaps.Ms1 : _run.Heatmaps.Ms2;
                values = grid.For(Units);
                label = (log ? "log10 " : "") + (HeatLevel == 1 ? "binned " : "scan ") + (Units == Core.Data.IntensityUnits.Ions ? "ions" : "intensity");
                break;
        }
        if (grid.IsEmpty)
        {
            plot.Title($"No MS{HeatLevel} data");
            HeatPlot.Refresh();
            return;
        }

        var heat = plot.Add.Heatmap(ToDisplay(values, log));
        heat.Colormap = new ScottPlot.Colormaps.Viridis();
        heat.NaNCellColor = Colors.Transparent;
        heat.Extent = new CoordinateRect(left: grid.RtLow, right: grid.RtHigh, bottom: grid.MzLow, top: grid.MzHigh);
        _colorBar = plot.Add.ColorBar(heat);
        Glimpse.Plotting.FigureStyle.StyleAxis(_colorBar.Axis);
        _colorBar.Label = label;
        plot.Title("");
        plot.Axes.Left.Label.Text = HeatLevel == 1 ? "m/z" : "Isolation window m/z";

        if (autoscale)
            plot.Axes.SetLimits(grid.RtLow, grid.RtHigh, grid.MzLow, grid.MzHigh);
        else
            plot.Axes.SetLimits(limits);
        if (_currentScan >= 0)
            MarkHeatmap(_run.Index[_currentScan].RetentionTime);
        HeatPlot.Refresh();
    }

    /// <summary>
    /// The grid as ScottPlot wants it: row 0 at the TOP (highest m/z), optionally log10(1 + v).
    /// </summary>
    private static double[,] ToDisplay(float[,] values, bool log)
    {
        var rows = values.GetLength(0);
        var cols = values.GetLength(1);
        var result = new double[rows, cols];
        for (var r = 0; r < rows; r++)
        {
            var src = rows - 1 - r;
            for (var c = 0; c < cols; c++)
            {
                double v = values[src, c];
                result[r, c] = float.IsNaN(values[src, c]) ? double.NaN : log ? Math.Log10(1 + Math.Max(0, v)) : v;
            }
        }
        return result;
    }

    /// <summary>
    /// Peptides per spectrum is an MS2 measure, the peptides co-isolated in each window, so choosing it
    /// moves the level to MS2 and greys out MS1; any other value makes MS1 available again.
    /// </summary>
    private void OnHeatValueChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var peptidesPerSpectrum = HeatValueCombo.SelectedIndex == 2;
        HeatMs1Item.IsEnabled = !peptidesPerSpectrum;
        if (peptidesPerSpectrum && HeatLevelCombo.SelectedIndex != 1)
            HeatLevelCombo.SelectedIndex = 1;   // redraws the heatmap
        else
            OnViewOptionChanged(sender, e);
    }

    private void OnResetHeatZoom(object sender, System.Windows.RoutedEventArgs e) => ResetHeatZoom();

    private void ResetHeatZoom()
    {
        if (_run is not null)
            RefreshHeatmap(autoscale: true);
    }

    private void OnHeatmapClick(double rt, double mz)
    {
        if (_run is null)
            return;
        var filter = HeatLevel == 1 ? ScanFilter.Ms1 : ScanFilter.Ms2Containing(mz);
        var scan = _run.Index.NearestScan(rt, filter);
        if (scan >= 0)
            SelectScan(scan, filter);
    }

    private void MarkHeatmap(double rt)
    {
        if (_heatMarker is not null)
            HeatPlot.Plot.Remove(_heatMarker);
        _heatMarker = HeatPlot.Plot.Add.VerticalLine(rt, 1, Colors.OrangeRed);
        _heatMarker.LinePattern = LinePattern.Dashed;
        HeatPlot.Refresh();
    }
}
