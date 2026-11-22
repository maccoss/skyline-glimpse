using Glimpse.Core.Data;
using Glimpse.Plotting;
using Xunit;

namespace Glimpse.Tests;

/// <summary>Tests that change <see cref="FigureStyle.Current"/> run alone: every plot test reads it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FigureStyleCollection
{
    public const string Name = "FigureStyle";
}

[Collection(FigureStyleCollection.Name)]
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("glimpse-settings").FullName;

    public void Dispose()
    {
        FigureStyle.Current = FigureSettings.Default;
        Directory.Delete(_dir, recursive: true);
    }

    private string File(string name) => Path.Combine(_dir, name);

    [Fact]
    public void TheDefaultsAreTodaysSizesAndThePublishedTable()
    {
        var d = FigureSettings.Default;
        Assert.Equal(
            new[] { 18f, 16f, 15f, 14f, 2f, 8f, 2f, 4f, 1.5f, 2f, 2.5f, 1.5f, 2.5f },
            new[]
            {
                d.AxisLabelFontSize, d.TickLabelFontSize, d.LegendFontSize, d.PeakLabelFontSize, d.FrameWidth,
                d.MajorTickLength, d.MajorTickWidth, d.MinorTickLength, d.MinorTickWidth, d.TraceWidth, d.XicWidth,
                d.StickWidth, d.AssignedStickWidth,
            });
        Assert.True(d.ScaleWithPlotSize);
        Assert.Same(IonCalibrationTable.Published, GlimpseSettings.Default.IonCalibration);
        Assert.EndsWith(Path.Combine("SkylineGlimpse", "settings.json"), GlimpseSettings.DefaultPath);
    }

    [Fact]
    public void SettingsSurviveASaveAndLoad()
    {
        var settings = new GlimpseSettings
        {
            Figures = FigureSettings.Default with { AxisLabelFontSize = 24, TraceWidth = 3.5f, ScaleWithPlotSize = false },
            IonCalibration = new[] { new AlphaEntry("Orbitrap Astral", MassAnalyzer.Astral, 1.4), new AlphaEntry("Stellar", MassAnalyzer.IonTrap, 1.0) },
        };
        var path = File("nested/settings.json");
        GlimpseSettings.Default.Save(path);
        settings.Save(path);                    // over the earlier file
        var loaded = GlimpseSettings.Load(path);

        Assert.Equal(settings.Figures, loaded.Figures);
        Assert.Equal(settings.IonCalibration, loaded.IonCalibration);
        var text = System.IO.File.ReadAllText(path);
        Assert.Contains("\"IonTrap\"", text);   // analyzers by name, not number
        Assert.Contains("\n  \"Figures\"", text.ReplaceLineEndings("\n"));   // indented, for editing by hand
        Assert.False(System.IO.File.Exists(path + ".tmp"));
    }

    [Fact]
    public void AMissingOrUnreadableFileGivesTheDefaults()
    {
        Assert.Same(GlimpseSettings.Default, GlimpseSettings.Load(File("none.json")));
        System.IO.File.WriteAllText(File("bad.json"), "{ not json");
        Assert.Same(GlimpseSettings.Default, GlimpseSettings.Load(File("bad.json")));
        System.IO.File.WriteAllText(File("null.json"), "null");
        Assert.Same(GlimpseSettings.Default, GlimpseSettings.Load(File("null.json")));
        Assert.Equal("{ not json", System.IO.File.ReadAllText(File("bad.json")));   // left alone
    }

    [Fact]
    public void AHandEditedFileIsBroughtIntoRange()
    {
        System.IO.File.WriteAllText(File("edited.json"), """
            {
              "Figures": { "AxisLabelFontSize": 500, "TickLabelFontSize": 1, "FrameWidth": 0, "TraceWidth": 100 },
              "IonCalibration": [
                { "Instrument": "  Orbitrap Astral ", "Analyzer": "Astral", "Alpha": 1.5 },
                { "Instrument": "", "Analyzer": "Orbitrap", "Alpha": 10 },
                { "Instrument": "Stellar", "Analyzer": "IonTrap", "Alpha": -1 },
                { "Instrument": "Stellar", "Analyzer": "IonTrap", "Alpha": 0 }
              ]
            }
            """);
        var s = GlimpseSettings.Load(File("edited.json"));
        Assert.Equal(48f, s.Figures.AxisLabelFontSize);
        Assert.Equal(6f, s.Figures.TickLabelFontSize);
        Assert.Equal(0.5f, s.Figures.FrameWidth);
        Assert.Equal(20f, s.Figures.TraceWidth);
        Assert.Equal(15f, s.Figures.LegendFontSize);    // not in the file: the default
        Assert.True(s.Figures.ScaleWithPlotSize);
        Assert.Equal(new[] { new AlphaEntry("Orbitrap Astral", MassAnalyzer.Astral, 1.5) }, s.IonCalibration);

        System.IO.File.WriteAllText(File("notable.json"), """{ "IonCalibration": null }""");
        Assert.Same(IonCalibrationTable.Published, GlimpseSettings.Load(File("notable.json")).IonCalibration);
    }

    [Fact]
    public void AnUnreadableSizeGoesBackToItsDefault()
    {
        var bad = FigureSettings.Default with
        {
            AxisLabelFontSize = float.NaN, TickLabelFontSize = float.PositiveInfinity, LegendFontSize = float.NaN,
            PeakLabelFontSize = float.NaN, FrameWidth = float.NaN, MajorTickLength = float.NaN, MajorTickWidth = float.NaN,
            MinorTickLength = float.NaN, MinorTickWidth = float.NaN, TraceWidth = float.NaN, XicWidth = float.NaN,
            StickWidth = float.NaN, AssignedStickWidth = float.NaN,
        };
        Assert.Equal(FigureSettings.Default, bad.Validated());
        var wide = (FigureSettings.Default with
        {
            LegendFontSize = 100, PeakLabelFontSize = 2, MajorTickLength = 50, MajorTickWidth = 0.1f, MinorTickLength = 0,
            MinorTickWidth = 40, XicWidth = 0.2f, StickWidth = 30, AssignedStickWidth = 0.1f,
        }).Validated();
        Assert.Equal(new[] { 48f, 6f, 20f, 0.5f, 0.5f, 20f, 0.5f, 20f, 0.5f },
            new[] { wide.LegendFontSize, wide.PeakLabelFontSize, wide.MajorTickLength, wide.MajorTickWidth, wide.MinorTickLength,
                wide.MinorTickWidth, wide.XicWidth, wide.StickWidth, wide.AssignedStickWidth });
    }

    [Fact]
    public void PlotsDrawWithTheCurrentSettings()
    {
        FigureStyle.Current = FigureSettings.Default with { TraceWidth = 3, XicWidth = 5, TickLabelFontSize = 20, StickWidth = 2, ScaleWithPlotSize = false };
        Assert.Equal(3f, FigureStyle.TraceWidth);
        Assert.Equal(20f, FigureStyle.TickLabelFontSize);
        Assert.Equal(new[] { 5f, 5f, 3f, 3f, 2f }, new[] { 1, 20, 21, 200, 201 }.Select(FigureStyle.XicWidth));
        Assert.Equal(2f, new StickPlottable(new[] { 100.0 }, new[] { 1.0 }).StickWidth);
        // Not growing with the plot: always 1.
        Assert.Equal(1.0, FigureStyle.SizeScale(2000, 800));

        var plot = new ScottPlot.Plot();
        FigureStyle.Apply(plot, "x", "y");
        Assert.Equal(20f, plot.Axes.Bottom.TickLabelStyle.FontSize);
    }
}
