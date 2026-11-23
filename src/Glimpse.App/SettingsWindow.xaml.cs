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

        Result = new GlimpseSettings
        {
            Figures = figures,
            IonCalibration = entries,
            PeptideLikeMs1MinCharge = PeptideLikeChargeCheck.IsChecked == true ? 2 : 1,
        }.Validated();
        DialogResult = true;
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
