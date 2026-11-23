using Glimpse.Core.Data;
using Glimpse.Core.Scans;
using Glimpse.Features;
using Glimpse.Pwiz;
using Xunit;

namespace Glimpse.Tests;

/// <summary>A spectrum's peptide-like envelopes, re-found and matched to the run's search, for labeling.</summary>
public class PeptideLikeEnvelopesTests
{
    private static (PeptideLikeSignal Signal, ScanIndex Index) Search(IMsDataSource source, IEnvelopeFinder finder)
    {
        using var observer = new PeptideLikeObserver(finder, workers: 3);
        var index = ScanIndexBuilder.Build(source, true, new IScanObserver[] { observer });
        Assert.True(observer.Completion.Wait(TimeSpan.FromMinutes(2)));
        return (observer.Completion.Result, index);
    }

    private static FakeSource Run()
    {
        // A 2+ envelope (500.0-501.0) in all three scans; a 1+ at 700 in scan 0 only; a 1+ at 300 in all three.
        var source = new FakeSource();
        for (var s = 0; s < 3; s++)
        {
            var mz = new List<double> { 300.0, 500.0, 500.5, 501.0 };
            var intensity = new List<double> { 30, 100, 50, 25 };
            if (s == 0) { mz.Add(700); intensity.Add(40); }
            source.Ms1(1 + s * 0.1, mz.ToArray(), intensity.ToArray());
        }
        return source;
    }

    private static readonly PatternEnvelopeFinder Finder = new((300.0, 1, 1), (500.0, 2, 3), (700.0, 1, 1));

    [Fact]
    public void OnlyPersistentEnvelopesWithTheirPeaks()
    {
        var source = Run();
        var (signal, _) = Search(source, Finder);
        var s = source.ReadSpectrum(0, false);
        var found = PeptideLikeEnvelopes.Persistent(signal, 0, s.Mz, s.Intensity, MassAnalyzer.Unknown, Finder);
        Assert.Equal(new[] { (1, new[] { 0 }), (2, new[] { 1, 2, 3 }) }, found.Select(e => (e.Charge, e.PeakIndices)));   // not the lone 700
        Assert.Equal(1, PeptideLikeEnvelopes.BasePeakIndex(found[1], s.Mz));
        // MS1 2+ only.
        Assert.Equal(new[] { 2 }, PeptideLikeEnvelopes.Persistent(signal, 0, s.Mz, s.Intensity, MassAnalyzer.Unknown, Finder, ms1MinCharge: 2).Select(e => e.Charge));
    }

    [Fact]
    public void NothingWhenTheScanWasNotSearchedOrTheEnvelopesDiffer()
    {
        var source = Run();
        var (signal, _) = Search(source, Finder);
        var s = source.ReadSpectrum(1, false);
        Assert.Empty(PeptideLikeEnvelopes.Persistent(signal, 7, s.Mz, s.Intensity, MassAnalyzer.Unknown, Finder));      // not searched
        // Another finder (one envelope fewer): the envelopes no longer line up with the search's, so none are used.
        var other = new PatternEnvelopeFinder((500.0, 2, 3));
        Assert.Empty(PeptideLikeEnvelopes.Persistent(signal, 1, s.Mz, s.Intensity, MassAnalyzer.Unknown, other));
        // The same count but another envelope (a 3+ where the search had a 2+).
        var swapped = new PatternEnvelopeFinder((300.0, 1, 1), (500.0, 3, 3));
        Assert.Empty(PeptideLikeEnvelopes.Persistent(signal, 1, s.Mz, s.Intensity, MassAnalyzer.Unknown, swapped));
        // A spectrum read differently (a peak's intensity changed).
        var changed = (double[])s.Intensity.Clone();
        changed[1] = 101;
        Assert.Empty(PeptideLikeEnvelopes.Persistent(signal, 1, s.Mz, changed, MassAnalyzer.Unknown, Finder));
    }

