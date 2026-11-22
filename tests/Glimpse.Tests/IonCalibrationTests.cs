using Glimpse.Core.Assignment;
using Glimpse.Core.Chromatograms;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Scans;
using Xunit;

namespace Glimpse.Tests;

/// <summary>Ions divided by the α of the analyzer that recorded each scan (Hsu et al. 2025, Table 1).</summary>
public class IonCalibrationTests
{
    [Fact]
    public void ThePublishedTableIsTable1OfThePaper()
    {
        var expected = new (string, MassAnalyzer, double)[]
        {
            ("Orbitrap Astral Zoom", MassAnalyzer.Astral, 1.31),
            ("Orbitrap Astral", MassAnalyzer.Astral, 1.53),
            ("Orbitrap Astral Zoom", MassAnalyzer.Orbitrap, 10.30),
            ("Orbitrap Astral", MassAnalyzer.Orbitrap, 10.62),
            ("Orbitrap Exploris 480", MassAnalyzer.Orbitrap, 10.95),
            ("Orbitrap Ascend", MassAnalyzer.Orbitrap, 9.40),
            ("Orbitrap Fusion Lumos", MassAnalyzer.Orbitrap, 15.44),
            ("Stellar", MassAnalyzer.IonTrap, 0.97),
            ("Orbitrap Ascend", MassAnalyzer.IonTrap, 0.98),
            ("Orbitrap Fusion Lumos", MassAnalyzer.IonTrap, 1.09),
        };
        Assert.Equal(expected, IonCalibrationTable.Published.Select(e => (e.Instrument, e.Analyzer, e.Alpha)));
    }

    [Fact]
    public void AnInstrumentGetsTheAlphaOfEachOfItsAnalyzers()
    {
        var astral = IonCalibration.For("Orbitrap Astral", IonCalibrationTable.Published);
        Assert.Equal(new IonCalibration(10.62, 1.53, double.NaN), astral);
        Assert.Equal(1.53, astral.AlphaFor(MassAnalyzer.Astral));
        Assert.Equal(10.62, astral.Divisor(MassAnalyzer.Orbitrap));
        Assert.False(astral.IsCalibrated(MassAnalyzer.IonTrap));
        Assert.Equal(1, astral.Divisor(MassAnalyzer.IonTrap));       // no α: uncalibrated
        Assert.Equal(1, astral.Divisor(MassAnalyzer.Unknown));
        Assert.Equal(double.NaN, astral.AlphaFor(MassAnalyzer.Other));
        Assert.True(astral.Any);
        Assert.Equal("Orbitrap α 10.62, Astral α 1.53", astral.Describe());

        // The Astral Zoom is a different instrument from the Astral, not a prefix match.
        Assert.Equal(new IonCalibration(10.30, 1.31, double.NaN), IonCalibration.For("Orbitrap Astral Zoom", IonCalibrationTable.Published));
        // Names match ignoring case and surrounding spaces.
        Assert.Equal(new IonCalibration(double.NaN, double.NaN, 0.97), IonCalibration.For("  stellar ", IonCalibrationTable.Published));
        Assert.Equal("ion trap α 0.97", IonCalibration.For("Stellar", IonCalibrationTable.Published).Describe());
    }

