using System.Windows.Controls;
using System.Windows.Input;
using Glimpse.App.Plotting;
using Glimpse.Plotting;
using Glimpse.Core.Chromatograms;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Scans;
using ScottPlot;

namespace Glimpse.App;

public partial class MainWindow
{
    private readonly List<ScanFilter> _scanFilters = new();
    private ScottPlot.Plottables.VerticalLine? _chromMarker;
    private MultiTracePlottable? _xicPlottable;

    private ScanFilter CurrentChromFilter =>
        ScanFilterCombo.SelectedIndex >= 0 && ScanFilterCombo.SelectedIndex < _scanFilters.Count
            ? _scanFilters[ScanFilterCombo.SelectedIndex]
            : ScanFilter.Ms1;

    private XicMode CurrentXicMode => (XicMode)Math.Max(0, XicModeCombo.SelectedIndex);

    private void InitChromatogram()
    {
        StylePlot(ChromPlot.Plot, "Retention time (min)", "Intensity");
        PlotClicks.OnClick(ChromPlot, c => OnChromatogramClick(c.X));
        PlotClicks.OnReset(ChromPlot, ResetChromZoom);
        PlotClicks.KeepFromZero(ChromPlot, ChromPlot.Plot.Axes.Left, ChromPlot.Plot.Axes.Right);
        ChromPlot.MouseMove += OnChromatogramMouseMove;
    }

    /// <summary>All scans, MS1, MS2, then one entry per isolation window (capped, for DDA data).</summary>
    private void PopulateScanFilters()
    {
        var index = _run!.Index;
        _scanFilters.Clear();
        ScanFilterCombo.Items.Clear();

        void Add(ScanFilter f, string text)
        {
            _scanFilters.Add(f);
            ScanFilterCombo.Items.Add(new ComboBoxItem { Content = text });
        }

        if (index.IndicesOfLevel(1).Count > 0)
            Add(ScanFilter.Ms1, "MS1");
        if (index.IndicesOfLevel(2).Count > 0)
            Add(ScanFilter.Ms2, "MS2 (all windows)");
        Add(ScanFilter.All, "All scans");
        const int maxWindows = 500;
        if (index.IsolationWindows.Count <= maxWindows)
        {
            foreach (var (lo, hi) in index.IsolationWindows)
                Add(ScanFilter.Ms2Window(lo, hi), $"MS2 {lo:0.00}-{hi:0.00}");
        }
        ScanFilterCombo.SelectedIndex = 0;
    }