    [Fact]
    public void ASearchReadFromAHkFileGetsItsPeaksBack()
    {
        var source = Run();
        var (signal, index) = Search(source, Finder);
        var dir = Directory.CreateTempSubdirectory("glimpse-pl");
        try
        {
            var path = Path.Combine(dir.FullName, "run.hk");
            var stamp = new HkFile.Stamp(1, DateTime.UnixEpoch, index.Count, "s");
            HkFile.Write(path, signal, stamp);
            var read = HkFile.TryRead(path, stamp, index.Headers.ToArray())!;
            Assert.False(read.HasPeaks);
            var s = source.ReadSpectrum(2, false);
            Assert.Equal(new[] { new[] { 0 }, new[] { 1, 2, 3 } },
                PeptideLikeEnvelopes.Persistent(read, 2, s.Mz, s.Intensity, MassAnalyzer.Unknown, Finder).Select(e => e.PeakIndices));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Returns another finder's envelopes with the mass moved a little: only the mass differs.</summary>
    private sealed class ShiftedMass(IEnvelopeFinder inner) : IEnvelopeFinder
    {
        public bool CanSearch(MassAnalyzer analyzer) => inner.CanSearch(analyzer);

        public IReadOnlyList<IsotopeEnvelope> Find(double[] mz, double[] intensity, MassAnalyzer analyzer) =>
            inner.Find(mz, intensity, analyzer).Select(e => e with { MonoMass = e.MonoMass + 0.01 }).ToArray();
    }

    [Fact]
    public void AnEnvelopeOfAnotherMassIsAnotherEnvelope()
    {
        var source = Run();
        var (signal, _) = Search(source, Finder);
        var s = source.ReadSpectrum(1, false);
        Assert.NotEmpty(PeptideLikeEnvelopes.Persistent(signal, 1, s.Mz, s.Intensity, MassAnalyzer.Unknown, Finder));
        Assert.Empty(PeptideLikeEnvelopes.Persistent(signal, 1, s.Mz, s.Intensity, MassAnalyzer.Unknown, new ShiftedMass(Finder)));
    }

    [Fact]
    public void TheMs1ChargeRuleLeavesMs2Alone()
    {
        // A DIA window over three cycles with a 1+ envelope: it counts in MS2 even when MS1 counts 2+ only.
        var source = new FakeSource();
        for (var c = 0; c < 3; c++)
        {
            source.Ms1(1 + c * 0.1, new[] { 450.0 }, new[] { 10.0 });
            source.Ms2(1.05 + c * 0.1, 400, 410, new[] { 300.0, 301.0 }, new[] { 80.0, 20 });
        }
        var finder = new PatternEnvelopeFinder((300.0, 1, 2));
        var (signal, _) = Search(source, finder);
        var s = source.ReadSpectrum(1, false);
        Assert.Equal(2, s.Header.MsLevel);
        Assert.Equal(new[] { 1 }, PeptideLikeEnvelopes.Persistent(signal, 1, s.Mz, s.Intensity, MassAnalyzer.Unknown, finder, ms1MinCharge: 2).Select(e => e.Charge));
    }

    [Fact]
    public void TheBasePeakIsTheMatchedPeakAtTheBasePeakMz()
    {
        var mz = new[] { 499.0, 500.0, 500.5, 501.0 };
        var envelope = new IsotopeEnvelope(1000, 2, 1, 500.5, 0.99, 500, 501, new[] { 1, 2, 3 });   // base peak the middle one
        Assert.Equal(2, PeptideLikeEnvelopes.BasePeakIndex(envelope, mz));
        Assert.Equal(3, PeptideLikeEnvelopes.BasePeakIndex(envelope with { BasePeakMz = 501.0004 }, mz));
    }

    /// <summary>Hardklör finds exactly the search's envelopes again in every searched scan of a real run.</summary>
    [Fact]
    public void TheRealFinderFindsTheSameEnvelopesAgain()
    {
        using var source = PwizMsDataSource.Open(TestData.SmallRaw);
        var finder = new AnalyzerEnvelopeFinder(new PwizEnvelopeModel());
        var (signal, index) = Search(source, finder);
        var searched = index.IndicesOfLevel(1).Where(signal.Covers).ToArray();
        Assert.Equal(7, searched.Length);
        var labeled = 0;
        foreach (var i in searched)
        {
            var s = source.ReadSpectrum(i, centroid: true);
            var persistent = PeptideLikeEnvelopes.Persistent(signal, i, s.Mz, s.Intensity, s.Header.Analyzer, finder);
            // Matched to the search: as many as the search linked into features in this scan.
            Assert.Equal(signal.FeatureOf[i].Count(id => id >= 0), persistent.Count);
            labeled += persistent.Count;
        }
        Assert.True(labeled > 100, $"{labeled} persistent envelopes");
    }
}
