using Glimpse.Core.Scans;
using Glimpse.Features;
using Xunit;

namespace Glimpse.Tests;

/// <summary>The peptide-like signal: envelopes found on worker threads during the pass, linked after it.</summary>
public class PeptideLikeTests
{
    /// <summary>
    /// Finds an envelope wherever a spectrum has a peak at one of the given m/z values: that peak and the next
    /// <c>Count - 1</c> peaks, at the given charge.
    /// </summary>
    private sealed class PatternFinder : IEnvelopeFinder
    {
        private readonly (double FirstMz, int Charge, int Count)[] _patterns;
        private int _calls;

        public PatternFinder(params (double FirstMz, int Charge, int Count)[] patterns) => _patterns = patterns;

        public int Calls => _calls;

        public IReadOnlyList<IsotopeEnvelope> Find(double[] mz, double[] intensity, Glimpse.Core.Data.MassAnalyzer analyzer)
        {
            Interlocked.Increment(ref _calls);
            var found = new List<IsotopeEnvelope>();
            foreach (var (first, charge, count) in _patterns)
            {
                var i = Array.FindIndex(mz, m => Math.Abs(m - first) < 0.001);
                if (i < 0 || i + count > mz.Length)
                    continue;
                var peaks = Enumerable.Range(i, count).ToArray();
                found.Add(new IsotopeEnvelope((first - 1.00727649) * charge, charge, (float)peaks.Sum(p => intensity[p]),
                    mz[i], 0.99, mz[i], mz[i + count - 1], peaks));
            }
            return found;
        }
    }

    private static PeptideLikeSignal Run(FakeSource source, IEnvelopeFinder finder)
    {
        using var observer = new PeptideLikeObserver(finder, workers: 3);
        ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        return observer.Completion.Wait(TimeSpan.FromSeconds(30)) ? observer.Completion.Result : throw new TimeoutException();
    }

    [Fact]
    public void PersistentMs1EnvelopesArePeptideLike()
    {
        // A 2+ envelope (500.0, 500.5, 501.0) in four scans; a 1+ envelope at 700 in three; a 3+ one at 800 in one.
        var source = new FakeSource();
        for (var s = 0; s < 4; s++)
        {
            var mz = new List<double> { 500.0, 500.5, 501.0 };
            var intensity = new List<double> { 100, 50, 25 };
            if (s < 3) { mz.Add(700); intensity.Add(40); }
            if (s == 0) { mz.Add(800); intensity.Add(1000); }
            source.Ms1(1 + s * 0.1, mz.ToArray(), intensity.ToArray());
        }
        var signal = Run(source, new PatternFinder((500.0, 2, 3), (700, 1, 1), (800, 3, 1)));

        Assert.Equal(new[] { 215.0, 215, 215, 175 }, signal.IntensityByScan());            // 175 + the 1+ at 700
        Assert.Equal(new[] { 175.0, 175, 175, 175 }, signal.IntensityByScan(ms1MinCharge: 2));
        Assert.Equal(new[] { 0.0, 0, 0, 0 }, signal.IntensityByScan(ms1MinCharge: 3));     // the 3+ is in one scan only
        Assert.Same(signal.IntensityByScan(2), signal.IntensityByScan(2));                // computed once
        Assert.Equal(new[] { 0, 1, 2, 3 }, signal.PeptideLikePeaks(0));                   // not the 800 peak (index 4)
        Assert.Equal(new[] { 0, 1, 2 }, signal.PeptideLikePeaks(0, ms1MinCharge: 2));
        Assert.Equal(2, signal.Features.Count);
        Assert.All(signal.Features, f => Assert.Equal(0, f.Series));
        Assert.Equal(new[] { 0, 1, 2, 3 }, Assert.Single(signal.Series));
        Assert.False(signal.IsDda);
    }

    [Fact]
    public void APeakInTwoEnvelopesCountsOnce()
    {
        // Envelopes 500.0 (2+, three peaks) and 501.0 (1+, two peaks) share the 501.0 peak.
        var source = new FakeSource();
        for (var s = 0; s < 3; s++)
            source.Ms1(1 + s * 0.1, new[] { 500.0, 500.5, 501.0, 502.0 }, new[] { 100.0, 50, 25, 5 });
        var signal = Run(source, new PatternFinder((500.0, 2, 3), (501.0, 1, 2)));
        Assert.Equal(180.0, signal.IntensityByScan()[0]);   // 100 + 50 + 25 + 5, the shared 25 once
    }