    /// <param name="autoscale">
    /// Rescale: the time axis to the selected peptides' XICs (up to 50 of them), else to the whole run;
    /// intensity from zero. Without it the current zoom is kept.
    /// </param>
    /// <param name="fullRun">With <paramref name="autoscale"/>, show the whole run even with XICs (Reset zoom).</param>
    private void RefreshChromatogram(bool autoscale, bool fullRun = false)
    {
        if (!IsLoaded && _run is null)
            return;
        var plot = ChromPlot.Plot;
        plot.Clear();
        _chromMarker = null;
        _xicPlottable = null;
        if (_run is null)
        {
            ChromPlot.Refresh();
            return;
        }

        var filter = CurrentChromFilter;
        var units = Units;
        var xics = BuildXicTraces(units);
        var hasXics = xics.Count > 0;
        // One intensity axis for everything: an XIC is shown as the part of the TIC it is.
        var runAxis = plot.Axes.Left;

        if (TicCheck.IsChecked == true)
            AddTrace(plot, RunChromatogram.Build(_run.Index, RunTraceKind.Tic, filter, units), Colors.Black, runAxis, hasXics);
        if (BpcCheck.IsChecked == true)
            AddTrace(plot, RunChromatogram.Build(_run.Index, RunTraceKind.BasePeak, filter, units), Color.FromHex("#1F4E9A"), runAxis, hasXics);
        if (AssignedCheck.IsChecked == true && _run.Signals is { } signals)
        {
            // Drawn over the TIC: total and assigned side by side, the gap between them unassigned.
            AddTrace(plot, AssignedTrace(signals, filter, units), Colors.SeaGreen, runAxis, hasXics);
        }

        if (hasXics)
        {
            var lines = xics.Select(x => new TraceLine(x.Times, x.Values, ColorFor(x.Owner), x.Name, x.Owner)).ToList();
            _xicPlottable = new MultiTracePlottable(lines) { LineWidth = FigureStyle.XicWidth(lines.Count) };
            plot.Add.Plottable(_xicPlottable);
            ChromHoverText.Text = $"{lines.Count:N0} XIC trace{(lines.Count == 1 ? "" : "s")}";
        }
        else
        {
            ChromHoverText.Text = "";
        }
        plot.Axes.Right.Label.Text = "";
        plot.Axes.Right.IsVisible = false;

        plot.Axes.Left.Label.Text = IntensityConversion.Label(units);
        plot.Legend.IsVisible = plot.GetPlottables().Count(p => p is not MultiTracePlottable) > 1;
        plot.Legend.Alignment = Alignment.UpperRight;
        if (autoscale)
        {
            plot.Axes.AutoScale();
            var (rtFirst, rtLast) = (_run.Index.RtFirst, _run.Index.RtLast);
            if (hasXics && xics.Count <= 50 && !fullRun)
            {
                // Zoom the time axis to the selection, within the run.
                var t0 = xics.Where(x => x.Times.Length > 0).Select(x => x.Times[0]).DefaultIfEmpty(rtFirst).Min();
                var t1 = xics.Where(x => x.Times.Length > 0).Select(x => x.Times[^1]).DefaultIfEmpty(rtLast).Max();
                var pad = Math.Max(0.2, (t1 - t0) * 0.1);
                plot.Axes.SetLimitsX(Math.Max(rtFirst, t0 - pad), Math.Min(rtLast, t1 + pad));
            }
            else if (rtLast > rtFirst)
            {
                // The whole run, first scan to last: AutoScale's margins would add time before and after it.
                plot.Axes.SetLimitsX(rtFirst, rtLast);
            }
            plot.Axes.SetLimitsY(0, plot.Axes.GetLimits(plot.Axes.Bottom, plot.Axes.Left).Top, plot.Axes.Left);
        }
        if (_currentScan >= 0)
            MarkChromatogram(_run.Index[_currentScan].RetentionTime);
        ChromPlot.Refresh();
    }

    /// <summary>
    /// The TIC of the spectra read so far, on the whole run's time axis, while a file is being read.
    /// MS1 when the run has MS1 scans (they arrive within the first cycle), else every scan.
    /// </summary>
    private void ShowPreview(ScanPassPreview preview, string fileName, TimeSpan elapsed)
    {
        var plot = ChromPlot.Plot;
        plot.Clear();
        _chromMarker = null;
        _xicPlottable = null;
        plot.Axes.Right.IsVisible = false;
        plot.Legend.IsVisible = false;
        var soFar = preview.SoFar;
        var filter = soFar.IndicesOfLevel(1).Count > 0 ? ScanFilter.Ms1 : ScanFilter.All;
        var tic = RunChromatogram.Build(soFar, RunTraceKind.Tic, filter, Units);
        AddTrace(plot, tic, Colors.Black, plot.Axes.Left, dimmed: false);
        plot.Axes.Left.Label.Text = IntensityConversion.Label(Units);
        var top = tic.Count == 0 ? 1 : tic.Values.Where(double.IsFinite).DefaultIfEmpty(1).Max();
        if (double.IsFinite(preview.RtFirst) && preview.RtLast > preview.RtFirst)
            plot.Axes.SetLimitsX(preview.RtFirst, preview.RtLast);
        plot.Axes.SetLimitsY(0, top * 1.05, plot.Axes.Left);
        ChromPlot.Refresh();
        StatusText.Text = $"Reading {fileName}: {preview.Fraction:P0} of the run ({soFar.RtLast:0.0} of {preview.RtLast:0.0} min), {elapsed.TotalSeconds:0} s";
    }

    private List<XicTrace> BuildXicTraces(IntensityUnits units)
    {
        if (_run?.Signals is not { } signals || _selectedPeptides.Count == 0 || CurrentXicMode == XicMode.Off)
            return new List<XicTrace>();
        return XicTraces.Build(_run.Index, signals, _selectedPeptides, CurrentXicMode, ms1: XicLevelCombo.SelectedIndex == 1, units,
            integratedOnly: IntegratedOnlyCheck.IsChecked == true);
    }

