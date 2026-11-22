using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glimpse.App.Plotting;
using Glimpse.Plotting;
using Glimpse.Core.Assignment;
using Glimpse.Core.Chemistry;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Scans;
using ScottPlot;
using Color = ScottPlot.Color;

namespace Glimpse.App;

public partial class MainWindow
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private int _currentScan = -1;
    private ScanFilter _currentScanFilter = ScanFilter.All;
    private int _spectrumRequest;
    private SpectrumData? _spectrum;
    private SpectrumAnnotation? _annotation;
    private PrecursorAnnotation? _focused;
    private StickPlottable? _sticks;
    private bool _suppressLegend;

    /// <summary>A peptide the user picked from an XIC: always annotated and focused in the spectra that follow.</summary>
    private DocPeptide? _forcedPeptide;

    private readonly HashSet<IonType> _labelTypes = new() { IonType.B, IonType.Y };
    private readonly HashSet<int> _labelCharges = new() { 1, 2 };
    private bool _labelPrecursor = true;

    /// <summary>One row of the legend beside the spectrum.</summary>
    private sealed record LegendRow(PrecursorAnnotation? Annotation, string Text, string Detail, Brush Swatch);

    private void InitSpectrum()
    {
        StylePlot(SpectrumPlot.Plot, "m/z", "Intensity");
        SpectrumPlot.MouseMove += OnSpectrumMouseMove;
        PlotClicks.OnReset(SpectrumPlot, ResetSpectrumZoom);
        PlotClicks.KeepFromZero(SpectrumPlot, SpectrumPlot.Plot.Axes.Left);
        BuildLabelMenus();
    }

    private void OnResetSpectrumZoom(object sender, RoutedEventArgs e) => ResetSpectrumZoom();

    /// <summary>The whole spectrum again, intensity from zero.</summary>
    private void ResetSpectrumZoom()
    {
        if (_sticks is null)
            return;
        var plot = SpectrumPlot.Plot;
        plot.Axes.AutoScale();
        plot.Axes.SetLimitsY(0, plot.Axes.GetLimits().Top);
        SpectrumPlot.Refresh();
    }

    private void BuildLabelMenus()
    {
        foreach (var type in Fragments.FragmentTypes)
        {
            var item = new MenuItem { Header = Fragments.Name(type), IsCheckable = true, IsChecked = _labelTypes.Contains(type), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (item.IsChecked)
                    _labelTypes.Add(type);
                else
                    _labelTypes.Remove(type);
                ReannotateCurrent();
            };
            IonTypesMenu.Items.Add(item);
        }
        IonTypesMenu.Items.Add(new Separator());
        var precursor = new MenuItem { Header = "precursor", IsCheckable = true, IsChecked = _labelPrecursor, StaysOpenOnClick = true };
        precursor.Click += (_, _) =>
        {
            _labelPrecursor = precursor.IsChecked;
            ReannotateCurrent();
        };
        IonTypesMenu.Items.Add(precursor);

        for (var z = 1; z <= 4; z++)
        {
            var charge = z;
            var item = new MenuItem { Header = $"{z}+", IsCheckable = true, IsChecked = _labelCharges.Contains(z), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (item.IsChecked)
                    _labelCharges.Add(charge);
                else
                    _labelCharges.Remove(charge);
                ReannotateCurrent();
            };
            ChargesMenu.Items.Add(item);
        }
    }

    private LabelOptions CurrentLabelOptions() => new()
    {
        Source = (LabelSource)Math.Max(0, LabelSourceCombo.SelectedIndex),
        Types = new HashSet<IonType>(_labelTypes),
        Charges = new HashSet<int>(_labelCharges),
        IncludePrecursor = _labelPrecursor,
    };

    private void OnLabelOptionChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ReannotateCurrent();
    }

    private void ReannotateCurrent()
    {
        if (_currentScan >= 0)
            _ = ShowSpectrumAsync(_currentScan);
    }

    /// <summary>Select a scan: show it, mark it on the chromatogram and heatmap, and remember the filter for stepping.</summary>
    private void SelectScan(int scan, ScanFilter filter)
    {
        _currentScanFilter = filter;
        _ = ShowSpectrumAsync(scan);
    }

    private void StepScan(int step)
    {
        if (_run is null || _currentScan < 0)
            return;
        var next = _run.Index.StepScan(_currentScan, step, _currentScanFilter);
        if (next >= 0)
            _ = ShowSpectrumAsync(next);
    }

    private void OnPreviousScan(object sender, RoutedEventArgs e) => StepScan(-1);

    private void OnNextScan(object sender, RoutedEventArgs e) => StepScan(+1);

    private async Task ShowSpectrumAsync(int scan)
    {
        var run = _run;
        if (run is null)
            return;
        var request = ++_spectrumRequest;
        _currentScan = scan;
        var rt = run.Index[scan].RetentionTime;
        MarkChromatogram(rt);
        MarkHeatmap(rt);

        var options = CurrentLabelOptions();
        var settings = _settings;
        var library = _library;
        var forced = _forcedPeptide;
        SpectrumData spectrum;
        SpectrumAnnotation annotation;
        try
        {
            (spectrum, annotation) = await Task.Run(() =>
            {
                var s = run.ReadSpectrum(scan);
                var candidates = CandidatesFor(s);
                if (forced is not null)
                {
                    // The picked peptide's own copy first: a peptide listed under several proteins is shown
                    // as the one picked, and once.
                    var picked = forced.Precursors.Where(p => s.Header.MsLevel < 2 || s.Header.IsolationContains(p.Mz));
                    candidates = SpectrumCandidates.DistinctMolecules(picked.Concat(candidates));
                }
                return (s, SpectrumAnnotator.Annotate(s, candidates, options, settings, library));
            });
        }
        catch (Exception ex)
        {
            // A read cut short by a newer click or a different file (its reader closed) is not an error.
            if (request == _spectrumRequest && ReferenceEquals(run, _run))
                SpectrumTitle.Text = $"Could not read scan {scan}: {ex.Message}";
            return;
        }
        // A newer click (or a different file) superseded this one while it was being read.
        if (request != _spectrumRequest || !ReferenceEquals(run, _run))
            return;
        var keepFocus = _focused?.Precursor;
        _spectrum = spectrum;
        _annotation = annotation;
        _focused = forced is not null
            ? annotation.Precursors.FirstOrDefault(a => a.Precursor.Peptide == forced)
            : keepFocus is null ? null : annotation.Precursors.FirstOrDefault(a => a.Precursor == keepFocus);
        RenderSpectrum();
        FillLegend();
        ShowSpectrumProperties(run, spectrum);
    }

    private void RenderSpectrum()
    {
        if (_spectrum is not { } s)
            return;
        var plot = SpectrumPlot.Plot;
        var limits = plot.Axes.GetLimits();
        var keepZoom = _sticks is not null && SpectrumPlot.Tag is int previous && previous == s.Header.Index;
        plot.Clear();
        var h = s.Header;
        var ions = Units == IntensityUnits.Ions && h.HasInjectionTime;
        var divisor = Calibration.Divisor(h.Analyzer);
        var values = ions ? s.Intensity.Select(v => v * h.InjectionTimeMs / 1000.0 / divisor).ToArray() : s.Intensity;
        _sticks = new StickPlottable(s.Mz, values);
        if (_annotation is { } a && a.Precursors.Count > 0)
        {
            var colors = new Color?[s.Mz.Length];
            var labels = ShowLabelsCheck.IsChecked == true ? new string?[s.Mz.Length] : null;
            var duplicates = DuplicatesCheck.IsChecked == true;
            var showMz = ShowMzCheck.IsChecked == true;
            for (var i = 0; i < s.Mz.Length; i++)
            {
                if (a.ByPeak[i] is not { Count: > 0 } matches)
                    continue;
                var owners = matches.Select(m => m.Owner).Distinct().ToList();
                if (_focused is not null && !owners.Contains(_focused))
                {
                    colors[i] = Color.FromHex("#d0d0d0"); // dim everything but the focused peptide
                    continue;
                }
                var shown = _focused is null ? matches : matches.Where(m => m.Owner == _focused).ToList();
                colors[i] = owners.Count > 1 && _focused is null ? PeptideColors.Shared : PeptideColor(shown[0].Owner.Precursor.Peptide);
                if (labels is not null)
                {
                    var text = duplicates
                        ? string.Join("/", shown.Select(m => PeakLabels.For(m, h.MsLevel)).Distinct())
                        : PeakLabels.For(shown[0], h.MsLevel) + (shown.Count > 1 ? "*" : "");
                    labels[i] = showMz ? $"{text} {s.Mz[i]:0.000}" : text;
                }
            }
            _sticks.PeakColors = colors;
            _sticks.PeakLabels = labels;
        }
        plot.Add.Plottable(_sticks);
        plot.Axes.Left.Label.Text = ions ? "Ions" : "Intensity";
        if (keepZoom)
        {
            plot.Axes.SetLimits(limits);
        }
        else
        {
            plot.Axes.AutoScale();
            plot.Axes.SetLimitsY(0, plot.Axes.GetLimits().Top);
        }
        SpectrumPlot.Tag = s.Header.Index;
        SpectrumPlot.Refresh();

        var title = $"Scan {s.Details.ScanNumber?.ToString(Inv) ?? (h.Index + 1).ToString(Inv)}  MS{h.MsLevel}  RT {h.RetentionTime:0.0000} min";
        if (h.HasIsolationWindow)
            title += $"  isolation {h.IsolationLower:0.00}-{h.IsolationUpper:0.00}";
        if (h.HasInjectionTime)
            title += $"  IT {h.InjectionTimeMs:0.###} ms";
        title += $"  {h.PeakCount:N0} peaks";
        if (_annotation is { Precursors.Count: > 0 } ann)
            title += $"  {ann.AssignedFraction:P1} assigned";
        if (!string.IsNullOrEmpty(s.Details.FilterString))
            title += $"   {s.Details.FilterString}";
        SpectrumTitle.Text = title;
        SpectrumTitle.ToolTip = title;
    }

    private void FillLegend()
    {
        _suppressLegend = true;
        try
        {
            var rows = new List<LegendRow>();
            if (_annotation is { } a && _spectrum is { } s)
            {
                var total = Math.Max(1e-12, a.TotalIntensity);
                rows.Add(new LegendRow(null, "(all peptides)", "Show every peptide", Brushes.Transparent));
                foreach (var p in a.Precursors)
                {
                    var color = WpfBrushes.From(PeptideColor(p.Precursor.Peptide));
                    var text = $"{p.Precursor.Peptide.ModifiedSequence} {p.Precursor.Charge}+  {p.ExplainedIntensity / total:P1}  ({p.MatchedIons}/{p.TheoreticalIons})";
                    var detail = $"{p.Precursor.Peptide.Protein.Name}\nm/z {p.Precursor.Mz:0.0000} {p.Precursor.LabelType}\n"
                        + $"{p.MatchedIons} of {p.TheoreticalIons} ions matched; {p.ExplainedIntensity / total:P1} of the spectrum ({p.UniqueIntensity / total:P1} unshared)"
                        + (p.Reconciled ? "" : "\nSequence masses did not reproduce Skyline's m/z: document transitions only")
                        + (p.LibraryDotProduct is { } d ? $"\nlibrary dotp {d:0.000}" : "");
                    rows.Add(new LegendRow(p, text, detail, color));
                }
                var count = a.Precursors.Count;
                LegendHeader.Text = count == 0
                    ? "No document peptides in this spectrum"
                    : $"{count:N0} peptide{(count == 1 ? "" : "s")}: {a.AssignedFraction:P1} of signal, {a.AssignedPeaks:N0}/{s.Mz.Length:N0} peaks";
            }
            LegendList.ItemsSource = rows;
            LegendList.SelectedItem = rows.FirstOrDefault(r => r.Annotation is not null && r.Annotation == _focused);
        }
        finally
        {
            _suppressLegend = false;
        }
    }

    private void OnLegendSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLegend)
            return;
        _focused = (LegendList.SelectedItem as LegendRow)?.Annotation;
        if (_focused is null)
            _forcedPeptide = null;
        RenderSpectrum();
        if (_run is not null && _spectrum is not null)
            ShowSpectrumProperties(_run, _spectrum);
    }

    private void OnLegendDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((LegendList.SelectedItem as LegendRow)?.Annotation is { } a)
            NavigateSkyline(a.Precursor.Locator ?? a.Precursor.Peptide.Locator);
    }

    private void ClearSpectrum()
    {
        _spectrum = null;
        _annotation = null;
        _focused = null;
        _sticks = null;
        SpectrumPlot.Plot.Clear();
        SpectrumPlot.Refresh();
        SpectrumTitle.Text = "Click a chromatogram or heatmap to show a spectrum";
        SpectrumHoverText.Text = "";
        LegendList.ItemsSource = null;
        LegendHeader.Text = "Peptides in spectrum";
    }

    private void OnSpectrumMouseMove(object sender, MouseEventArgs e)
    {
        if (_spectrum is null || _sticks is null)
            return;
        var c = PlotClicks.ToCoordinates(SpectrumPlot, e);
        var axis = SpectrumPlot.Plot.Axes.Bottom;
        var pixelsPerMz = SpectrumPlot.Plot.RenderManager.LastRender.DataRect.Width / Math.Max(1e-9, axis.Range.Span);
        var i = _sticks.NearestPeak(c.X, pixelsPerMz);
        if (i < 0)
        {
            SpectrumHoverText.Text = $"m/z {c.X:0.0000}";
            return;
        }
        var text = $"m/z {_spectrum.Mz[i]:0.0000}  intensity {_spectrum.Intensity[i]:0.###E0}";
        if (_annotation?.ByPeak[i] is { Count: > 0 } matches)
        {
            text += "  " + string.Join("; ", matches.Take(4).Select(m =>
                $"{m.Ion.Label} {m.Owner.Precursor.Peptide.ModifiedSequence} ({m.Owner.Precursor.Peptide.Protein.Name}) {m.PpmError:+0.0;-0.0} ppm"));
            if (matches.Count > 4)
                text += $"; +{matches.Count - 4} more";
        }
        SpectrumHoverText.Text = text;
        SpectrumHoverText.ToolTip = text;
    }

    private void ShowRunProperties()
    {
        Properties.Clear();
        AddRunRows(_run!);
    }

    private void AddRunRows(LoadedRun run)
    {
        var info = run.Index.Run;
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                Properties.Add(new PropertyRow("File", name, value));
        }
        Add("File name", info.FileName);
        Add("File path", Path.GetDirectoryName(run.Index.FilePath));
        Add("Replicate", _document.ResultFiles.FirstOrDefault(f => f.FileName == CurrentResultFileName())?.ReplicateName);
        Add("Acquired", info.StartTime?.ToString("yyyy-MM-dd HH:mm", Inv));
        Add("Instrument", info.InstrumentModel);
        Add("Serial number", info.InstrumentSerialNumber);
        Add("Spectra", $"{run.Index.Count:N0} ({run.Index.IndicesOfLevel(1).Count:N0} MS1, {run.Index.IndicesOfLevel(2).Count:N0} MS2)");
        Add("Retention time", $"{run.Index.RtFirst:0.00} - {run.Index.RtLast:0.00} min");
        Add("Isolation windows", run.Index.IsolationWindows.Count.ToString("N0", Inv));
        Add("Centroided", run.Centroid ? "vendor centroiding requested" : "as stored");
        Add("Ion calibration", Calibration.Describe() ?? "α 1 (instrument not in the calibration table)");
        if (run.Signals is { } sig)
            Add("Document precursors", $"{sig.PrecursorsInFile:N0} with integrated peaks in this file");
        foreach (var c in info.Configurations)
            Add("Configuration", c);
    }

    private void ShowSpectrumProperties(LoadedRun run, SpectrumData s)
    {
        Properties.Clear();
        AddRunRows(run);
        var h = s.Header;
        var d = s.Details;
        void Add(string category, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                Properties.Add(new PropertyRow(category, name, value));
        }

        if (_focused is { } f)
            AddFocusedRows(f, Add);

        const string acq = "Acquisition";
        Add(acq, "Scan number", d.ScanNumber?.ToString(Inv));
        Add(acq, "Spectrum index", h.Index.ToString(Inv));
        Add(acq, "Native ID", d.NativeId);
        Add(acq, "MS level", h.MsLevel.ToString(Inv));
        Add(acq, "Retention time", $"{h.RetentionTime:0.0000} min");
        Add(acq, "Polarity", h.Polarity.ToString());
        Add(acq, "Injection time", h.HasInjectionTime ? $"{h.InjectionTimeMs:0.###} ms" : "not reported");
        Add(acq, "Centroided", h.Centroided ? "yes" : "no (profile)");
        if (h.HasIsolationWindow)
            Add(acq, "Isolation window", $"{h.IsolationLower:0.0000} - {h.IsolationUpper:0.0000} (target {h.IsolationTarget:0.0000}, width {h.IsolationUpper - h.IsolationLower:0.###})");
        Add(acq, "Selected ion", d.SelectedIonMz is { } sel ? $"{sel:0.0000}" + (d.SelectedIonCharge is { } z ? $" ({z}+)" : "") : null);
        if (h.Analyzer != MassAnalyzer.Unknown)
        {
            var alpha = Calibration.AlphaFor(h.Analyzer);
            Add(acq, "Analyzer", AnalyzerName(h.Analyzer)
                + (Calibration.IsCalibrated(h.Analyzer) ? $" (ion calibration α {alpha.ToString("0.##", Inv)})" : " (ion calibration α 1: not in the table)"));
        }
        Add(acq, "Dissociation", d.Dissociation);
        Add(acq, "Collision energy", d.CollisionEnergy?.ToString("0.##", Inv));
        if (d.ScanWindowLower is { } lo && d.ScanWindowUpper is { } hi)
            Add(acq, "Scan range", $"{lo:0.##} - {hi:0.##}");
        Add(acq, "Ion mobility", d.IonMobility);
        Add(acq, "Filter", d.FilterString);

        const string stats = "Spectrum statistics";
        Add(stats, "Peaks", h.PeakCount.ToString("N0", Inv));
        if (s.Mz.Length > 0)
            Add(stats, "m/z range", $"{s.Mz[0]:0.0000} - {s.Mz[^1]:0.0000}");
        Add(stats, "TIC", $"{h.TotalIonCurrent:0.###E0}");
        if (h.HasInjectionTime)
            Add(stats, "TIC (ions)", $"{IntensityConversion.Convert(h.TotalIonCurrent, h, IntensityUnits.Ions, Calibration):0.###E0}");
        if (!double.IsNaN(h.BasePeakMz))
            Add(stats, "Base peak", $"{h.BasePeakMz:0.0000} ({h.BasePeakIntensity:0.###E0})");
        if (_annotation is { } a)
        {
            Add(stats, "Document peptides", a.Precursors.Count.ToString("N0", Inv));
            Add(stats, "Signal assigned", $"{a.AssignedFraction:P1} ({a.AssignedPeaks:N0} of {h.PeakCount:N0} peaks)");
            if (h.HasInjectionTime)
                Add(stats, "Ions assigned", $"{IntensityConversion.Convert(a.AssignedIntensity, h, IntensityUnits.Ions, Calibration):0.###E0} of "
                    + $"{IntensityConversion.Convert(a.TotalIntensity, h, IntensityUnits.Ions, Calibration):0.###E0}");
        }

        foreach (var (name, value) in d.Other)
            Add("Other", name, value);
    }

    private void AddFocusedRows(PrecursorAnnotation f, Action<string, string, string?> add)
    {
        const string pep = "Peptide";
        var p = f.Precursor;
        add(pep, "Protein", p.Peptide.Protein.Name);
        add(pep, "Gene", p.Peptide.Protein.Gene);
        add(pep, "Modified sequence", p.Peptide.ModifiedSequence);
        add(pep, "Charge", p.Charge.ToString(Inv));
        add(pep, "Label", p.LabelType);
        add(pep, "Precursor m/z", p.Mz.ToString("0.0000", Inv));
        var file = CurrentResultFileName();
        if (file is not null && p.Peaks.TryGetValue(file, out var peak))
        {
            add(pep, "Retention time", $"{peak.Apex:0.00} min (peak {peak.Start:0.00} - {peak.End:0.00})");
            add(pep, "Detection q value", peak.DetectionQValue?.ToString("0.####", Inv));
            add(pep, "Library dotp (Skyline)", peak.LibraryDotProduct?.ToString("0.###", Inv));
            add(pep, "Isotope dotp", peak.IsotopeDotProduct?.ToString("0.###", Inv));
        }
        var total = _annotation?.TotalIntensity ?? 0;
        add(pep, "Ions matched", $"{f.MatchedIons} of {f.TheoreticalIons}");
        if (total > 0)
            add(pep, "Signal explained", $"{f.ExplainedIntensity / total:P1} ({f.UniqueIntensity / total:P1} not shared)");
        add(pep, "Transitions", p.Transitions.Count.ToString(Inv));
        if (!f.Reconciled)
            add(pep, "Note", "Modified sequence masses do not reproduce Skyline's precursor m/z; only document transitions are labeled.");

        if (f.Library is { } lib)
        {
            const string libCat = "Library match";
            add(libCat, "Library", lib.LibraryName);
            add(libCat, "Source file", lib.SourceFile);
            add(libCat, "ID file", lib.IdFile);
            add(libCat, "Score", lib.Score is { } sc ? $"{sc:G4}" + (lib.ScoreType is { } st ? $" ({st})" : "") : null);
            add(libCat, "Spectrum count", lib.Copies?.ToString(Inv));
            add(libCat, "Library RT", lib.RetentionTime?.ToString("0.00", Inv));
            add(libCat, "Spectrum ID", lib.SpectrumIdInFile);
            add(libCat, "Library peaks", lib.Mz.Length.ToString(Inv));
            add(libCat, "dotp vs this spectrum", f.LibraryDotProduct?.ToString("0.000", Inv));
        }
    }

    private static string AnalyzerName(MassAnalyzer analyzer) => analyzer switch
    {
        MassAnalyzer.Orbitrap => "Orbitrap",
        MassAnalyzer.Astral => "Astral",
        MassAnalyzer.IonTrap => "Ion trap",
        _ => "Other",
    };
}
