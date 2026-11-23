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
        Assert.Equal(1, GlimpseSettings.Default.PeptideLikeMs1MinCharge);   // every MS1 charge counts
        Assert.Equal(2, GlimpseSettings.Default.PeptideLikeLabelDecimals);
        var d0 = GlimpseSettings.Default;
        // Skyline's and Kronik's, except idotp 0.85 (Skyline 0.9) and 2 scans (Kronik 3).
        Assert.Equal((7, 10.0, 1, 2), (d0.HardklorMaxCharge, d0.KronikPpmTolerance, d0.KronikGapTolerance, d0.KronikMinScans));
        Assert.Equal((2, 0.85), (d0.HardklorDepth, d0.HardklorMinIdotP));
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
            PeptideLikeMs1MinCharge = 2,
            PeptideLikeLabelDecimals = 4,
            HardklorMaxCharge = 5,
            HardklorDepth = 3,
            HardklorMinIdotP = 0.85,
            KronikPpmTolerance = 7.5,
            KronikGapTolerance = 2,
            KronikMinScans = 4,
        };
        var path = File("nested/settings.json");
        GlimpseSettings.Default.Save(path);
        settings.Save(path);                    // over the earlier file
        var loaded = GlimpseSettings.Load(path);

        Assert.Equal(settings.Figures, loaded.Figures);
        Assert.Equal(settings.IonCalibration, loaded.IonCalibration);
        Assert.Equal(2, loaded.PeptideLikeMs1MinCharge);
        Assert.Equal(4, loaded.PeptideLikeLabelDecimals);
        Assert.Equal((5, 7.5, 2, 4), (loaded.HardklorMaxCharge, loaded.KronikPpmTolerance, loaded.KronikGapTolerance, loaded.KronikMinScans));
        Assert.Equal((3, 0.85), (loaded.HardklorDepth, loaded.HardklorMinIdotP));
        var text = System.IO.File.ReadAllText(path);
        Assert.Contains("\"IonTrap\"", text);   // analyzers by name, not number
        Assert.Contains("\n  \"Figures\"", text.ReplaceLineEndings("\n"));   // indented, for editing by hand
        Assert.False(System.IO.File.Exists(path + ".tmp"));
    }

    [Fact]
    public void AMissingOrUnreadableFileGivesTheDefaults()
    {
        var problems = new List<string>();
        Assert.Same(GlimpseSettings.Default, GlimpseSettings.Load(File("none.json"), problems.Add));
        Assert.Empty(problems);                                        // no file is no problem
        System.IO.File.WriteAllText(File("bad.json"), "{ not json");
        Assert.Same(GlimpseSettings.Default, GlimpseSettings.Load(File("bad.json"), problems.Add));
        // Said why, and kept a copy so a later save cannot lose the user's table.
        var problem = Assert.Single(problems);
        Assert.StartsWith("Could not read bad.json (", problem);
        Assert.EndsWith("), so Glimpse uses the default settings. The file was kept as bad.json.unreadable.", problem);
        Assert.Equal("{ not json", System.IO.File.ReadAllText(File("bad.json.unreadable")));
        Assert.Equal(File("bad.json.unreadable"), GlimpseSettings.UnreadablePath(File("bad.json")));
        // An analyzer name Glimpse does not know makes the file unreadable too.
        System.IO.File.WriteAllText(File("tof.json"), """{ "IonCalibration": [ { "Instrument": "X", "Analyzer": "TOF", "Alpha": 1 } ] }""");
        Assert.Same(GlimpseSettings.Default, GlimpseSettings.Load(File("tof.json"), problems.Add));
        Assert.Equal(2, problems.Count);
        // Read again (Glimpse started again): the earlier copy is replaced, still kept.
        GlimpseSettings.Load(File("tof.json"), problems.Add);
        Assert.EndsWith("The file was kept as tof.json.unreadable.", problems[^1]);
        // No copy possible (a folder in its place): said without claiming one.
        System.IO.File.WriteAllText(File("stuck.json"), "{ not json");
        Directory.CreateDirectory(File("stuck.json.unreadable"));
        GlimpseSettings.Load(File("stuck.json"), problems.Add);
        Assert.EndsWith("), so Glimpse uses the default settings.", problems[^1]);
        Assert.Same(GlimpseSettings.Default, GlimpseSettings.Load(File("tof.json")));   // nobody to tell: still the defaults
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
        Assert.Equal(1, s.PeptideLikeMs1MinCharge);    // not in the file: the default
        Assert.Equal(2, (GlimpseSettings.Default with { PeptideLikeMs1MinCharge = 5 }).Validated().PeptideLikeMs1MinCharge);
        Assert.Equal(1, (GlimpseSettings.Default with { PeptideLikeMs1MinCharge = 0 }).Validated().PeptideLikeMs1MinCharge);
        Assert.Equal(6, (GlimpseSettings.Default with { PeptideLikeLabelDecimals = 9 }).Validated().PeptideLikeLabelDecimals);
        Assert.Equal(0, (GlimpseSettings.Default with { PeptideLikeLabelDecimals = -1 }).Validated().PeptideLikeLabelDecimals);
        Assert.Equal(2, s.PeptideLikeLabelDecimals);    // not in the file: the default
        GlimpseSettings Clamp(GlimpseSettings x) => x.Validated();
        var low = Clamp(GlimpseSettings.Default with { HardklorMaxCharge = 0, KronikPpmTolerance = 0.01, KronikGapTolerance = -1, KronikMinScans = 0 });
        Assert.Equal((1, 0.1, 0, 1), (low.HardklorMaxCharge, low.KronikPpmTolerance, low.KronikGapTolerance, low.KronikMinScans));
        var high = Clamp(GlimpseSettings.Default with { HardklorMaxCharge = 20, KronikPpmTolerance = 500, KronikGapTolerance = 9, KronikMinScans = 50 });
        Assert.Equal((10, 100.0, 5, 20), (high.HardklorMaxCharge, high.KronikPpmTolerance, high.KronikGapTolerance, high.KronikMinScans));
        Assert.Equal(10.0, Clamp(GlimpseSettings.Default with { KronikPpmTolerance = double.NaN }).KronikPpmTolerance);
        Assert.Equal((1, 0.5), (Clamp(GlimpseSettings.Default with { HardklorDepth = 0, HardklorMinIdotP = 0.1 }).HardklorDepth,
            Clamp(GlimpseSettings.Default with { HardklorDepth = 0, HardklorMinIdotP = 0.1 }).HardklorMinIdotP));
        Assert.Equal((10, 0.99), (Clamp(GlimpseSettings.Default with { HardklorDepth = 50, HardklorMinIdotP = 2 }).HardklorDepth,
            Clamp(GlimpseSettings.Default with { HardklorDepth = 50, HardklorMinIdotP = 2 }).HardklorMinIdotP));
        Assert.Equal(0.85, Clamp(GlimpseSettings.Default with { HardklorMinIdotP = double.NaN }).HardklorMinIdotP);
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