    private Color ColorFor(object? owner) => owner switch
    {
        DocPeptide p => PeptideColor(p),
        DocProtein p => PeptideColors.For(p),
        _ => Colors.DarkOrange,
    };

    /// <summary>Per scan of the filter: the signal document peptides claim.</summary>
    private Trace AssignedTrace(Core.Assignment.DocumentSignals signals, ScanFilter filter, IntensityUnits units)
    {
        var tic = RunChromatogram.Build(_run!.Index, RunTraceKind.Tic, filter, units);
        var assigned = new double[tic.Count];
        for (var i = 0; i < tic.Count; i++)
        {
            var h = _run.Index[tic.ScanIndices[i]];
            assigned[i] = IntensityConversion.Convert(signals.AssignedIntensity[h.Index], h.InjectionTimeMs, units);
        }
        return new Trace($"Assigned ({filter.Describe()})", tic.Times, assigned, tic.ScanIndices);
    }

    private static void AddTrace(Plot plot, Trace trace, Color color, IYAxis axis, bool dimmed)
    {
        if (trace.Count == 0)
            return;
        var signal = plot.Add.SignalXY(trace.Times, trace.Values);
        // Behind XICs the run traces recede, but must stay readable (0.45 washed a blue trace out).
        signal.Color = dimmed ? color.WithAlpha(0.6) : color;
        signal.LineWidth = FigureStyle.TraceWidth;
        signal.LegendText = trace.Name;
        signal.Axes.YAxis = axis;
    }

    private void OnChromatogramClick(double rt)
    {
        if (_run is null)
            return;
        // Clicking an XIC trace selects that peptide and shows the spectrum where it was measured.
        if (_xicPlottable?.Highlighted is { Tag: DocPeptide peptide })
        {
            // Show the peptide in the spectrum where its trace was measured, and annotate it even when
            // that scan is just outside Skyline's integrated peak.
            _forcedPeptide = peptide;
            var precursor = peptide.Precursors.FirstOrDefault();
            if (precursor is not null)
            {
                var traceFilter = XicLevelCombo.SelectedIndex == 0 ? ScanFilter.Ms2Containing(precursor.Mz) : ScanFilter.Ms1;
                var traceScan = _run.Index.NearestScan(rt, traceFilter);
                if (traceScan >= 0)
                {
                    SelectScan(traceScan, traceFilter);
                    return;
                }
            }
        }
        _forcedPeptide = null;
        var filter = CurrentChromFilter;
        var scan = _run.Index.NearestScan(rt, filter);
        if (scan >= 0)
            SelectScan(scan, filter);
    }

    private void OnChromatogramMouseMove(object sender, MouseEventArgs e)
    {
        if (_xicPlottable is null)
        {
            ChromHoverText.Text = "";
            return;
        }
        var nearest = _xicPlottable.Nearest(PlotClicks.ToRenderPixel(ChromPlot, e));
        if (!ReferenceEquals(nearest, _xicPlottable.Highlighted))
        {
            _xicPlottable.Highlighted = nearest;
            ChromPlot.Refresh();
        }
        ChromHoverText.Text = nearest?.Name ?? $"{_xicPlottable.Traces.Count:N0} XIC trace{(_xicPlottable.Traces.Count == 1 ? "" : "s")}";
    }

    private void OnResetChromZoom(object sender, System.Windows.RoutedEventArgs e) => ResetChromZoom();

    /// <summary>Back to the whole run, first retention time to last, intensity from zero.</summary>
    private void ResetChromZoom()
    {
        if (_run is not null)
            RefreshChromatogram(autoscale: true, fullRun: true);
    }

    private void MarkChromatogram(double rt)
    {
        if (_chromMarker is not null)
            ChromPlot.Plot.Remove(_chromMarker);
        _chromMarker = ChromPlot.Plot.Add.VerticalLine(rt, 1, Colors.OrangeRed);
        _chromMarker.LinePattern = LinePattern.Dashed;
        ChromPlot.Refresh();
    }

    private static void StylePlot(Plot plot, string xLabel, string yLabel) => FigureStyle.Apply(plot, xLabel, yLabel);
}
