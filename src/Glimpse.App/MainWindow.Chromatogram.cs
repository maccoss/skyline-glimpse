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
    private readonly XicBuildCache _xicCache = new();
    private bool _xicPendingAutoscale;
    private bool _xicPendingFullRun;

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
    /// Rescale: the time axis to the whole run, or to the selected peptides' XICs when they are drawn
    /// without the TIC, base peak or assigned trace (<see cref="ChromatogramTimeAxis"/>); intensity from
    /// zero. Without it the current zoom is kept.
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
        var xics = XicTracesFor(units, autoscale, fullRun, out var buildingXics);
        var hasXics = xics.Count > 0;
        // One intensity axis for everything, so an XIC is shown as the part of the TIC it is; or, with
        // "Scale XICs separately", the XICs on their own axis on the right (a peptide's fragments are a
        // millionth of an MS1 TIC and lie flat on a shared axis).
        var runAxis = plot.Axes.Left;
        var hasRunTraces = TicCheck.IsChecked == true || BpcCheck.IsChecked == true
            || (AssignedCheck.IsChecked == true && _run.Signals is not null);
        var separateXicAxis = hasXics && hasRunTraces && XicSeparateCheck.IsChecked == true;

        if (TicCheck.IsChecked == true)
            AddTrace(plot, RunChromatogram.Build(_run.Index, RunTraceKind.Tic, filter, units, Calibration), Colors.Black, runAxis, hasXics);
        if (BpcCheck.IsChecked == true)
            AddTrace(plot, RunChromatogram.Build(_run.Index, RunTraceKind.BasePeak, filter, units, Calibration), Color.FromHex("#1F4E9A"), runAxis, hasXics);
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
            if (separateXicAxis)
                _xicPlottable.Axes.YAxis = plot.Axes.Right;
            ChromHoverText.Text = $"{lines.Count:N0} XIC trace{(lines.Count == 1 ? "" : "s")}";
        }
        else
        {
            ChromHoverText.Text = buildingXics ? $"Building XICs for {_selectedPeptides.Count:N0} peptides..." : "";
        }
        plot.Axes.Right.Label.Text = separateXicAxis ? "XIC " + IntensityConversion.Label(units).ToLowerInvariant() : "";
        plot.Axes.Right.IsVisible = separateXicAxis;

        plot.Axes.Left.Label.Text = IntensityConversion.Label(units);
        plot.Legend.IsVisible = plot.GetPlottables().Count(p => p is not MultiTracePlottable) > 1;
        plot.Legend.Alignment = Alignment.UpperRight;
        if (autoscale)
        {
            plot.Axes.AutoScale();
            // The whole run while the TIC or another run trace is drawn; XICs on their own, their span.
            // Set exactly: AutoScale's margins would add time before and after the run.
            var (low, high) = ChromatogramTimeAxis.Range(_run.Index.RtFirst, _run.Index.RtLast, xics, hasRunTraces, fullRun);
            if (high > low)
                plot.Axes.SetLimitsX(low, high);
            plot.Axes.SetLimitsY(0, plot.Axes.GetLimits(plot.Axes.Bottom, plot.Axes.Left).Top, plot.Axes.Left);
            if (separateXicAxis)
                plot.Axes.SetLimitsY(0, plot.Axes.GetLimits(plot.Axes.Bottom, plot.Axes.Right).Top, plot.Axes.Right);
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
        var tic = RunChromatogram.Build(soFar, RunTraceKind.Tic, filter, Units, CalibrationFor(soFar.Run.InstrumentModel));
        AddTrace(plot, tic, Colors.Black, plot.Axes.Left, dimmed: false);
        plot.Axes.Left.Label.Text = IntensityConversion.Label(Units);
        var top = tic.Count == 0 ? 1 : tic.Values.Where(double.IsFinite).DefaultIfEmpty(1).Max();
        if (double.IsFinite(preview.RtFirst) && preview.RtLast > preview.RtFirst)
            plot.Axes.SetLimitsX(preview.RtFirst, preview.RtLast);
        plot.Axes.SetLimitsY(0, top * 1.05, plot.Axes.Left);
        ChromPlot.Refresh();
        StatusText.Text = $"Reading {fileName}: {preview.Fraction:P0} of the run ({soFar.RtLast:0.0} of {preview.RtLast:0.0} min), {elapsed.TotalSeconds:0} s";
    }

    /// <summary>Selections up to this many peptides have their XICs built at once; larger ones in the background.</summary>
    /// <remarks>Every peptide of a 69,000-precursor document takes seconds to sum: the window must not freeze meanwhile.</remarks>
    private const int MaxImmediateXicPeptides = 200;

    /// <summary>
    /// The XICs of the selection, built at once or kept from last time. A large selection is built in the
    /// background: none are returned (<paramref name="building"/> is set) and the chromatogram is drawn
    /// again, with the view asked for, once they are ready.
    /// </summary>
    private List<XicTrace> XicTracesFor(IntensityUnits units, bool autoscale, bool fullRun, out bool building)
    {
        building = false;
        if (_run?.Signals is not { } signals || _selectedPeptides.Count == 0 || CurrentXicMode == XicMode.Off)
        {
            _xicCache.Store(null, new List<XicTrace>());
            return new List<XicTrace>();
        }
        var request = new XicRequest(_run.Index, signals, _selectedPeptides, CurrentXicMode, Ms1: XicLevelCombo.SelectedIndex == 1, units,
            IntegratedOnly: IntegratedOnlyCheck.IsChecked == true, Calibration: Calibration);
        if (_xicCache.TryGet(request, out var traces))
            return traces;
        if (request.Peptides.Count <= MaxImmediateXicPeptides)
        {
            traces = request.Build();
            _xicCache.Store(request, traces);
            return traces;
        }
        building = true;
        if (_xicCache.Start(request))
        {
            _xicPendingAutoscale = false;
            _xicPendingFullRun = false;
            BuildXicsInBackground(request);
        }
        _xicPendingAutoscale |= autoscale;
        _xicPendingFullRun = fullRun;
        return traces;
    }

    private async void BuildXicsInBackground(XicRequest request)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        List<XicTrace> traces;
        try
        {
            traces = await Task.Run(request.Build);
        }
        catch (Exception ex)
        {
            App.WriteLog("Could not build the XICs: " + ex.Message);
            _xicCache.Fail(request);
            ChromHoverText.Text = "Could not build the XICs";
            return;
        }
        if (!_xicCache.Complete(request, traces))
            return;   // the selection or the view changed meanwhile
        App.WriteLog($"XICs for {request.Peptides.Count:N0} peptides ({traces.Count:N0} traces) in {clock.ElapsedMilliseconds:N0} ms");
        RefreshChromatogram(_xicPendingAutoscale, _xicPendingFullRun);
    }

    private Color ColorFor(object? owner) => owner switch
    {
        DocPrecursor p => ChargeTint(PeptideColor(p.Peptide), p),
        DocPeptide p => PeptideColor(p),
        DocProtein p => PeptideColors.For(p),
        _ => Colors.DarkOrange,
    };

    /// <summary>
    /// A precursor's XIC color: its peptide's color, lighter for each further charge state or label, so a
    /// peptide's 2+ and 3+ are told apart and still read as one peptide.
    /// </summary>
    private static Color ChargeTint(Color peptideColor, DocPrecursor precursor)
    {
        var rank = precursor.Peptide.Precursors.IndexOf(precursor);
        if (rank <= 0)
            return peptideColor;
        var toWhite = Math.Min(0.6, 0.3 * rank);
        byte Mix(byte c) => (byte)(c + (255 - c) * toWhite);
        return new Color(Mix(peptideColor.R), Mix(peptideColor.G), Mix(peptideColor.B), peptideColor.A);
    }

    /// <summary>Per scan of the filter: the signal document peptides claim.</summary>
    private Trace AssignedTrace(Core.Assignment.DocumentSignals signals, ScanFilter filter, IntensityUnits units)
    {
        var calibration = Calibration;
        var tic = RunChromatogram.Build(_run!.Index, RunTraceKind.Tic, filter, units, calibration);
        var assigned = new double[tic.Count];
        for (var i = 0; i < tic.Count; i++)
        {
            var h = _run.Index[tic.ScanIndices[i]];
            assigned[i] = IntensityConversion.Convert(signals.AssignedIntensity[h.Index], h, units, calibration);
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
        // Clicking an XIC trace selects that peptide and shows the spectrum where it was measured: for a
        // charge state's trace, that precursor's own isolation window.
        var clicked = _xicPlottable?.Highlighted?.Tag switch
        {
            DocPrecursor p => (p.Peptide, p),
            DocPeptide p => (p, p.Precursors.FirstOrDefault()),
            _ => ((DocPeptide?)null, (DocPrecursor?)null),
        };
        if (clicked.Item1 is { } peptide)
        {
            // Show the peptide in the spectrum where its trace was measured, and annotate it even when
            // that scan is just outside Skyline's integrated peak.
            _forcedPeptide = peptide;
            var precursor = clicked.Item2;
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

    /// <summary>How close, in render units at the reference plot size, the mouse must be to pick an XIC trace.</summary>
    private const double HoverRadius = 8;

    private void OnChromatogramMouseMove(object sender, MouseEventArgs e)
    {
        if (_xicPlottable is null)
        {
            ChromHoverText.Text = "";
            return;
        }
        // The same hover radius on screen however much the plot's text and lines have grown.
        var nearest = _xicPlottable.Nearest(PlotClicks.ToRenderPixel(ChromPlot, e), HoverRadius / PlotClicks.SizeScaleOf(ChromPlot));
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
