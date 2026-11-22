using Glimpse.Core.Assignment;
using Glimpse.Core.Chromatograms;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Scans;
using Xunit;

namespace Glimpse.Tests;

/// <summary>XICs built in the background are kept only while their selection is still the one shown.</summary>
public class XicBuildCacheTests
{
    private readonly ScanIndex _index;
    private readonly DocumentSignals _signals;
    private readonly DocPeptide _aak;

    public XicBuildCacheTests()
    {
        var protein = new DocProtein("P1", null, null, null);
        var precursor = Docs.Precursor(protein, "AAK", 2, 400.0, "run1.raw", 0, 2, ("precursor", 2, 400.0));
        _aak = precursor.Peptide;
        var source = new FakeSource().Ms1(1, new[] { 400.0 }, new[] { 100.0 }).Ms1(1.5, new[] { 400.0 }, new[] { 60.0 });
        var observer = new DocumentSignalObserver(Docs.Model(protein), "run1.raw", DocumentSettings.Default);
        _index = ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        _signals = observer.Result!;
    }

    private XicRequest Request(IReadOnlyList<DocPeptide> peptides, XicMode mode = XicMode.Summed, bool ms1 = true,
        IntensityUnits units = IntensityUnits.AsRecorded, bool integratedOnly = false) =>
        new(_index, _signals, peptides, mode, ms1, units, integratedOnly);

    [Fact]
    public void RequestsAreTheSameForTheSameSelectionListAndOptions()
    {
        var selection = new[] { _aak };
        var a = Request(selection);
        Assert.Equal(a, Request(selection));
        Assert.Equal(a.GetHashCode(), Request(selection).GetHashCode());
        // The same peptides in a new list are a new selection.
        Assert.NotEqual(a, Request(new[] { _aak }));
        Assert.NotEqual(a, Request(selection, mode: XicMode.PerPeptide));
        Assert.NotEqual(a, Request(selection, ms1: false));
        Assert.NotEqual(a, Request(selection, units: IntensityUnits.Ions));
        Assert.NotEqual(a, Request(selection, integratedOnly: true));
        Assert.NotEqual(a, a with { Signals = _signals with { FileName = "other.raw" } });
        Assert.NotEqual(a, a with { Index = new ScanIndex(_index.FilePath, _index.Run, _index.Headers.ToArray()) });
        Assert.False(a.Equals(null));

        var trace = Assert.Single(a.Build());
        Assert.Equal(new[] { 100.0, 60.0 }, trace.Values);
    }

    [Fact]
    public void ABackgroundBuildIsKeptWhileItsRequestIsStillWanted()
    {
        var cache = new XicBuildCache();
        var big = Request(new[] { _aak });
        Assert.False(cache.TryGet(big, out var none));
        Assert.Empty(none);

        Assert.True(cache.Start(big));
        Assert.Same(big, cache.Building);
        Assert.False(cache.Start(Request(big.Peptides)));   // the same request again: already building
        var traces = big.Build();
        Assert.True(cache.Complete(big, traces));
        Assert.Null(cache.Building);
        Assert.True(cache.TryGet(Request(big.Peptides), out var kept));
        Assert.Same(traces, kept);
    }

    [Fact]
    public void ABackgroundBuildOvertakenByAnotherSelectionIsDropped()
    {
        var cache = new XicBuildCache();
        var big = Request(new[] { _aak });
        var small = Request(new[] { _aak }, mode: XicMode.PerPeptide);
        cache.Start(big);
        // A small selection, built at once, replaces it before it finishes.
        var smallTraces = small.Build();
        cache.Store(small, smallTraces);
        Assert.Null(cache.Building);
        Assert.False(cache.Complete(big, big.Build()));
        Assert.True(cache.TryGet(small, out var kept));
        Assert.Same(smallTraces, kept);
        Assert.False(cache.TryGet(big, out _));

        // Another large selection started meanwhile: the first one's result is not it.
        var other = Request(new[] { _aak }, integratedOnly: true);
        cache.Start(big);
        cache.Start(other);
        Assert.False(cache.Complete(big, big.Build()));
        Assert.Same(other, cache.Building);

        // No XICs wanted at all.
        cache.Store(null, new List<XicTrace>());
        Assert.Null(cache.Building);
        Assert.False(cache.Complete(other, other.Build()));
        Assert.False(cache.TryGet(small, out _));
    }

    [Fact]
    public void AFailedBuildCanBeStartedAgain()
    {
        var cache = new XicBuildCache();
        var big = Request(new[] { _aak });
        var other = Request(new[] { _aak }, ms1: false);
        cache.Start(other);
        cache.Fail(big);   // not the one building: no change
        Assert.Same(other, cache.Building);
        cache.Fail(other);
        Assert.Null(cache.Building);
        Assert.True(cache.Start(other));
    }
}