    [Fact]
    public void AnInstrumentNotInTheTableStaysUncalibrated()
    {
        Assert.Equal(IonCalibration.Uncalibrated, IonCalibration.For("Q Exactive HF", IonCalibrationTable.Published));
        Assert.Equal(IonCalibration.Uncalibrated, IonCalibration.For(null, IonCalibrationTable.Published));
        Assert.Equal(IonCalibration.Uncalibrated, IonCalibration.For("  ", IonCalibrationTable.Published));
        Assert.False(IonCalibration.Uncalibrated.Any);
        // An unknown instrument uses α 1 for every analyzer: ions are intensity x injection time / 1000.
        var unknown = IonCalibration.For("Thermo Electron instrument model", IonCalibrationTable.Published);
        Assert.Equal(new[] { 1.0, 1.0, 1.0, 1.0, 1.0 },
            new[] { MassAnalyzer.Orbitrap, MassAnalyzer.Astral, MassAnalyzer.IonTrap, MassAnalyzer.Other, MassAnalyzer.Unknown }.Select(unknown.Divisor));
        var scan = FakeSource.Spectrum(0, 2, 1, new[] { 500.0 }, new[] { 400.0 }, 5f, 499, 501).Header with { Analyzer = MassAnalyzer.Orbitrap };
        Assert.Equal(2.0, IntensityConversion.Convert(400, scan, IntensityUnits.Ions, unknown));
        Assert.Null(IonCalibration.Uncalibrated.Describe());
        // A row with an unusable α counts as no row.
        var table = new[] { new AlphaEntry("X", MassAnalyzer.Orbitrap, 0), new AlphaEntry("X", MassAnalyzer.Astral, double.NaN) };
        Assert.Equal(IonCalibration.Uncalibrated, IonCalibration.For("X", table));
        // The first usable row of an analyzer wins.
        var two = new[] { new AlphaEntry("X", MassAnalyzer.Orbitrap, -1), new AlphaEntry("X", MassAnalyzer.Orbitrap, 4), new AlphaEntry("X", MassAnalyzer.Orbitrap, 5) };
        Assert.Equal(4, IonCalibration.For("X", two).Orbitrap);
    }

    [Fact]
    public void OnlyAPositiveAlphaCalibrates()
    {
        var c = new IonCalibration(0, -1, 2.3456);
        Assert.False(c.IsCalibrated(MassAnalyzer.Orbitrap));        // α 0 is no α
        Assert.Equal(1, c.Divisor(MassAnalyzer.Orbitrap));
        Assert.False(c.IsCalibrated(MassAnalyzer.Astral));
        Assert.True(c.IsCalibrated(MassAnalyzer.IonTrap));
        Assert.Equal("ion trap α 2.35", c.Describe());              // the others left out, two decimals
        Assert.True(c.Any);                                         // the ion trap alone
        Assert.True(new IonCalibration(10, double.NaN, double.NaN).Any);   // the Orbitrap alone
        Assert.True(new IonCalibration(double.NaN, 1.5, double.NaN).Any);  // the Astral alone
    }

    [Fact]
    public void IonsAreDividedByTheScansAnalyzerAlpha()
    {
        var calibration = new IonCalibration(10, 2, double.NaN);
        var astralScan = FakeSource.Spectrum(0, 2, 1, new[] { 500.0 }, new[] { 400.0 }, 5f, 499, 501).Header with { Analyzer = MassAnalyzer.Astral };
        var trapScan = astralScan with { Analyzer = MassAnalyzer.IonTrap };
        // 400 x 5 ms / 1000 = 2 uncalibrated ions.
        Assert.Equal(1.0, IntensityConversion.Convert(400, astralScan, IntensityUnits.Ions, calibration));
        Assert.Equal(2.0, IntensityConversion.Convert(400, trapScan, IntensityUnits.Ions, calibration));   // no ion trap α
        Assert.Equal(2.0, IntensityConversion.Convert(400, astralScan, IntensityUnits.Ions, null));
        Assert.Equal(400.0, IntensityConversion.Convert(400, astralScan, IntensityUnits.AsRecorded, calibration));
        Assert.Equal(double.NaN, IntensityConversion.Convert(400, astralScan with { InjectionTimeMs = float.NaN }, IntensityUnits.Ions, calibration));
    }

    [Fact]
    public void ChromatogramsAndXicsApplyTheCalibration()
    {
        // MS1 from the Orbitrap (α 10), MS2 from the Astral (α 2), 10 ms injection times.
        var source = new FakeSource()
            .Ms1(1.0, new[] { 405.0 }, new[] { 1000.0 }, 10f, MassAnalyzer.Orbitrap)
            .Ms2(1.1, 400, 410, new[] { 300.0 }, new[] { 500.0 }, 10f, MassAnalyzer.Astral);
        var index = ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>());
        var calibration = new IonCalibration(10, 2, double.NaN);

