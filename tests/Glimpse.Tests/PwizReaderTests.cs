using System.Diagnostics;
using Glimpse.Core.Chromatograms;
using Glimpse.Core.Data;
using Glimpse.Core.Heatmaps;
using Glimpse.Core.Scans;
using Glimpse.Pwiz;
using Xunit;
using Xunit.Abstractions;

namespace Glimpse.Tests;

public class PwizReaderTests
{
    private readonly ITestOutputHelper _out;

    public PwizReaderTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void SmallRaw_IndexesEverySpectrum()
    {
        using var source = PwizMsDataSource.Open(TestData.SmallRaw);
        var heatmap = new IntensityHeatmapAccumulator();
        var index = ScanIndexBuilder.Build(source, centroid: true, new IScanObserver[] { heatmap });

        Assert.Equal(source.SpectrumCount, index.Count);
        Assert.True(index.Count > 10);
        Assert.Contains(index.Headers, h => h.MsLevel == 1);
        Assert.Contains(index.Headers, h => h.MsLevel == 2);
        Assert.All(index.Headers, h => Assert.False(double.IsNaN(h.RetentionTime)));
        Assert.NotNull(heatmap.Result);
        Assert.False(heatmap.Result!.Ms1.IsEmpty);
        _out.WriteLine($"{index.Count} spectra, {index.Run.InstrumentModel}, RT {index.RtFirst:0.00}-{index.RtLast:0.00}");
    }

