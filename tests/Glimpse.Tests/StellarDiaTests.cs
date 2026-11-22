using System.Diagnostics;
using Glimpse.Core.Chromatograms;
using Glimpse.Core.Data;
using Glimpse.Core.Heatmaps;
using Glimpse.Core.Scans;
using Glimpse.Pwiz;
using Xunit;
using Xunit.Abstractions;

namespace Glimpse.Tests;

/// <summary>
/// A real DIA run: Ste-2024-12-02_HeLa_4mz_sDIA_400-900_20.mzML (Stellar, 4 m/z windows from 400 to 900,
/// centroided, 1.5 GB) from Panorama. Downloaded once into the test-data cache; skipped where it is
/// neither cached nor downloadable (no Panorama netrc entry), and in CI.
/// </summary>
public class StellarDiaTests
{
    private readonly ITestOutputHelper _out;

    public StellarDiaTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait("Category", TestData.PanoramaData)]
    public void TheScanPassReadsTheWholeStellarRun()
    {
        if (TestData.StellarHela is not { } path)
        {
            _out.WriteLine("The Stellar run is not cached and could not be downloaded (no Panorama netrc entry?); skipped.");
            return;
        }
        var clock = Stopwatch.StartNew();
        using var source = PwizMsDataSource.Open(path);
        var heatmap = new IntensityHeatmapAccumulator();
        var index = ScanIndexBuilder.Build(source, centroid: true, new IScanObserver[] { heatmap });
        _out.WriteLine($"{index.Count:N0} spectra in {clock.Elapsed.TotalSeconds:0.0} s");

        // 780 cycles of one MS1 scan and 125 MS2 windows, over 24 minutes.
        Assert.Equal("Stellar", index.Run.InstrumentModel);
        Assert.Equal(98_280, index.Count);
        Assert.Equal(780, index.IndicesOfLevel(1).Count);
        Assert.Equal(97_500, index.IndicesOfLevel(2).Count);
        Assert.Equal(0.0001087396666667, index.RtFirst, 12);
        Assert.Equal(24.026247267767, index.RtLast, 9);
        Assert.All(index.Headers, h => Assert.True(h.Centroided));

        // Every scan has an injection time, so every scan has an ion count.
        Assert.True(index.HasInjectionTimes);
        Assert.All(index.Headers, h => Assert.True(h.HasInjectionTime));

        // 125 windows, each 4 m/z wide, from 400.43 to 900.66.
        Assert.Equal(125, index.IsolationWindows.Count);
        Assert.Equal(400.433, index.IsolationWindows[0].Item1, 3);
        Assert.Equal(404.433, index.IsolationWindows[0].Item2, 3);
        Assert.Equal(896.658, index.IsolationWindows[^1].Item1, 3);
        Assert.Equal(900.658, index.IsolationWindows[^1].Item2, 3);
        Assert.All(index.IsolationWindows, w => Assert.Equal(4.0, w.Item2 - w.Item1, 3));

        var first = index[1];
        Assert.Equal(2, first.MsLevel);
        Assert.Equal(400.432800292969, first.IsolationLower, 9);
        Assert.Equal(404.432800292969, first.IsolationUpper, 9);
        Assert.Equal(13.067968, first.InjectionTimeMs, 6);
        Assert.Equal(215, first.PeakCount);
        Assert.Equal(45556.51521682739, first.TotalIonCurrent, 6);

        // The spectrum read back agrees with its header.
        var spectrum = source.ReadSpectrum(1, centroid: true);
        Assert.Equal(215, spectrum.Mz.Length);
        Assert.Equal(45556.51521682739, spectrum.Intensity.Sum(), 6);
        Assert.Equal("HCD", spectrum.Details.Dissociation);
        Assert.Contains("scan=2", spectrum.Details.NativeId);

        // Ions = intensity x injection time / 1000: the first MS1 scan has 9,849,989.79 at 1 ms.
        var ms1 = RunChromatogram.Build(index, RunTraceKind.Tic, ScanFilter.Ms1, IntensityUnits.Ions);
        Assert.Equal(780, ms1.Count);
        Assert.Equal(9849.989794586181, ms1.Values[0], 6);

        Assert.Equal(901, heatmap.Result!.Ms1.Rows);
        Assert.Equal(501, heatmap.Result.Ms2.Rows);
    }
}
