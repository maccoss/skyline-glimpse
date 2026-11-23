using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Glimpse.Core.Data;
using Glimpse.Plotting;

namespace Glimpse.App;

/// <summary>Plot sizes and the ion calibration table, edited together and saved by the main window.</summary>
public partial class SettingsWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>One editable figure size: its label, how to read it, how to set it.</summary>
    private sealed record FigureField(string Label, Func<FigureSettings, float> Get, Func<FigureSettings, float, FigureSettings> Set);

    private static readonly FigureField[] Fields =
    {
        new("Axis title font size", f => f.AxisLabelFontSize, (f, v) => f with { AxisLabelFontSize = v }),
        new("Tick label font size", f => f.TickLabelFontSize, (f, v) => f with { TickLabelFontSize = v }),
        new("Legend font size", f => f.LegendFontSize, (f, v) => f with { LegendFontSize = v }),
        new("Spectrum peak label font size", f => f.PeakLabelFontSize, (f, v) => f with { PeakLabelFontSize = v }),
        new("Axis frame thickness", f => f.FrameWidth, (f, v) => f with { FrameWidth = v }),
        new("Major tick length", f => f.MajorTickLength, (f, v) => f with { MajorTickLength = v }),
        new("Major tick thickness", f => f.MajorTickWidth, (f, v) => f with { MajorTickWidth = v }),
        new("Minor tick length", f => f.MinorTickLength, (f, v) => f with { MinorTickLength = v }),
        new("Minor tick thickness", f => f.MinorTickWidth, (f, v) => f with { MinorTickWidth = v }),
        new("TIC and base peak line width", f => f.TraceWidth, (f, v) => f with { TraceWidth = v }),
        new("XIC line width (a few traces)", f => f.XicWidth, (f, v) => f with { XicWidth = v }),
        new("Spectrum stick width", f => f.StickWidth, (f, v) => f with { StickWidth = v }),
        new("Assigned peak stick width", f => f.AssignedStickWidth, (f, v) => f with { AssignedStickWidth = v }),
    };

    private readonly TextBox[] _boxes;
    private readonly ObservableCollection<AlphaRow> _alphas = new();

    public SettingsWindow(GlimpseSettings settings)
    {
        InitializeComponent();
        Result = settings;
        _boxes = new TextBox[Fields.Length];
        for (var i = 0; i < Fields.Length; i++)
        {
            FigureGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = Fields[i].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3) };
            var box = new TextBox { Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(label, i);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            FigureGrid.Children.Add(label);
            FigureGrid.Children.Add(box);
            _boxes[i] = box;
        }
        ShowFigures(settings.Figures);

        AnalyzerColumn.ItemsSource = new[] { MassAnalyzer.Orbitrap, MassAnalyzer.Astral, MassAnalyzer.IonTrap };
        ShowAlphas(settings.IonCalibration);
        AlphaGrid.ItemsSource = _alphas;
        PeptideLikeChargeCheck.IsChecked = settings.PeptideLikeMs1MinCharge >= 2;
        LabelDecimalsBox.Text = settings.PeptideLikeLabelDecimals.ToString(Inv);
        MaxChargeBox.Text = settings.HardklorMaxCharge.ToString(Inv);
        PpmBox.Text = settings.KronikPpmTolerance.ToString("0.###", Inv);
        GapBox.Text = settings.KronikGapTolerance.ToString(Inv);
        MinScansBox.Text = settings.KronikMinScans.ToString(Inv);
    }

    /// <summary>The settings as edited, once OK is pressed.</summary>
    public GlimpseSettings Result { get; private set; }

    private void ShowFigures(FigureSettings figures)
    {
        for (var i = 0; i < Fields.Length; i++)
            _boxes[i].Text = Fields[i].Get(figures).ToString("0.##", Inv);
        ScaleWithSizeCheck.IsChecked = figures.ScaleWithPlotSize;
    }

    private void ShowAlphas(IEnumerable<AlphaEntry> entries)
    {
        _alphas.Clear();
        foreach (var e in entries)
            _alphas.Add(new AlphaRow { Instrument = e.Instrument, Analyzer = e.Analyzer, Alpha = e.Alpha.ToString("0.###", Inv) });
    }

    private void OnRestoreFigureDefaults(object sender, RoutedEventArgs e) => ShowFigures(FigureSettings.Default);

    private void OnRestorePublishedAlphas(object sender, RoutedEventArgs e) => ShowAlphas(IonCalibrationTable.Published);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        AlphaGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var figures = FigureSettings.Default with { ScaleWithPlotSize = ScaleWithSizeCheck.IsChecked == true };
        for (var i = 0; i < Fields.Length; i++)
        {
            if (!float.TryParse(_boxes[i].Text, NumberStyles.Float, Inv, out var v) || !float.IsFinite(v) || v <= 0)
            {
                Refuse($"\"{Fields[i].Label}\" needs a positive number (for example 2 or 1.5).", _boxes[i]);
                return;
            }
            figures = Fields[i].Set(figures, v);
        }

        var entries = new List<AlphaEntry>();
        foreach (var row in _alphas)
        {
            if (string.IsNullOrWhiteSpace(row.Instrument) && string.IsNullOrWhiteSpace(row.Alpha))
                continue;   // an empty row the grid added
            if (string.IsNullOrWhiteSpace(row.Instrument)
                || !double.TryParse(row.Alpha, NumberStyles.Float, Inv, out var alpha) || !double.IsFinite(alpha) || alpha <= 0)
            {
                Refuse($"Each calibration row needs an instrument name and a positive α (row \"{row.Instrument}\", α \"{row.Alpha}\").", null);
                return;
            }
            entries.Add(new AlphaEntry(row.Instrument.Trim(), row.Analyzer, alpha));
        }
        // Only the first row of an instrument and analyzer would count, so a second is refused, not ignored.
        if (IonCalibrationTable.FirstDuplicate(entries) is { } twice)
        {
            Refuse($"\"{twice.Instrument}\" has two {twice.Analyzer} rows. Keep one α per instrument and analyzer.", null);
            return;
        }

        if (!int.TryParse(LabelDecimalsBox.Text, NumberStyles.Integer, Inv, out var decimals) || decimals < 0 || decimals > GlimpseSettings.MaxLabelDecimals)
        {
            Refuse($"The envelope label decimals need a whole number from 0 to {GlimpseSettings.MaxLabelDecimals}.", LabelDecimalsBox);
            return;
        }

        if (!TryWhole(MaxChargeBox, 1, GlimpseSettings.MaxHardklorCharge, "The highest charge state", out var maxCharge)
            || !TryWhole(GapBox, 0, GlimpseSettings.MaxGapTolerance, "The missed scans allowed", out var gap)
            || !TryWhole(MinScansBox, 1, GlimpseSettings.MaxMinScans, "The fewest scans", out var minScans))
            return;
        if (!double.TryParse(PpmBox.Text, NumberStyles.Float, Inv, out var ppm) || !double.IsFinite(ppm)
            || ppm < GlimpseSettings.MinPpm || ppm > GlimpseSettings.MaxPpm)
        {
            Refuse($"The mass tolerance needs a number of ppm from {GlimpseSettings.MinPpm.ToString(Inv)} to {GlimpseSettings.MaxPpm.ToString(Inv)}.", PpmBox);
            return;
        }

        Result = new GlimpseSettings
        {
            Figures = figures,
            IonCalibration = entries,
            PeptideLikeMs1MinCharge = PeptideLikeChargeCheck.IsChecked == true ? 2 : 1,
            PeptideLikeLabelDecimals = decimals,
            HardklorMaxCharge = maxCharge,
            KronikPpmTolerance = ppm,
            KronikGapTolerance = gap,
            KronikMinScans = minScans,
        }.Validated();
        DialogResult = true;
    }

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/> in <paramref name="box"/>, or a refusal naming it.</summary>
    private bool TryWhole(TextBox box, int min, int max, string what, out int value)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, Inv, out value) && value >= min && value <= max)
            return true;
        Refuse($"{what} needs a whole number from {min} to {max}.", box);
        return false;
    }

    private void Refuse(string message, TextBox? focus)
    {
        MessageBox.Show(this, message, "Glimpse settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        focus?.Focus();
    }

    /// <summary>One editable row of the calibration table (α kept as text so a half-typed number is not lost).</summary>
    public sealed class AlphaRow
    {
        public string Instrument { get; set; } = "";
        public MassAnalyzer Analyzer { get; set; } = MassAnalyzer.Orbitrap;
        public string Alpha { get; set; } = "";
    }
}