    [Fact]
    public void OnlyThermoRawFilesAreReadOnSeveralThreads()
    {
        Assert.True(PwizMsDataSource.SupportsParallelReaders(TestData.SmallRaw));   // .RAW: any case
        Assert.True(PwizMsDataSource.SupportsParallelReaders(TestData.ThermoTestFile("BSA-FT-HCD.raw")));
        var dir = Directory.CreateTempSubdirectory("glimpse");
        try
        {
            var waters = Directory.CreateDirectory(Path.Combine(dir.FullName, "run.raw")).FullName;
            var mzml = Path.Combine(dir.FullName, "run.mzML");
            File.WriteAllText(mzml, "");
            Assert.False(PwizMsDataSource.SupportsParallelReaders(waters));   // a Waters folder
            Assert.False(PwizMsDataSource.SupportsParallelReaders(mzml));
            Assert.False(PwizMsDataSource.SupportsParallelReaders(Path.Combine(dir.FullName, "missing.raw")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ThermoHcd_HeaderMatchesPeaks()
    {
        using var source = PwizMsDataSource.Open(TestData.ThermoTestFile("BSA-FT-HCD.raw"));
        var index = ScanIndexBuilder.Build(source, centroid: true, Array.Empty<IScanObserver>());
        var ms2 = index.IndicesOfLevel(2);
        Assert.NotEmpty(ms2);

        var spectrum = source.ReadSpectrum(ms2[0], centroid: true);
        Assert.Equal(spectrum.Intensity.Sum(), spectrum.Header.TotalIonCurrent, 6);
        Assert.Equal(spectrum.Intensity.Max(), spectrum.Header.BasePeakIntensity, 1);
        Assert.True(spectrum.Header.HasIsolationWindow);
        Assert.True(spectrum.Header.HasInjectionTime, "Thermo scans carry an ion injection time");
        Assert.True(IsAscending(spectrum.Mz));
        Assert.Equal("HCD", spectrum.Details.Dissociation);
        Assert.NotNull(spectrum.Details.FilterString);
        _out.WriteLine($"scan {spectrum.Details.ScanNumber}: {spectrum.Details.FilterString}, IT {spectrum.Header.InjectionTimeMs} ms, "
            + $"iso {spectrum.Header.IsolationLower:0.00}-{spectrum.Header.IsolationUpper:0.00}");
    }

    [Fact]
    public void ThermoEtd_ReportsEtd()
    {
        using var source = PwizMsDataSource.Open(TestData.ThermoTestFile("BSA-FT-ETD.raw"));
        var index = ScanIndexBuilder.Build(source, centroid: true, Array.Empty<IScanObserver>());
        var ms2 = index.IndicesOfLevel(2);
        Assert.NotEmpty(ms2);
        var spectrum = source.ReadSpectrum(ms2[0], centroid: true);
        Assert.Contains("ETD", spectrum.Details.Dissociation);
    }

    [Fact]
    public void Chromatogram_IonsUseInjectionTime()
    {
        using var source = PwizMsDataSource.Open(TestData.ThermoTestFile("BSA-FT-HCD.raw"));
        var index = ScanIndexBuilder.Build(source, centroid: true, Array.Empty<IScanObserver>());
        // This file is MS2-only, so the all-scans TIC.
        var rate = RunChromatogram.Build(index, RunTraceKind.Tic, ScanFilter.All, IntensityUnits.AsRecorded);
        var ions = RunChromatogram.Build(index, RunTraceKind.Tic, ScanFilter.All, IntensityUnits.Ions);
        Assert.NotEqual(0, rate.Count);
        Assert.Equal(rate.Count, ions.Count);
        var h = index[rate.ScanIndices[0]];
        Assert.Equal(rate.Values[0] * h.InjectionTimeMs / 1000.0, ions.Values[0], 6);
    }

    /// <summary>
    /// Throughput on a real run. Set GLIMPSE_BIG_RAW to a file (and optionally GLIMPSE_BIG_RAW_MAX to a
    /// spectrum cap) to run it; otherwise it passes without doing anything.
    /// </summary>
    [Fact]
    public void BigRaw_FullPassTiming()
    {
        if (TestData.BigRaw is not { } path)
        {
            _out.WriteLine("GLIMPSE_BIG_RAW not set; skipped.");
            return;
        }
        var clock = Stopwatch.StartNew();
        using var source = PwizMsDataSource.Open(path);
        var open = clock.Elapsed;
        var heatmap = new IntensityHeatmapAccumulator();
        var maxSpectra = int.TryParse(Environment.GetEnvironmentVariable("GLIMPSE_BIG_RAW_MAX"), out var m) ? m : int.MaxValue;
        var limited = new LimitedSource(source, maxSpectra);
        var index = ScanIndexBuilder.Build(limited, centroid: true, new IScanObserver[] { heatmap });
        var pass = clock.Elapsed - open;
        _out.WriteLine($"{Path.GetFileName(path)}: open {open.TotalSeconds:0.0} s, {index.Count:N0} of {source.SpectrumCount:N0} spectra in "
            + $"{pass.TotalSeconds:0.0} s ({index.Count / pass.TotalSeconds:N0}/s); MS1 {index.IndicesOfLevel(1).Count:N0}, "
            + $"MS2 {index.IndicesOfLevel(2).Count:N0}; windows {index.IsolationWindows.Count}; {index.Run.InstrumentModel}; "
            + $"IT present {index.HasInjectionTimes}; heatmap MS1 {heatmap.Result!.Ms1.Rows}x{heatmap.Result.Ms1.Columns}, "
            + $"MS2 {heatmap.Result.Ms2.Rows}x{heatmap.Result.Ms2.Columns}; managed heap {GC.GetTotalMemory(false) / 1e6:N0} MB");

        // The acquired isolation windows, and the order of the first two cycles - what a document's
        // isolation scheme has to match.
        foreach (var (lo, hi) in index.IsolationWindows.Take(400))
            _out.WriteLine($"  window {lo:0.0000}-{hi:0.0000} (width {hi - lo:0.0000})");
        var firstMs1 = index.IndicesOfLevel(1).Take(3).ToArray();
        if (firstMs1.Length >= 3)
        {
            for (var i = firstMs1[0]; i < firstMs1[2]; i++)
            {
                var h = index[i];
                _out.WriteLine($"  scan {i}: MS{h.MsLevel} rt {h.RetentionTime:0.0000} iso {h.IsolationLower:0.0000}-{h.IsolationUpper:0.0000} IT {h.InjectionTimeMs}");
            }
        }
    }

    private static bool IsAscending(double[] values)
    {
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
                return false;
        }
        return true;
    }

    /// <summary>Caps the spectrum count so a timing run on a huge file can sample the start.</summary>
    private sealed class LimitedSource : IMsDataSource
    {
        private readonly IMsDataSource _inner;
        private readonly int _max;

        public LimitedSource(IMsDataSource inner, int max)
        {
            _inner = inner;
            _max = max;
        }

        public string FilePath => _inner.FilePath;

        public int SpectrumCount => Math.Min(_max, _inner.SpectrumCount);

        public RunInfo Run => _inner.Run;

        public bool CanVendorCentroid => _inner.CanVendorCentroid;

        public SpectrumData ReadSpectrum(int index, bool centroid) => _inner.ReadSpectrum(index, centroid);

        public double ReadRetentionTime(int index) => _inner.ReadRetentionTime(index);

        public void Dispose() => _inner.Dispose();
    }
}
