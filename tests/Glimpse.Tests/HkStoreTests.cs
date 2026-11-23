using Glimpse.Core.Scans;
using Glimpse.Features;
using Glimpse.Pwiz;
using Xunit;

namespace Glimpse.Tests;

/// <summary>Where a run's .hk file is saved and found: beside the raw file, else Glimpse's cache folder.</summary>
public sealed class HkStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("glimpse-hkstore").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static PeptideLikeSignal Signal()
    {
        var source = new FakeSource();
        for (var s = 0; s < 3; s++)
            source.Ms1(1 + s * 0.1, new[] { 500.0, 500.5, 501.0 }, new[] { 100.0, 50, 25 });
        using var observer = new PeptideLikeObserver(new PatternEnvelopeFinder((500.0, 2, 3)), workers: 2);
        ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        Assert.True(observer.Completion.Wait(TimeSpan.FromSeconds(30)));
        return observer.Completion.Result;
    }

    [Fact]
    public void SavedBesideTheRawFileAndFoundThere()
    {
        var raw = Path.Combine(_dir, "data", "run.raw");
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        File.WriteAllBytes(raw, new byte[10]);
        var store = new HkStore(Path.Combine(_dir, "cache"));
        var stamp = HkFile.Stamp.Of(raw, 3, "s");
        Assert.Null(store.FindUsable(raw, stamp));                 // nothing saved yet

        var where = store.Save(raw, Signal(), stamp);
        Assert.Equal(Path.Combine(_dir, "data", "run.hk"), where);
        Assert.Equal(where, store.FindUsable(raw, stamp));
        Assert.Null(store.FindUsable(raw, stamp with { Settings = "other" }));
        Assert.False(Directory.Exists(Path.Combine(_dir, "cache")));
    }

    [Fact]
    public void SavedInTheCacheWhenTheRawFilesFolderCannotTakeIt()
    {
        var raw = Path.Combine(_dir, "share", "run.raw");
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        File.WriteAllBytes(raw, new byte[10]);
        Directory.CreateDirectory(Path.Combine(_dir, "share", "run.hk"));   // run.hk cannot be a file there
        var store = new HkStore(Path.Combine(_dir, "cache"));
        var stamp = HkFile.Stamp.Of(raw, 3, "s");

        var where = store.Save(raw, Signal(), stamp);
        Assert.Equal(store.CachePath(raw), where);
        Assert.StartsWith(Path.Combine(_dir, "cache"), where);
        Assert.Equal(where, store.FindUsable(raw, stamp));
        Assert.False(File.Exists(Path.Combine(_dir, "share", "run.hk.tmp")));
    }

    [Fact]
    public void CacheFilesAreNamedByRunAndPath()
    {
        var store = new HkStore(@"C:\cache");
        var a = store.CachePath(@"D:\one\run.raw");
        Assert.Matches(@"^C:\\cache\\run-[0-9A-F]{16}\.hk$", a);
        Assert.NotEqual(a, store.CachePath(@"D:\two\run.raw"));    // same name, another folder
        Assert.Equal(a, store.CachePath(@"d:\ONE\run.raw"));       // Windows paths ignore case
        Assert.Equal(store.CachePath(@"D:\w\waters.raw"), store.CachePath(@"D:\w\waters.raw\"));
    }

    [Fact]
    public void TheSettingsTextNamesEverythingASearchDependsOn()
    {
        Assert.Equal(
            "model m; orbitrap Orbitrap 60000 z1-7 r0.9876883405951378 d2 max5000 areaTrue; "
            + "astral Tof 80000 z1-7 r0.9876883405951378 d2 max5000 areaTrue; iontrap none; "
            + "kronik 10ppm gap1 min3; dia windows 1000; centroid True",
            HkStore.SearchSettings("m", centroid: true));
        Assert.EndsWith("centroid False", HkStore.SearchSettings("m", centroid: false));
        Assert.Equal("pwiz isotope tables, 13C 0.01084, spacing 1.00286864", PwizEnvelopeModel.Description);
    }
}