        Assert.Equal(new[] { 1.0 }, RunChromatogram.Build(index, RunTraceKind.Tic, ScanFilter.Ms1, IntensityUnits.Ions, calibration).Values);
        Assert.Equal(new[] { 2.5 }, RunChromatogram.Build(index, RunTraceKind.Tic, ScanFilter.Ms2, IntensityUnits.Ions, calibration).Values);
        Assert.Equal(new[] { 10.0 }, RunChromatogram.Build(index, RunTraceKind.Tic, ScanFilter.Ms1, IntensityUnits.Ions).Values);

        Assert.Equal(MassAnalyzer.Orbitrap, index.AnalyzerOf(1));
        Assert.Equal(MassAnalyzer.Astral, index.AnalyzerOf(2));
        Assert.Equal(MassAnalyzer.Unknown, index.AnalyzerOf(3));

        var protein = new DocProtein("P", null, null, "MoleculeGroup:/P");
        var precursor = Docs.Precursor(protein, "AAAK", 2, 405.0, "run1.raw", 0.5, 1.5, ("precursor", 2, 405.0), ("y2", 1, 300.0));
        var observer = new DocumentSignalObserver(Docs.Model(protein), "run1.raw", DocumentSettings.Default);
        index = ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        var peptides = new[] { precursor.Peptide };
        var ms1 = new XicRequest(index, observer.Result!, peptides, XicMode.Summed, true, IntensityUnits.Ions, false, calibration).Build();
        var ms2 = new XicRequest(index, observer.Result!, peptides, XicMode.Summed, false, IntensityUnits.Ions, false, calibration).Build();
        Assert.Equal(new[] { 1.0 }, Assert.Single(ms1).Values);     // 1000 x 10 / 1000 / 10
        Assert.Equal(new[] { 2.5 }, Assert.Single(ms2).Values);     // 500 x 10 / 1000 / 2
    }

    [Fact]
    public void AnAnalyzerMixWithinALevelHasNoSingleAnalyzer()
    {
        var source = new FakeSource()
            .Ms2(1.0, 400, 410, new[] { 300.0 }, new[] { 5.0 }, 10f, MassAnalyzer.IonTrap)
            .Ms2(1.1, 400, 410, new[] { 300.0 }, new[] { 5.0 }, 10f, MassAnalyzer.Orbitrap);
        var index = ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>());
        Assert.Equal(MassAnalyzer.Unknown, index.AnalyzerOf(2));
    }

    [Fact]
    public void ACalibrationChangeIsANewXicRequest()
    {
        var source = new FakeSource().Ms1(1.0, new[] { 405.0 }, new[] { 1000.0 });
        var protein = new DocProtein("P", null, null, "MoleculeGroup:/P");
        var precursor = Docs.Precursor(protein, "AAAK", 2, 405.0, "run1.raw", 0.5, 1.5, ("precursor", 2, 405.0));
        var observer = new DocumentSignalObserver(Docs.Model(protein), "run1.raw", DocumentSettings.Default);
        var index = ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        var peptides = new[] { precursor.Peptide };
        XicRequest Request(IonCalibration? c) => new(index, observer.Result!, peptides, XicMode.Summed, true, IntensityUnits.Ions, false, c);

        Assert.Equal(Request(new IonCalibration(10, 2, 1)), Request(new IonCalibration(10, 2, 1)));
        Assert.Equal(Request(new IonCalibration(10, 2, 1)).GetHashCode(), Request(new IonCalibration(10, 2, 1)).GetHashCode());
        Assert.NotEqual(Request(new IonCalibration(10, 2, 1)), Request(new IonCalibration(11, 2, 1)));
        Assert.NotEqual(Request(null), Request(new IonCalibration(10, 2, 1)));
    }
}