    [Fact]
    public void DiaWindowsPersistOnTheirOwnAndMs2CountsSinglyCharged()
    {
        // Three cycles of MS1 + windows A (400-410) and B (410-420). A has a 1+ envelope every cycle; B only once.
        var source = new FakeSource();
        for (var c = 0; c < 3; c++)
        {
            var rt = 1 + c * 0.1;
            source.Ms1(rt, new[] { 450.0 }, new[] { 10.0 });
            source.Ms2(rt + 0.01, 400, 410, new[] { 300.0, 301.0 }, new[] { 80.0, 20 });
            source.Ms2(rt + 0.02, 410, 420, c == 1 ? new[] { 600.0 } : new[] { 650.0 }, new[] { 9.0 });
        }
        var finder = new PatternFinder((300.0, 1, 2), (600.0, 1, 1));
        var signal = Run(source, finder);

        var tic = signal.IntensityByScan(ms1MinCharge: 2);
        Assert.Equal(new[] { 0.0, 100, 0, 0, 100, 0, 0, 100, 0 }, tic);                  // 1+ counts in MS2
        Assert.Equal(3, signal.Series.Count);                                              // MS1, A, B
        Assert.Equal(new[] { 1, 4, 7 }, signal.Series[1]);
        Assert.Equal(1, Assert.Single(signal.Features).Series);
        Assert.Equal(9, finder.Calls);                                                     // every scan searched once
    }

    [Fact]
    public void AWindowSeenOnceIsNotSearched()
    {
        var source = new FakeSource()
            .Ms1(1.0, new[] { 450.0 }, new[] { 10.0 })
            .Ms2(1.01, 400, 410, new[] { 300.0 }, new[] { 5.0 })
            .Ms2(1.02, 500, 510, new[] { 300.0 }, new[] { 5.0 })
            .Ms2(1.03, 400, 410, new[] { 300.0 }, new[] { 5.0 });
        var finder = new PatternFinder();
        var signal = Run(source, finder);
        Assert.Equal(3, finder.Calls);                       // the MS1 and both 400-410 scans
        Assert.True(signal.Covers(1));
        Assert.False(signal.Covers(2));
        Assert.True(double.IsNaN(signal.IntensityByScan()[2]));
        Assert.Equal(0.0, signal.IntensityByScan()[1]);
        Assert.Empty(signal.PeptideLikePeaks(2));
    }

    [Fact]
    public void ADdaRunHasNoMs2Signal()
    {
        // A new isolation window for every MS2 scan: more than the DIA limit means DDA.
        var source = new FakeSource();
        for (var i = 0; i < PeptideLikeObserver.MaxDiaWindows + 5; i++)
        {
            if (i % 50 == 0)
                source.Ms1(1 + i * 0.001, new[] { 450.0 }, new[] { 10.0 });
            var lo = 400 + i * 0.37;
            source.Ms2(1 + i * 0.001 + 0.0005, lo, lo + 2, new[] { 300.0 }, new[] { 5.0 });
            if (i % 7 == 0)
                source.Ms2(1 + i * 0.001 + 0.0007, lo, lo + 2, new[] { 300.0 }, new[] { 5.0 });   // some come back
        }
        var signal = Run(source, new PatternFinder());
        Assert.True(signal.IsDda);
        Assert.Single(signal.Series);                                                     // MS1 only
        Assert.All(signal.Series[0], i => Assert.True(signal.Covers(i)));
        Assert.Equal(signal.Series[0].Length, signal.IntensityByScan().Count(v => !double.IsNaN(v)));
    }

    [Fact]
    public void AnAbandonedPassCancelsTheSearch()
    {
        var observer = new PeptideLikeObserver(new PatternFinder(), workers: 2);
        observer.Begin(10, 0, 1);
        observer.Dispose();
        Assert.True(observer.Completion.IsCanceled);
    }

    [Fact]
    public void ScanEnvelopesKeepEachEnvelopesPeaks()
    {
        var envelopes = new[]
        {
            new IsotopeEnvelope(998, 2, 175, 500, 0.9, 500, 501, new[] { 2, 3, 4 }),
            new IsotopeEnvelope(500, 1, 30, 501, 0.9, 501, 502, new[] { 4, 7 }),
        };
        var compact = ScanEnvelopes.From(envelopes, new[] { 0.0, 1, 2, 3, 4, 5, 6, 7 });
        Assert.Equal(new[] { 2, 3, 4, 7 }, compact.PeakIndex);
        Assert.Equal(new[] { 2f, 3, 4, 7 }, compact.PeakIntensity);
        Assert.Equal(new ushort[] { 0, 1, 2 }, compact.PeaksOf(0).ToArray());
        Assert.Equal(new ushort[] { 2, 3 }, compact.PeaksOf(1).ToArray());
        Assert.Equal(new[] { new EnvelopeKey(998, 2, 175), new EnvelopeKey(500, 1, 30) }, compact.Keys);
        Assert.Same(ScanEnvelopes.Empty, ScanEnvelopes.From(Array.Empty<IsotopeEnvelope>(), Array.Empty<double>()));
    }
}
