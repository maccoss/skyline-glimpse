using Glimpse.Core.Chromatograms;
using Glimpse.Core.Data;
using Glimpse.Core.Scans;
using Xunit;

namespace Glimpse.Tests;

public class ScanIndexTests
{
    /// <summary>MS1 at 0, 1, 2 ...; two DIA windows (400-410, 410-420) after each MS1.</summary>
    private static (FakeSource Source, ScanIndex Index) Dia(int cycles = 4)
    {
        var source = new FakeSource();
        for (var c = 0; c < cycles; c++)
        {
            source.Ms1(c, new[] { 500.0 }, new[] { 100.0 + c }, injectionMs: c == 1 ? float.NaN : 10f);
            source.Ms2(c + 0.3, 400, 410, new[] { 300.0, 301.0 }, new[] { 5.0, 7.0 });
            source.Ms2(c + 0.6, 410, 420, new[] { 350.0 }, new[] { 3.0 });
        }
        return (source, ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>()));
    }

    [Fact]
    public void IndexesLevelsWindowsAndRange()
    {
        var (source, index) = Dia();
        Assert.Equal(12, index.Count);
        Assert.Equal(12, source.Reads);
        Assert.Equal(new[] { 0, 3, 6, 9 }, index.IndicesOfLevel(1));
        Assert.Equal(8, index.IndicesOfLevel(2).Count);
        Assert.Empty(index.IndicesOfLevel(3));
        Assert.Empty(index.IndicesOfLevel(-1));
        Assert.Equal(new[] { (400.0, 410.0), (410.0, 420.0) }, index.IsolationWindows);
        Assert.Equal(0, index.RtFirst);
        Assert.Equal(3.6, index.RtLast, 9);
        Assert.True(index.HasInjectionTimes);
    }

    [Fact]
    public void NearestScanHonorsTheFilter()
    {
        var (_, index) = Dia();
        Assert.Equal(3, index.NearestScan(1.1, ScanFilter.Ms1));
        Assert.Equal(0, index.NearestScan(-5, ScanFilter.Ms1));
        Assert.Equal(9, index.NearestScan(99, ScanFilter.Ms1));
        Assert.Equal(4, index.NearestScan(1.25, ScanFilter.Ms2));
        // Window filter: the nearest scan of the right window, not the nearest MS2.
        Assert.Equal(5, index.NearestScan(1.3, ScanFilter.Ms2Containing(415)));
        Assert.Equal(4, index.NearestScan(1.6, ScanFilter.Ms2Window(400, 410)));
        Assert.Equal(-1, index.NearestScan(1, ScanFilter.Ms2Containing(900)));
        Assert.Equal(1, index.NearestScan(0.31, ScanFilter.All));
    }

    [Fact]
    public void StepScanSkipsOtherKinds()
    {
        var (_, index) = Dia();
        Assert.Equal(3, index.StepScan(0, +1, ScanFilter.Ms1));
        Assert.Equal(0, index.StepScan(3, -1, ScanFilter.Ms1));
        Assert.Equal(-1, index.StepScan(9, +1, ScanFilter.Ms1));
        Assert.Equal(-1, index.StepScan(0, -1, ScanFilter.Ms1));
        Assert.Equal(4, index.StepScan(1, +1, ScanFilter.Ms2Window(400, 410)));
        Assert.Equal(2, index.StepScan(1, +1, ScanFilter.All));
    }

    [Fact]
    public void ScanFilterAcceptsAndDescribes()
    {
        var ms2 = FakeSource.Spectrum(0, 2, 1, Array.Empty<double>(), Array.Empty<double>(), 1, 400, 410).Header;
        var ms1 = FakeSource.Spectrum(0, 1, 1, Array.Empty<double>(), Array.Empty<double>(), 1, double.NaN, double.NaN).Header;
        Assert.True(ScanFilter.All.Accepts(ms1));
        Assert.True(ScanFilter.Ms1.Accepts(ms1));
        Assert.False(ScanFilter.Ms1.Accepts(ms2));
        Assert.True(ScanFilter.Ms2Containing(400).Accepts(ms2));
        Assert.False(ScanFilter.Ms2Containing(410).Accepts(ms2)); // half-open, like Skyline
        Assert.False(ScanFilter.Ms2Containing(405).Accepts(ms1));
        Assert.True(ScanFilter.Ms2Window(400.0005, 409.9995).Accepts(ms2));
        Assert.False(ScanFilter.Ms2Window(400, 411).Accepts(ms2));
        Assert.Equal("All scans", ScanFilter.All.Describe());
        Assert.Equal("MS1", ScanFilter.Ms1.Describe());
        Assert.Equal("MS2 400-410", ScanFilter.Ms2Window(400, 410).Describe());
        Assert.Equal("MS2 containing 405", ScanFilter.Ms2Containing(405).Describe());
    }

    [Fact]
    public void RunChromatogramTicBasePeakAndIons()
    {
        var (_, index) = Dia();
        var tic = RunChromatogram.Build(index, RunTraceKind.Tic, ScanFilter.Ms2Window(400, 410), IntensityUnits.AsRecorded);
        Assert.Equal(4, tic.Count);
        Assert.All(tic.Values, v => Assert.Equal(12.0, v, 9));
        Assert.Equal(new[] { 1, 4, 7, 10 }, tic.ScanIndices);
        Assert.Equal(12.0, tic.MaxValue, 9);

        var bpc = RunChromatogram.Build(index, RunTraceKind.BasePeak, ScanFilter.Ms2Window(400, 410), IntensityUnits.AsRecorded);
        Assert.All(bpc.Values, v => Assert.Equal(7.0, v, 9));
        Assert.StartsWith("BPC", bpc.Name);

        // Ions: intensity x injection time; the scan without an injection time drops out.
        var ions = RunChromatogram.Build(index, RunTraceKind.Tic, ScanFilter.Ms1, IntensityUnits.Ions);
        Assert.Equal(new[] { 0.0, 2.0, 3.0 }, ions.Times);
        Assert.Equal(100 * 0.01, ions.Values[0], 9);
        Assert.Equal(4, RunChromatogram.Build(index, RunTraceKind.Tic, ScanFilter.Ms1, IntensityUnits.AsRecorded).Count);
    }

    [Fact]
    public void IntensityConversionNeedsInjectionTime()
    {
        Assert.Equal(5.0, IntensityConversion.Convert(5, float.NaN, IntensityUnits.AsRecorded));
        Assert.Equal(0.05, IntensityConversion.Convert(5, 10f, IntensityUnits.Ions), 12);
        Assert.True(double.IsNaN(IntensityConversion.Convert(5, float.NaN, IntensityUnits.Ions)));
        Assert.True(double.IsNaN(IntensityConversion.Convert(5, 0f, IntensityUnits.Ions)));
        Assert.Equal("Ions", IntensityConversion.Label(IntensityUnits.Ions));
        Assert.Equal("Intensity", IntensityConversion.Label(IntensityUnits.AsRecorded));
    }

    [Fact]
    public void BuildReportsProgressAndCancels()
    {
        var (source, _) = Dia(100);
        var reports = new List<double>();
        ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>(), new SyncProgress(reports));
        Assert.Equal(1.0, reports[^1]);
        Assert.True(reports.Count >= 50);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>(), null, cts.Token));
    }

    [Fact]
    public void PreviewsShowTheSpectraReadSoFar()
    {
        // 50 cycles = 150 spectra, at 0, 0.3, 0.6, 1, ... 49.6 min: a preview every 150 / 25 = 6 spectra.
        var (source, _) = Dia(50);
        var previews = new List<ScanPassPreview>();
        var full = ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>(), preview: new SyncProgress<ScanPassPreview>(previews));
        Assert.Equal(24, previews.Count);                     // the 25th would be the finished run itself
        Assert.Equal(6, previews[0].SoFar.Count);
        Assert.Equal(new[] { 6, 12, 18 }, previews.Take(3).Select(p => p.SoFar.Count));
        Assert.Equal(144, previews[^1].SoFar.Count);
        Assert.Equal(150, full.Count);
        // Each preview knows the whole run's range, and how far through it the pass is.
        Assert.All(previews, p => Assert.Equal((0.0, 49.6), (p.RtFirst, p.RtLast)));
        Assert.Equal(1.6 / 49.6, previews[0].Fraction, 9);   // 6 spectra: the last at 1.6 min
        Assert.Equal(1, previews[0].SoFar.IndicesOfLevel(1)[^1] / 3);
        Assert.Equal(0.06, new ScanPassPreview(previews[0].SoFar, 1, 11).Fraction, 9);   // (1.6 - 1) / (11 - 1)
        Assert.Equal(0, new ScanPassPreview(previews[0].SoFar, 0, 0).Fraction);          // no time range
        Assert.Equal(0, new ScanPassPreview(previews[0].SoFar, 3, 2).Fraction);
        Assert.Equal(1, new ScanPassPreview(previews[^1].SoFar, 0, 10).Fraction);       // clamped
        Assert.Equal(0, new ScanPassPreview(previews[0].SoFar, 2, 11).Fraction);        // before the range: clamped
        var noTimes = new ScanIndex("x", previews[0].SoFar.Run, Array.Empty<ScanHeader>());
        Assert.Equal(0, new ScanPassPreview(noTimes, 0, 10).Fraction);                   // nothing read yet
    }

    [Fact]
    public void SpectrumLookups()
    {
        var s = FakeSource.Spectrum(0, 2, 1, new[] { 100.0, 100.01, 200.0 }, new[] { 1.0, 3.0, 2.0 }, 1, 0, 1);
        Assert.Equal(1, s.MostIntenseIn(99.99, 100.02));
        Assert.Equal(-1, s.MostIntenseIn(150, 160));
        Assert.Equal(4.0, s.SumIn(99, 101), 9);
        Assert.Equal(0, SpectrumDataLowerBound(new double[0], 5));
        Assert.Throws<ArgumentException>(() => new SpectrumData(s.Header, new[] { 1.0 }, Array.Empty<double>(), new SpectrumDetails()));
    }

    private static int SpectrumDataLowerBound(double[] a, double v) => SpectrumData.LowerBound(a, v);

    private sealed class SyncProgress<T>(List<T> reports) : IProgress<T>
    {
        public void Report(T value) => reports.Add(value);
    }

    private sealed class SyncProgress : IProgress<double>
    {
        private readonly List<double> _reports;

        public SyncProgress(List<double> reports) => _reports = reports;

        public void Report(double value) => _reports.Add(value);
    }
}
