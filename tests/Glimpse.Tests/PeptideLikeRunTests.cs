using System.Diagnostics;
using Glimpse.Core.Data;
using Glimpse.Core.Scans;
using Glimpse.Features;
using Glimpse.Features.Hardklor;
using Glimpse.Pwiz;
using Xunit;
using Xunit.Abstractions;

namespace Glimpse.Tests;

/// <summary>The peptide-like signal with the real Hardklör port, by analyzer and on real files.</summary>
public class PeptideLikeRunTests
{
    private readonly ITestOutputHelper _out;

    public PeptideLikeRunTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void EachAnalyzerGetsItsPeakWidth()
    {
        Assert.Equal((HardklorInstrument.Orbitrap, 60000.0), Of(AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.Orbitrap)));
        Assert.Equal((HardklorInstrument.Tof, 80000.0), Of(AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.Astral)));
        Assert.Equal((HardklorInstrument.Orbitrap, 60000.0), Of(AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.Other)));
        Assert.Equal((HardklorInstrument.Orbitrap, 60000.0), Of(AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.Unknown)));
        // Peak widths at m/z 800: Orbitrap 800^1.5 / (20 x 60000), Astral 800 / 80000.
        Assert.Equal(0.018856, AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.Orbitrap)!.PeakWidth(800), 6);
        Assert.Equal(0.01, AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.Astral)!.PeakWidth(800), 9);
        // An ion trap cannot resolve the isotopes of multiply charged ions: not searched.
        Assert.Null(AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.IonTrap));
        var finder = new AnalyzerEnvelopeFinder(new PwizEnvelopeModel());
        Assert.False(finder.CanSearch(MassAnalyzer.IonTrap));
        Assert.True(finder.CanSearch(MassAnalyzer.Orbitrap));
        Assert.True(finder.CanSearch(MassAnalyzer.Astral));
        Assert.True(finder.CanSearch(MassAnalyzer.Unknown));

        static (HardklorInstrument, double) Of(HardklorSettings? s) => (s!.Instrument, s.Resolution);
    }

    [Fact]
    public void TheFinderUsesTheScansAnalyzer()
    {
        // A 2+ envelope whose isotopes sit 0.005 m/z off: inside the Orbitrap's peaks (0.012 wide at m/z 600),
        // outside the Astral's (0.0075).
        var mz = new[] { 600.3, 600.3 + 1.00290 / 2 + 0.005, 600.3 + 2 * 1.00290 / 2 + 0.005, 600.3 + 3 * 1.00290 / 2 + 0.005 };
        var intensity = new[] { 100000.0, 64000, 25500, 7400 };
        var finder = new AnalyzerEnvelopeFinder(new PwizEnvelopeModel());
        var orbitrap = finder.Find(mz, intensity, MassAnalyzer.Orbitrap);
        Assert.Equal(2, Assert.Single(orbitrap).Charge);
        var expected = new HardklorFinder(AnalyzerEnvelopeFinder.SettingsFor(MassAnalyzer.Orbitrap)!, new PwizEnvelopeModel()).Find(mz, intensity);
        Assert.Equal(expected.Single().MonoMass, orbitrap.Single().MonoMass);
        Assert.Equal(2, Assert.Single(finder.Find(mz, intensity, MassAnalyzer.Other)).Charge);       // Orbitrap settings
        Assert.DoesNotContain(finder.Find(mz, intensity, MassAnalyzer.Astral), e => e.Charge == 2);
        Assert.Empty(finder.Find(mz, intensity, MassAnalyzer.IonTrap));                               // not searched
    }

    [Fact]
    public void SmallRawHasPersistentMs1Envelopes()
    {
        using var source = PwizMsDataSource.Open(TestData.SmallRaw);
        using var observer = new PeptideLikeObserver(new AnalyzerEnvelopeFinder(new PwizEnvelopeModel()), workers: 4);
        var index = ScanIndexBuilder.Build(source, centroid: true, new IScanObserver[] { observer });
        Assert.True(observer.Completion.Wait(TimeSpan.FromMinutes(2)));
        var signal = observer.Completion.Result;

        // small.RAW is DDA (MS2 on a few precursors): MS1 only, and of that only the FT scans: its ion trap
        // full scans are not searched.
        var ft = index.IndicesOfLevel(1).Where(i => index[i].Analyzer != MassAnalyzer.IonTrap).ToArray();
        Assert.Equal(7, ft.Length);
        Assert.Equal(ft, signal.Series[0]);
        Assert.All(index.IndicesOfLevel(1).Except(ft), i => Assert.False(signal.Covers(i)));
        var ms1 = ft.Select(i => signal.IntensityByScan()[i]).ToArray();
        var tic = ft.Select(i => index[i].TotalIonCurrent).ToArray();
        _out.WriteLine($"{signal.Features.Count} features; MS1 peptide-like/TIC: "
            + string.Join(" ", ms1.Zip(tic, (p, t) => (p / t).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.NotEmpty(signal.Features);
        Assert.All(ms1.Zip(tic), p => Assert.InRange(p.First, 0, p.Second * 1.0000001));   // never more than the TIC
        Assert.Contains(ms1, v => v > 0);
        Assert.True(signal.IntensityByScan(ms1MinCharge: 2).Zip(signal.IntensityByScan()).All(p => double.IsNaN(p.First) || p.First <= p.Second));
    }

    /// <summary>
    /// Time on a real run: set GLIMPSE_BIG_RAW (and optionally GLIMPSE_BIG_RAW_MAX). Otherwise it does nothing.
    /// </summary>
    [Fact]
    public void BigRaw_PeptideLikeTiming()
    {
        if (TestData.BigRaw is not { } path)
        {
            _out.WriteLine("GLIMPSE_BIG_RAW not set; skipped.");
            return;
        }
        using var source = PwizMsDataSource.Open(path);
        var clock = Stopwatch.StartNew();
        using var observer = new PeptideLikeObserver(new AnalyzerEnvelopeFinder(new PwizEnvelopeModel()));
        var index = ScanIndexBuilder.Build(source, centroid: true, new IScanObserver[] { observer });
        var pass = clock.Elapsed;
        var signal = observer.Completion.GetAwaiter().GetResult();
        var total = clock.Elapsed;
        var byScan = signal.IntensityByScan();
        double Fraction(int level) => index.IndicesOfLevel(level).Where(i => !double.IsNaN(byScan[i])).Sum(i => byScan[i])
            / index.IndicesOfLevel(level).Where(i => !double.IsNaN(byScan[i])).Sum(i => index[i].TotalIonCurrent);
        _out.WriteLine($"{Path.GetFileName(path)}: {index.Count:N0} spectra, pass {pass.TotalSeconds:0.0} s, peptide-like done "
            + $"{total.TotalSeconds:0.0} s; {signal.Features.Count:N0} features in {signal.Series.Count} series (DDA {signal.IsDda}); "
            + $"peptide-like share of TIC: MS1 {Fraction(1):P1}, MS2 {Fraction(2):P1}; "
            + $"MS1 2+ only {index.IndicesOfLevel(1).Sum(i => signal.IntensityByScan(2)[i]) / index.IndicesOfLevel(1).Sum(i => index[i].TotalIonCurrent):P1}; "
            + $"managed heap {GC.GetTotalMemory(false) / 1e6:N0} MB");
    }
}
