using Glimpse.Core.Data;
using Glimpse.Core.Scans;
using Glimpse.Features;
using Glimpse.Pwiz;
using Parquet;
using Parquet.Schema;
using Xunit;

namespace Glimpse.Tests;

/// <summary>The .hk file: a run's peptide-like search saved beside the raw file and read back.</summary>
public sealed class HkFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("glimpse-hk").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly DateTime Modified = new(2026, 2, 11, 20, 32, 0, DateTimeKind.Utc);

    private static (PeptideLikeSignal Signal, ScanHeader[] Headers) Search(IMsDataSource source, IEnvelopeFinder finder)
    {
        using var observer = new PeptideLikeObserver(finder, workers: 3);
        var index = ScanIndexBuilder.Build(source, true, new IScanObserver[] { observer });
        Assert.True(observer.Completion.Wait(TimeSpan.FromMinutes(2)));
        return (observer.Completion.Result, index.Headers.ToArray());
    }

    /// <summary>
    /// The same signal: per-scan totals (both MS1 charge rules), coverage, features, series and envelopes. A
    /// signal read back has no peaks (the file keeps each scan's totals instead).
    /// </summary>
    private static void AssertSame(PeptideLikeSignal expected, PeptideLikeSignal actual, int scans)
    {
        Assert.Equal(expected.IntensityByScan(1), actual.IntensityByScan(1));
        Assert.Equal(expected.IntensityByScan(2), actual.IntensityByScan(2));
        for (var i = 0; i < scans; i++)
        {
            Assert.Equal(expected.Covers(i), actual.Covers(i));
            Assert.Empty(actual.PeptideLikePeaks(i));
        }
        Assert.Equal(expected.Series, actual.Series);
        Assert.Equal(expected.Features, actual.Features);
        Assert.Equal(expected.IsDda, actual.IsDda);
        Assert.Equal(expected.Counts.Envelopes, actual.Counts.Envelopes);
        Assert.Equal(0, actual.Counts.PeakRefs);
        Assert.True(expected.HasPeaks);
        Assert.False(actual.HasPeaks);
        Assert.Throws<InvalidOperationException>(() => actual.IntensityByScan(3));   // only 1+ and 2+ are kept
    }

    [Fact]
    public void ARealSearchSurvivesAWriteAndRead()
    {
        using var source = PwizMsDataSource.Open(TestData.SmallRaw);
        var (signal, headers) = Search(source, new AnalyzerEnvelopeFinder(new PwizEnvelopeModel()));
        Assert.True(signal.Features.Count > 0);
        var stamp = new HkFile.Stamp(123, Modified, headers.Length, "settings A");
        var path = Path.Combine(_dir, "small.hk");
        HkFile.Write(path, signal, stamp);
        Assert.False(File.Exists(path + ".tmp"));

        var read = HkFile.TryRead(path, stamp, headers);
        Assert.NotNull(read);
        AssertSame(signal, read!, headers.Length);
    }

    [Fact]
    public void DiaWindowsAndScansWithoutEnvelopesSurvive()
    {
        // Two windows over three cycles; window B has an envelope once, so most of its scans have none.
        var source = new FakeSource();
        for (var c = 0; c < 3; c++)
        {
            var rt = 1 + c * 0.1;
            source.Ms1(rt, new[] { 500.0, 500.5, 501.0 }, new[] { 100.0, 50, 25 }, 10f, MassAnalyzer.Orbitrap);
            source.Ms2(rt + 0.01, 400, 410, new[] { 300.0, 301.0 }, new[] { 80.0, 20 }, 10f, MassAnalyzer.Astral);
            source.Ms2(rt + 0.02, 410, 420, c == 1 ? new[] { 600.0 } : new[] { 650.0 }, new[] { 9.0 }, 10f, MassAnalyzer.Astral);
        }
        var (signal, headers) = Search(source, new PatternEnvelopeFinder((500.0, 2, 3), (300.0, 1, 2), (600.0, 1, 1)));
        Assert.Equal(3, signal.Series.Count);
        var stamp = new HkFile.Stamp(0, Modified, headers.Length, "s");
        var path = Path.Combine(_dir, "dia.hk");
        HkFile.Write(path, signal, stamp);
        HkFile.Write(path, signal, stamp);                       // over an existing file
        AssertSame(signal, HkFile.TryRead(path, stamp, headers)!, headers.Length);
    }

    [Fact]
    public void AFileMadeFromSomethingElseIsIgnored()
    {
        var source = new FakeSource();
        for (var s = 0; s < 3; s++)
            source.Ms1(1 + s * 0.1, new[] { 500.0, 500.5, 501.0 }, new[] { 100.0, 50, 25 });
        var (signal, headers) = Search(source, new PatternEnvelopeFinder((500.0, 2, 3)));
        var stamp = new HkFile.Stamp(1000, Modified, headers.Length, "settings A");
        var path = Path.Combine(_dir, "run.hk");
        HkFile.Write(path, signal, stamp);

        Assert.NotNull(HkFile.TryRead(path, stamp, headers));
        Assert.Null(HkFile.TryRead(path, stamp with { RawSize = 1001 }, headers));                 // the raw file changed
        Assert.Null(HkFile.TryRead(path, stamp with { RawModifiedUtc = Modified.AddSeconds(1) }, headers));
        Assert.Null(HkFile.TryRead(path, stamp with { Settings = "settings B" }, headers));        // other search settings
        Assert.Null(HkFile.TryRead(path, stamp with { SpectrumCount = 4 }, headers));
        Assert.Null(HkFile.TryRead(path, stamp, headers.Take(2).ToArray()));                       // another run
        Assert.Null(HkFile.TryRead(Path.Combine(_dir, "none.hk"), stamp, headers));
        File.WriteAllText(Path.Combine(_dir, "bad.hk"), "not parquet");
        Assert.Null(HkFile.TryRead(Path.Combine(_dir, "bad.hk"), stamp, headers));
        Assert.False(HkFile.Matches(Path.Combine(_dir, "bad.hk"), stamp));
        // Parquet's magic at both ends around garbage: whatever the reader throws, the file is just unusable.
        var fake = new byte[64];
        "PAR1"u8.CopyTo(fake);
        "PAR1"u8.CopyTo(fake.AsSpan(60));
        BitConverter.GetBytes(40).CopyTo(fake, 56);
        new Random(1).NextBytes(fake.AsSpan(4, 52));
        File.WriteAllBytes(Path.Combine(_dir, "garbage.hk"), fake);
        Assert.False(HkFile.Matches(Path.Combine(_dir, "garbage.hk"), stamp));
        Assert.Null(HkFile.TryRead(Path.Combine(_dir, "garbage.hk"), stamp, headers));
        // A truncated real file.
        var whole = File.ReadAllBytes(path);
        File.WriteAllBytes(Path.Combine(_dir, "cut.hk"), whole[..(whole.Length / 2)]);
        Assert.False(HkFile.Matches(Path.Combine(_dir, "cut.hk"), stamp));
        Assert.Null(HkFile.TryRead(Path.Combine(_dir, "cut.hk"), stamp, headers));
    }

    [Fact]
    public async Task ARowIsOneEnvelopeWithItsPeaks()
    {
        var source = new FakeSource();
        for (var s = 0; s < 3; s++)
            source.Ms1(1 + s * 0.1, new[] { 400.0, 500.0, 500.5, 501.0 }, new[] { 7.0, 100, 50, 25 });
        var (signal, headers) = Search(source, new PatternEnvelopeFinder((500.0, 2, 3)));
        var path = Path.Combine(_dir, "rows.hk");
        HkFile.Write(path, signal, new HkFile.Stamp(1, Modified, headers.Length, "s"));

        await using var stream = File.OpenRead(path);
        await using var reader = await ParquetReader.CreateAsync(stream);
        Assert.Equal(
            new[] { "scan", "ms_level", "rt", "series", "mono_mass", "charge", "intensity", "base_peak_mz", "correlation",
                "low_mz", "high_mz", "feature", "scan_peptide_like", "scan_peptide_like_2plus" },
            reader.Schema.GetDataFields().Select(f => f.Name));
        Assert.Equal("2", reader.CustomMetadata["glimpse.hk.format"]);
        Assert.Equal("[[0,1,2]]", reader.CustomMetadata["glimpse.series"]);
        using var group = reader.OpenRowGroupReader(0);
        Assert.Equal(3, group.RowCount);
        async Task<int[]> Ints(string name)
        {
            var v = new int[3];
            await group.ReadAsync(reader.Schema.GetDataFields().Single(f => f.Name == name), v.AsMemory());
            return v;
        }
        Assert.Equal(new[] { 0, 1, 2 }, await Ints("scan"));
        Assert.Equal(new[] { 2, 2, 2 }, await Ints("charge"));
        Assert.Equal(new[] { 0, 0, 0 }, await Ints("feature"));      // one feature over three scans
        var signal3 = new double[3];
        await group.ReadAsync(reader.Schema.GetDataFields().Single(f => f.Name == "scan_peptide_like"), signal3.AsMemory());
        Assert.Equal(new[] { 175.0, 175, 175 }, signal3);              // 100 + 50 + 25, not the lone 400 peak
    }

    [Theory]
    [InlineData(@"D:\data\run.raw", @"D:\data\run.hk")]
    [InlineData(@"D:\data\run.RAW", @"D:\data\run.hk")]
    [InlineData(@"D:\data\run.mzML", @"D:\data\run.hk")]
    [InlineData(@"D:\data\waters.raw\", @"D:\data\waters.hk")]     // a vendor folder
    public void TheFileSitsBesideTheRawFile(string raw, string hk) => Assert.Equal(hk, HkFile.PathBeside(raw));

    [Fact]
    public void TheStampOfAFileAndOfAFolder()
    {
        var file = Path.Combine(_dir, "run.raw");
        File.WriteAllBytes(file, new byte[42]);
        File.SetLastWriteTimeUtc(file, Modified);
        Assert.Equal(new HkFile.Stamp(42, Modified, 7, "x"), HkFile.Stamp.Of(file, 7, "x"));
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "waters.raw")).FullName;
        Directory.SetLastWriteTimeUtc(folder, Modified);
        Assert.Equal(new HkFile.Stamp(0, Modified, 7, "x"), HkFile.Stamp.Of(folder, 7, "x"));          // empty: the folder's time
        // A vendor folder: the files inside it, total size and newest time (the folder's own time lags).
        File.WriteAllBytes(Path.Combine(folder, "_FUNC001.DAT"), new byte[100]);
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllBytes(Path.Combine(folder, "sub", "_extern.inf"), new byte[20]);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "_FUNC001.DAT"), Modified.AddHours(1));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "sub", "_extern.inf"), Modified.AddHours(2));
        Directory.SetLastWriteTimeUtc(folder, Modified);
        Assert.Equal(new HkFile.Stamp(120, Modified.AddHours(2), 7, "x"), HkFile.Stamp.Of(folder, 7, "x"));
    }
}
