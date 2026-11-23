using System.Globalization;
using Glimpse.Features;
using Glimpse.Features.Hardklor;
using Glimpse.Pwiz;
using Xunit;
using Xunit.Abstractions;

namespace Glimpse.Tests;

public class HardklorTests
{
    private static readonly PwizEnvelopeModel Model = new();
    private readonly ITestOutputHelper _out;

    public HardklorTests(ITestOutputHelper output) => _out = output;

    // ---- Settings ----

    [Fact]
    public void DefaultsAreSkylinesConfiguration()
    {
        var s = new HardklorSettings();
        Assert.Equal(s, HardklorSettings.Default);
        Assert.Equal(HardklorInstrument.Orbitrap, s.Instrument);
        Assert.Equal(60000, s.Resolution);
        Assert.Equal(1, s.MinCharge);
        Assert.Equal(7, s.MaxCharge);
        Assert.Equal(2, s.Depth);
        Assert.Equal(5000, s.MaxMoleculeMz);
        Assert.True(s.DistributionArea);
        // Skyline writes cos((1 - 0.9) * pi / 2) for its default idotp of 0.9.
        Assert.Equal(0.98768834059513777, s.Correlation, 15);
    }

    [Theory]
    [InlineData(0.9, 0.98768834059513777)]
    [InlineData(0.5, 0.70710678118654757)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.0)]  // clamped to 1, as Skyline does
    [InlineData(0.0, 0.0)]
    public void CorrelationFromIdotP_IsSkylinesConversion(double idotp, double expected) =>
        Assert.Equal(expected, HardklorSettings.CorrelationFromIdotP(idotp), 12);

    [Theory]
    [InlineData(HardklorInstrument.Orbitrap, 60000, 400, 0.0066666666666667)]  // 400^1.5 / (20 * 60000)
    [InlineData(HardklorInstrument.Orbitrap, 60000, 900, 0.0225)]              // 900^1.5 / (20 * 60000)
    [InlineData(HardklorInstrument.Fticr, 100000, 400, 0.004)]                 // 400^2 / (400 * 100000)
    [InlineData(HardklorInstrument.Fticr, 100000, 800, 0.016)]
    [InlineData(HardklorInstrument.Tof, 20000, 400, 0.02)]                     // 400 / 20000
    [InlineData(HardklorInstrument.Tof, 20000, 1000, 0.05)]
    [InlineData(HardklorInstrument.Qit, 2, 400, 0.0004)]                       // 2 / 5000 at any m/z
    [InlineData(HardklorInstrument.Qit, 2, 1200, 0.0004)]
    public void PeakWidth_IsCalcFWHM(HardklorInstrument instrument, double resolution, double mz, double expected) =>
        Assert.Equal(expected, new HardklorSettings { Instrument = instrument, Resolution = resolution }.PeakWidth(mz), 12);

    [Theory]
    [InlineData(0, 7, 60000, 2, 5000)]
    [InlineData(3, 2, 60000, 2, 5000)]
    [InlineData(1, 101, 60000, 2, 5000)]
    [InlineData(1, 7, 0, 2, 5000)]
    [InlineData(1, 7, double.NaN, 2, 5000)]
    [InlineData(1, 7, 60000, 0, 5000)]
    [InlineData(1, 7, 60000, 2, 4)]
    public void InvalidSettingsAreRejected(int minCharge, int maxCharge, double resolution, int depth, double maxMz)
    {
        var settings = new HardklorSettings
        {
            MinCharge = minCharge, MaxCharge = maxCharge, Resolution = resolution, Depth = depth, MaxMoleculeMz = maxMz,
        };
        Assert.Throws<ArgumentException>(() => new HardklorFinder(settings, Model));
    }

    [Fact]
    public void ExtremeValidSettingsAreAccepted()
    {
        Assert.NotNull(new HardklorFinder(new HardklorSettings { MinCharge = 3, MaxCharge = 3 }, Model));
        var finder = new HardklorFinder(new HardklorSettings { MinCharge = 1, MaxCharge = 100, Depth = 1, MaxMoleculeMz = 5 }, Model);
        Assert.Equal(2, finder.Models.BinCount);
        Assert.Throws<ArgumentNullException>(() => new HardklorFinder(null!, Model));
        Assert.Throws<ArgumentNullException>(() => new HardklorFinder(HardklorSettings.Default, null!));
    }

    // ---- Averagine and the model library ----

    [Theory]
    // The formulas Hardklor.exe reported for these library bins (see TestFiles/Hardklor/small_fticr.hk).
    [InlineData(54 * 5 * 6 - 1.007276466 * 6, 72, 102, 20, 21, 1)]  // m/z 270, z 6: H102C72N20O21S1
    [InlineData(71 * 5 * 3 - 1.007276466 * 3, 47, 92, 13, 14, 0)]   // m/z 355, z 3: H92C47N13O14
    [InlineData(5 - 1.007276466, 0, 4, 0, 0, 0)]                     // bin 1, z 1: H4
    [InlineData(1000.0, 45, 84, 12, 13, 0)]
    public void Averagine_IsHardkloRsComposition(double mass, int c, int h, int n, int o, int s) =>
        Assert.Equal(new ElementCounts(c, h, n, o, s), Averagine.Compute(mass));

    [Fact]
    public void ModelLibrary_BinsBy5MzAndChargeAndBuildsLazily()
    {
        var model = new CountingModel();
        var library = new HardklorModelLibrary(model, 2, 4, 5000);
        Assert.Equal(1001, library.BinCount);
        Assert.Same(EnvelopeModel.Empty, library.Get(2, 4.9));   // bin 0 is empty, as in Hardklor
        Assert.Null(library.Get(2, 5005.0));                      // past molecule_max_mz
        Assert.Same(EnvelopeModel.Empty, library.Get(2, -1.0));  // (int)(-0.2) is bin 0, as in the C++
        Assert.Null(library.Get(2, -5.1));
        Assert.NotNull(library.Get(2, 5004.9));                   // the last bin

        var a = library.Get(3, 600.0)!;
        var b = library.Get(3, 604.99)!;
        Assert.Same(a, b);                                        // one bin, built once
        Assert.NotSame(a, library.Get(3, 605.0));
        Assert.NotSame(a, library.Get(2, 600.0));
        Assert.Equal(4, model.Calls);                             // 5004.9, 600 (z 3), 605 (z 3), 600 (z 2)
        Assert.Equal(Averagine.Compute(120 * 5 * 3 - 1.007276466 * 3), a.Formula);
    }

    [Fact]
    public void ModelLibrary_ConvertsToMzAtChargeAndScalesTo100()
    {
        var library = new HardklorModelLibrary(new FixedModel(), 1, 3, 5000);
        var m = library.Get(2, 600)!;
        // FixedModel: neutral masses 1000, 1001, 1002 at 0.5, 1, 0.25.
        Assert.Equal(new[] { 501.007276466, 501.507276466, 502.007276466 }, m.Mz.Select(v => Math.Round(v, 9)));
        Assert.Equal(new[] { 50f, 100f, 25f }, m.Intensity);
        Assert.Equal(1.75f, m.Area);
        Assert.Equal(3, m.Size);
        var formula = Averagine.Compute(120 * 5 * 2 - 1.007276466 * 2);
        Assert.Equal(HardklorModelLibrary.MonoisotopicMass(formula), m.ZeroMass, 9);
    }

    [Fact]
    public void MonoisotopicMass_OfCHNOS()
    {
        Assert.Equal(12.0, HardklorModelLibrary.MonoisotopicMass(new ElementCounts(1, 0, 0, 0, 0)), 9);
        Assert.Equal(18.0105646837, HardklorModelLibrary.MonoisotopicMass(new ElementCounts(0, 2, 0, 1, 0)), 9);
        Assert.Equal(14.0030740048, HardklorModelLibrary.MonoisotopicMass(new ElementCounts(0, 0, 1, 0, 0)), 9);
        Assert.Equal(31.972071, HardklorModelLibrary.MonoisotopicMass(new ElementCounts(0, 0, 0, 0, 1)), 9);
    }

    // ---- PwizEnvelopeModel ----

    [Fact]
    public void PwizModel_SumsFineStructureIntoNominalPeaks()
    {
        // C100: binomial with p(13C) = 0.01084 (Dwight Matthews' value, in place of pwiz's NIST 0.0107).
        var peaks = Model.Distribution(new ElementCounts(100, 0, 0, 0, 0), 0.01);
        Assert.Equal(1200.0, peaks[0].Mass, 9);
        // Spaced by Monocle's 1.00286864 Da, not 13C's 1.0033548.
        Assert.Equal(1201.00286864, peaks[1].Mass, 7);
        Assert.Equal(1202.00573728, peaks[2].Mass, 7);
        // M+1 / M+0 = 100 p / (1 - p) = 1.0816, so M+1 is the most abundant (relative 1);
        // M+0 = (1 - p) / (100 p), M+2 / M+1 = 49.5 p / (1 - p).
        Assert.Equal(0.91250922509225092, peaks[0].Abundance, 9);
        Assert.Equal(1.0, peaks[1].Abundance);
        Assert.Equal(0.54246026932, peaks[2].Abundance, 9);
        Assert.Equal(0.194194, peaks[3].Abundance, 5);   // 1617 p^2 / (1 - p)^2
        Assert.Equal(0.01084, PwizEnvelopeModel.Carbon13Abundance);
        Assert.Equal(1.00286864, PwizEnvelopeModel.IsotopeSpacing);
    }

    [Fact]
    public void PwizModel_DropsPeaksBelowTheCutoffAndCaches()
    {
        var formula = Averagine.Compute(1200);
        var all = Model.Distribution(formula, 0.0001);
        var kept = Model.Distribution(formula, 0.05);
        Assert.True(all.Count > kept.Count);
        Assert.All(kept, p => Assert.True(p.Abundance >= 0.05));
        Assert.Same(kept, Model.Distribution(formula, 0.05));
        Assert.Empty(Model.Distribution(new ElementCounts(0, 0, 0, 0, 0), 0.01));
        // M+1 of averagine 1200: sum over ~54 C, 84 H, 15 N, 16 O; masses step about 1.0029 Da.
        Assert.Equal(1.0029, all[1].Mass - all[0].Mass, 3);
    }

    // ---- Ported pieces ----

    [Theory]
    [InlineData(250, true, 1)]
    [InlineData(250, false, 2)]
    [InlineData(300, true, 2)]
    [InlineData(300, false, 2)]
    [InlineData(50, true, 0)]
    [InlineData(450, false, 3)]
    [InlineData(100, false, 0)]
    [InlineData(400, true, 3)]
    [InlineData(101, true, 0)]
    [InlineData(399, false, 3)]
    [InlineData(199, false, 1)]
    [InlineData(201, true, 1)]
    public void BinarySearch_FindsTheNeighborOnOneSide(double mz, bool floor, int expected) =>
        Assert.Equal(expected, HardklorFinder.BinarySearch(new double[] { 100, 200, 300, 400 }, 4, mz, floor));

    [Fact]
    public void BinarySearch_LongerSpectrum()
    {
        var s = Enumerable.Range(0, 11).Select(i => 100.0 + 10 * i).ToArray(); // 100..200
        Assert.Equal(3, HardklorFinder.BinarySearch(s, s.Length, 135, true));
        Assert.Equal(4, HardklorFinder.BinarySearch(s, s.Length, 135, false));
        Assert.Equal(8, HardklorFinder.BinarySearch(s, s.Length, 181, true));
        Assert.Equal(9, HardklorFinder.BinarySearch(s, s.Length, 181, false));
        Assert.Equal(1, HardklorFinder.BinarySearch(s, s.Length, 110, true));
    }

    [Theory]
    [InlineData(new float[] { 100, 50 }, new float[] { 200, 100 }, 1.0)]
    [InlineData(new float[] { 3, 4 }, new float[] { 4, 3 }, 0.96)]
    [InlineData(new float[] { 100, 0 }, new float[] { 0, 100 }, 0.0)]
    [InlineData(new float[] { 0, 0 }, new float[] { 1, 2 }, 0.0)]
    [InlineData(new float[] { 1, 2 }, new float[] { 0, 0 }, 0.0)]
    [InlineData(new float[] { 1, 1, 1 }, new float[] { 1, 1, 0 }, 0.81649658092772603)]
    public void LinReg_IsTheCosine(float[] mer, float[] obs, double expected) =>
        Assert.Equal(expected, HardklorFinder.LinReg(mer, obs, mer.Length), 6);

    [Fact]
    public void LinReg_UsesOnlyCount()
    {
        Assert.Equal(1.0, HardklorFinder.LinReg(new float[] { 1, 2, 5 }, new float[] { 2, 4, 1 }, 2), 9);
        Assert.Equal(0.0, HardklorFinder.LinReg(new float[] { -1, -2 }, new float[] { 1, 2 }, 2), 9);
    }

    [Theory]
    [InlineData(new[] { 500.0, 501.003 }, new[] { 2 }, new[] { 1 })]
    [InlineData(new[] { 500.0, 500.5017 }, new[] { 2 }, new[] { 2 })]
    [InlineData(new[] { 500.0, 500.3344 }, new[] { 2 }, new[] { 3 })]
    [InlineData(new[] { 500.0, 500.1434 }, new[] { 2 }, new[] { 7 })]
    [InlineData(new[] { 500.0, 500.125 }, new[] { 2 }, new int[0])]            // charge 8 is out of range
    [InlineData(new[] { 500.0, 500.4 }, new[] { 2 }, new int[0])]              // 1/0.4 = 2.5: between charges
    [InlineData(new[] { 500.0, 501.2 }, new[] { 2 }, new int[0])]              // beyond 1.1 m/z
    [InlineData(new[] { 499.5, 500.0 }, new[] { 2 }, new int[0])]              // nothing to the right
    [InlineData(new[] { 499.5, 500.0, 501.0 }, new[] { 2 }, new[] { 1, 2 })]   // left adds charges once right matched
    [InlineData(new[] { 499.0, 499.6667, 500.0, 500.5 }, new[] { 2 }, new[] { 1, 2, 3 })]  // left: 0.333 -> 3, 1.0 -> 1
    public void QuickCharge_FindsSpacingsOfStrongPeaks(double[] mz, int[] indexOfBase, int[] expected)
    {
        var inten = mz.Select(_ => 100f).ToArray();
        var charges = new List<int>();
        var index = Array.IndexOf(mz, 500.0);
        Assert.True(index >= 0 && indexOfBase.Length == 1);
        HardklorFinder.QuickCharge(mz, inten, mz.Length, index, 1, 7, new bool[8], charges);
        Assert.Equal(expected, charges);
    }

    [Fact]
    public void QuickCharge_IgnoresPeaksBelowAQuarterAndDuplicates()
    {
        var mz = new[] { 499.5, 500.0, 500.0, 500.5, 501.0 };
        var inten = new[] { 100f, 100f, 100f, 24.9f, 25f };
        var charges = new List<int>();
        HardklorFinder.QuickCharge(mz, inten, mz.Length, 1, 1, 7, new bool[8], charges);
        Assert.Equal(new[] { 1, 2 }, charges);   // 501.0 gives 1 on the right; 499.5 gives 2 on the left
        HardklorFinder.QuickCharge(mz, inten, mz.Length, 1, 2, 7, new bool[8], charges);
        Assert.Empty(charges);                   // charge 1 out of range: nothing on the right
    }

    [Fact]
    public void PeakMatcher_MatchesAndScoresAndReportsOverlap()
    {
        var vMR = new HardklorFinder.Aligned();
        vMR.Set(new EnvelopeModel(default, 0, new[] { 500.0, 500.5, 501.0, 501.5 }, new[] { 100f, 60f, 20f, 5f }, 1.85f), 0);
        var mz = new[] { 499.0, 500.001, 500.5, 501.0005, 501.5, 503 };
        var inten = new[] { 10f, 1000f, 600f, 200f, 1500f, 10f };
        var mask = new bool[6];
        mask[2] = true; // the base peak of an earlier envelope
        var match = new HardklorFinder.Match();
        var mer = new float[8];
        var obs = new float[8];
        var corr = HardklorFinder.PeakMatcher(vMR, mz, inten, 6, mask, 499.9, 501.6, 0.01, 1, match, out var overlap, mer, obs);
        Assert.Equal(new[] { 1, 2, 3, 4 }, match.Index);
        Assert.Equal(new[] { 1f, 0.6f, 0.2f, 0.05f }, match.Peak);
        Assert.Equal(2, overlap);             // matched at 60%, above 50
        // The last peak (5% expected, 1500 observed) pulls the correlation below 0.9 and is trimmed.
        Assert.Equal(3, match.Count);
        Assert.Equal(1.0, corr, 6);

        mask[2] = false;
        mask[3] = true;                       // matched only at 20%: not an overlap
        HardklorFinder.PeakMatcher(vMR, mz, inten, 6, mask, 499.9, 501.6, 0.01, 1, match, out overlap, mer, obs);
        Assert.Equal(-1, overlap);

        // A poor last peak that is expected above 50% is not trimmed.
        vMR.Set(new EnvelopeModel(default, 0, new[] { 500.0, 500.5, 501.0, 501.5 }, new[] { 100f, 60f, 55f, 51f }, 2.66f), 0);
        corr = HardklorFinder.PeakMatcher(vMR, mz, inten, 6, new bool[6], 499.9, 501.6, 0.01, 1, match, out _, mer, obs);
        Assert.Equal(4, match.Count);
        // mer (100, 60, 55, 51) . obs (1000, 600, 200, 1500)
        Assert.Equal((100.0 * 1000 + 60 * 600 + 55 * 200 + 51 * 1500)
            / Math.Sqrt((100.0 * 100 + 60 * 60 + 55 * 55 + 51 * 51) * (1000.0 * 1000 + 600 * 600 + 200 * 200 + 1500 * 1500)), corr, 6);
    }

    [Fact]
    public void PeakMatcher_KeepsTheClosestPeakWithinHalfAWidth()
    {
        var vMR = new HardklorFinder.Aligned();
        vMR.Set(new EnvelopeModel(default, 0, new[] { 500.0, 500.5 }, new[] { 100f, 50f }, 1.5f), 0);
        // Two candidates near 500.5: 500.497 is closer than 500.504; 499.992 is just outside 0.005 of 500.0.
        var mz = new[] { 499.992, 500.0004, 500.497, 500.504 };
        var inten = new[] { 50f, 100f, 50f, 70f };
        var match = new HardklorFinder.Match();
        var corr = HardklorFinder.PeakMatcher(vMR, mz, inten, 4, new bool[4], 499.9, 500.6, 0.005, 1, match, out _,
            new float[4], new float[4]);
        Assert.Equal(new[] { 1, 2 }, match.Index);
        Assert.Equal(1.0, corr, 6);
        // Starting from the right end, the left search still finds the closest.
        HardklorFinder.PeakMatcher(vMR, mz, inten, 4, new bool[4], 499.9, 500.6, 0.005, 3, match, out _,
            new float[4], new float[4]);
        Assert.Equal(new[] { 1, 2 }, match.Index);
        // Bounds limit the search: with upper below 500.49 the second peak is missing (below 50%, ignored).
        corr = HardklorFinder.PeakMatcher(vMR, mz, inten, 4, new bool[4], 499.9, 500.49, 0.005, 1, match, out _,
            new float[4], new float[4]);
        Assert.Equal(new[] { 1 }, match.Index);
        Assert.Equal(0.0, corr);
    }

    [Fact]
    public void PeakMatcher_CountsMissingStrongPeaksAsZero()
    {
        var vMR = new HardklorFinder.Aligned();
        vMR.Set(new EnvelopeModel(default, 0, new[] { 500.0, 500.5, 501.0 }, new[] { 70f, 100f, 60f }, 2.3f), 0);
        // No peak at 500.5 (100%): it counts as an observed zero and stops the match.
        var mz = new[] { 500.0, 501.0 };
        var inten = new[] { 700f, 600f };
        var match = new HardklorFinder.Match();
        var corr = HardklorFinder.PeakMatcher(vMR, mz, inten, 2, new bool[2], 499.9, 501.1, 0.01, 0, match, out _,
            new float[4], new float[4]);
        Assert.Equal(new[] { 0 }, match.Index);
        Assert.Equal(1, match.Count);
        Assert.Equal(0.0, corr);              // fewer than two matched peaks

        // Solved peaks (intensity below 0.1) match but count as zero.
        mz = new[] { 500.0, 500.5, 501.0 };
        inten = new[] { -700f, 1000f, 600f };
        corr = HardklorFinder.PeakMatcher(vMR, mz, inten, 3, new bool[3], 499.9, 501.1, 0.01, 1, match, out _,
            new float[4], new float[4]);
        Assert.Equal(new[] { 0, 1, 2 }, match.Index);
        Assert.Equal(2, match.Count);
        // mer (70, 100, 60) . obs (0, 1000, 600)
        Assert.Equal((100.0 * 1000 + 60 * 600) / Math.Sqrt((70.0 * 70 + 100 * 100 + 60 * 60) * (1000.0 * 1000 + 600 * 600)), corr, 6);
    }

    [Fact]
    public void PeakMatcherB_StopsAtTheFirstMissingPeak()
    {
        var vMR = new HardklorFinder.Aligned();
        vMR.Set(new EnvelopeModel(default, 0, new[] { 500.0, 500.5, 501.0, 501.5 }, new[] { 100f, 60f, 20f, 10f }, 1.9f), 0);
        var mz = new[] { 500.0, 500.5, 501.5 };
        var inten = new[] { 1000f, 600f, 100f };
        var match = new HardklorFinder.Match();
        var corr = HardklorFinder.PeakMatcherB(vMR, mz, inten, 3, 499.9, 501.6, 0.01, 0, match, new float[4], new float[4]);
        Assert.Equal(new[] { 0, 1 }, match.Index);
        Assert.Equal(2, match.Count);
        Assert.Equal(1.0, corr, 6);

        // A poor third peak is trimmed while the correlation is below 0.9.
        mz = new[] { 500.0, 500.5, 501.0 };
        inten = new[] { 1000f, 100f, 1000f };
        corr = HardklorFinder.PeakMatcherB(vMR, mz, inten, 3, 499.9, 501.6, 0.01, 0, match, new float[4], new float[4]);
        Assert.Equal(new[] { 0, 1, 2 }, match.Index);
        Assert.Equal(2, match.Count);
        Assert.Equal((100.0 * 1000 + 60 * 100) / Math.Sqrt((100.0 * 100 + 60 * 60) * (1000.0 * 1000 + 100 * 100)), corr, 6);
    }

    // ---- The finder on synthetic spectra ----

    /// <summary>An averagine-like envelope at charge 2 from m/z 600.3, among noise.</summary>
    private static (double[] Mz, double[] Intensity) SyntheticSpectrum()
    {
        var peaks = new List<(double, double)>
        {
            (450.2, 300), (590.11, 500), (599.0, 200),
            (600.3, 100000), (600.3 + 1.00290 / 2, 64000), (600.3 + 2 * 1.00290 / 2, 25500), (600.3 + 3 * 1.00290 / 2, 7400),
            (612.7, 800), (700.4, 150),
        };
        return (peaks.Select(p => p.Item1).ToArray(), peaks.Select(p => p.Item2).ToArray());
    }

    [Fact]
    public void Find_SyntheticEnvelope()
    {
        var finder = new HardklorFinder(HardklorSettings.Default, Model);
        var (mz, intensity) = SyntheticSpectrum();
        var found = finder.Find(mz, intensity);
        var e = Assert.Single(found);
        Assert.Equal(2, e.Charge);
        Assert.Equal(600.3, e.BasePeakMz);
        Assert.Equal(new[] { 3, 4, 5, 6 }, e.PeakIndices);
        // The monoisotopic peak is the base peak: 2 * (600.3 - proton).
        Assert.Equal(1198.585447, e.MonoMass, 3);
        Assert.True(e.Correlation > 0.999);
        Assert.Equal(599.0, e.LowMz);
        Assert.Equal(612.7, e.HighMz);
        // distribution_area: base peak times the model's summed relative abundance (about 1.95 here).
        Assert.InRange(e.Intensity, 190000f, 200000f);

        var heights = new HardklorFinder(HardklorSettings.Default with { DistributionArea = false }, Model).Find(mz, intensity);
        Assert.Equal(100000f, Assert.Single(heights).Intensity);
    }

    [Fact]
    public void Find_RespectsCorrelationAndChargeRange()
    {
        var (mz, intensity) = SyntheticSpectrum();
        Assert.Empty(new HardklorFinder(HardklorSettings.Default with { MinCharge = 3 }, Model).Find(mz, intensity));
        Assert.Empty(new HardklorFinder(HardklorSettings.Default with { Correlation = 0.99999 }, Model).Find(mz, intensity));
        // M+2 at 2000 instead of 25500: correlation 0.982, below Skyline's 0.9877.
        intensity[5] = 2000;
        Assert.Empty(new HardklorFinder(HardklorSettings.Default, Model).Find(mz, intensity));
        Assert.Single(new HardklorFinder(HardklorSettings.Default with { Correlation = 0.97 }, Model).Find(mz, intensity));
    }

    [Fact]
    public void Find_IsSafeFromSeveralThreads()
    {
        var finder = new HardklorFinder(HardklorSettings.Default with { Instrument = HardklorInstrument.Fticr, Resolution = 100000 }, Model);
        var spectra = SmallMs1();
        var serial = spectra.Select(s => Describe(new HardklorFinder(finder.Settings, Model).Find(s.Mz, s.Intensity))).ToArray();
        var parallel = new string[spectra.Count * 8];
        Parallel.For(0, parallel.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => parallel[i] = Describe(finder.Find(spectra[i % spectra.Count].Mz, spectra[i % spectra.Count].Intensity)));
        for (var i = 0; i < parallel.Length; i++)
            Assert.Equal(serial[i % spectra.Count], parallel[i]);

        static string Describe(IReadOnlyList<IsotopeEnvelope> found) => string.Join("\n", found.Select(e =>
            FormattableString.Invariant($"{e.MonoMass:R} {e.Charge} {e.Intensity:R} {e.Correlation:R} {string.Join(",", e.PeakIndices)}")));
    }

    [Fact]
    public void Find_TwoEnvelopes_AndHardklorsTieQuirk()
    {
        // Two copies of the synthetic envelope (without its noise), 3 m/z apart: the same library bin.
        var rel = new[] { 100000.0, 64000, 25500, 7400 };
        double[] Mz(double start) => rel.Select((_, k) => start + k * 1.00290 / 2).ToArray();
        var mz = Mz(600.3).Concat(Mz(603.3)).ToArray();
        var finder = new HardklorFinder(HardklorSettings.Default, Model);

        // The second copy 20% weaker: both found, in m/z order, with their own peaks.
        var found = finder.Find(mz, rel.Concat(rel.Select(v => v * 0.8)).ToArray());
        Assert.Equal(2, found.Count);
        Assert.Equal(new[] { 600.3, 603.3 }, found.Select(e => e.BasePeakMz));
        Assert.Equal(new[] { 0, 1, 2, 3 }, found[0].PeakIndices);
        Assert.Equal(new[] { 4, 5, 6, 7 }, found[1].PeakIndices);

        // Equal intensities: Hardklor takes the next peak strictly below the last one, so the second copy's
        // base peak, as intense as the first's, is never a starting point.
        found = finder.Find(mz, rel.Concat(rel).ToArray());
        Assert.DoesNotContain(found, e => e.BasePeakMz == 603.3);
        Assert.Equal(600.3, found[0].BasePeakMz);
    }

    [Fact]
    public void Find_KeepsAtMostDepthEnvelopesPerWindow()
    {
        // Three interleaved envelopes at charge 2 whose base peaks (600.3, 600.4, 600.7) lie within 0.5 m/z:
        // RefineHits keeps the Depth most intense.
        var peaks = new List<(double Mz, double Intensity)>();
        void Add(double start, double scale)
        {
            var rel = new[] { 100.0, 64, 25.5, 7.4 };
            for (var k = 0; k < rel.Length; k++)
                peaks.Add((start + k * 1.00290 / 2, rel[k] * scale));
        }
        Add(600.30, 1000);
        Add(600.40, 800);
        Add(600.70, 600);
        peaks.Sort();
        var mz = peaks.Select(p => p.Mz).ToArray();
        var intensity = peaks.Select(p => p.Intensity).ToArray();
        var depth2 = new HardklorFinder(HardklorSettings.Default, Model).Find(mz, intensity);
        var depth3 = new HardklorFinder(HardklorSettings.Default with { Depth = 3 }, Model).Find(mz, intensity);
        var depth1 = new HardklorFinder(HardklorSettings.Default with { Depth = 1 }, Model).Find(mz, intensity);
        Assert.Equal(new[] { 600.30, 600.40, 600.70 }, depth3.Select(e => e.BasePeakMz));
        Assert.Equal(new[] { 600.30, 600.40 }, depth2.Select(e => e.BasePeakMz));
        Assert.Equal(new[] { 600.30 }, depth1.Select(e => e.BasePeakMz));
    }

    [Fact]
    public void Find_EmptyAndInvalidInput()
    {
        var finder = new HardklorFinder(HardklorSettings.Default, Model);
        Assert.Empty(finder.Find(Array.Empty<double>(), Array.Empty<double>()));
        Assert.Empty(finder.Find(new[] { 500.0 }, new[] { 1000.0 }));
        Assert.Empty(finder.Find(new[] { 500.0, 500.5 }, new[] { 0.5, 0.25 }));   // all below 1: no low point
        Assert.Throws<ArgumentException>(() => finder.Find(new[] { 500.0 }, Array.Empty<double>()));
        Assert.Throws<ArgumentNullException>(() => finder.Find(null!, Array.Empty<double>()));
        Assert.Throws<ArgumentNullException>(() => finder.Find(Array.Empty<double>(), null!));
    }

    [Fact]
    public void Find_DoesNotChangeItsInput()
    {
        var finder = new HardklorFinder(HardklorSettings.Default, Model);
        var (mz, intensity) = SyntheticSpectrum();
        var copy = intensity.ToArray();
        finder.Find(mz, intensity);
        Assert.Equal(copy, intensity);
    }

    // ---- Against Hardklor.exe ----

    /// <summary>One envelope line of a Hardklor .hk file.</summary>
    private sealed record HkPeptide(int Scan, double MonoMass, int Charge, double Intensity, double BaseMz, double Corr);

    private static List<HkPeptide> ReadHk(string path)
    {
        var result = new List<HkPeptide>();
        var scan = 0;
        foreach (var line in File.ReadLines(path))
        {
            var f = line.Split('\t');
            if (f[0] == "S")
                scan = int.Parse(f[1], CultureInfo.InvariantCulture);
            else if (f[0] == "P")
                result.Add(new HkPeptide(scan, double.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture),
                    double.Parse(f[3], CultureInfo.InvariantCulture), double.Parse(f[4], CultureInfo.InvariantCulture),
                    double.Parse(f[8], CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static string HkFile(string name) => Path.Combine(TestData.RepoRoot, "tests", "Glimpse.Tests", "TestFiles", "Hardklor", name);

    /// <summary>The MS1 spectra of small.RAW, vendor-centroided: what Hardklor.exe read from msconvert's mzML.</summary>
    private static List<(int Scan, double[] Mz, double[] Intensity)> SmallMs1()
    {
        using var source = PwizMsDataSource.Open(TestData.SmallRaw);
        var spectra = new List<(int, double[], double[])>();
        for (var i = 0; i < source.SpectrumCount; i++)
        {
            var s = source.ReadSpectrum(i, centroid: true);
            if (s.Header.MsLevel == 1)
                spectra.Add((s.Details.ScanNumber ?? -1, s.Mz, s.Intensity));
        }
        return spectra;
    }

    /// <summary>
    /// PwizEnvelopeModel's abundances at the masses Hardklor's Mercury gives them in its fast mode
    /// (CMercury8::Mercury and ConvertMass): evenly spaced by sqrt(mass variance / integer-mass variance),
    /// anchored so the mean is the average mass. Only to check this port against Hardklor.exe exactly.
    /// </summary>
    private sealed class MercurySpacedModel : IEnvelopeModel
    {
        private readonly PwizEnvelopeModel _inner = new();

        public IReadOnlyList<IsotopePeak> Distribution(ElementCounts f, double cutoff)
        {
            var peaks = _inner.Distribution(f, cutoff);
            double mw = 0, intMw = 0, molVar = 0, intVar = 0, mono = 0;
            foreach (var (element, n) in new[]
            {
                (global::Pwiz.Util.Chemistry.ElementType.C, f.C), (global::Pwiz.Util.Chemistry.ElementType.H, f.H),
                (global::Pwiz.Util.Chemistry.ElementType.N, f.N), (global::Pwiz.Util.Chemistry.ElementType.O, f.O),
                (global::Pwiz.Util.Chemistry.ElementType.S, f.S),
            })
            {
                var isotopes = PwizEnvelopeModel.Isotopes(element).Where(i => i.Abundance > 0).ToArray();
                double m1 = isotopes.Sum(i => i.Abundance * i.Mass), m2 = isotopes.Sum(i => i.Abundance * i.Mass * i.Mass);
                double r1 = isotopes.Sum(i => i.Abundance * Math.Round(i.Mass)), r2 = isotopes.Sum(i => i.Abundance * Math.Round(i.Mass) * Math.Round(i.Mass));
                mw += n * m1;
                intMw += n * r1;
                molVar += n * (m2 - m1 * m1);
                intVar += n * (r2 - r1 * r1);
                mono += n * isotopes.Min(i => i.Mass);
            }
            var ratio = intVar == 0 ? 1 : Math.Sqrt(molVar) / Math.Sqrt(intVar);
            var monoInt = Math.Round(mono);
            return peaks.Select(p => new IsotopePeak(mw + (monoInt + Math.Round(p.Mass - mono) - intMw) * ratio, p.Abundance)).ToArray();
        }
    }

    /// <summary>
    /// The port decides exactly as Hardklor.exe does: given Hardklor's isotope peak masses and the same
    /// abundances (small_fticr_matthews.hk), it finds the same envelopes, on the same base peaks with the same
    /// charges. Every difference left in <see cref="AgreesWithHardklorExe"/> is the isotope model's.
    /// </summary>
    [Fact]
    public void DecidesExactlyAsHardklorExe()
    {
        var expected = ReadHk(HkFile("small_fticr_matthews.hk")).Select(h => (h.Scan, h.Charge, h.BaseMz)).Order().ToList();
        var finder = new HardklorFinder(HardklorSettings.Default with { Instrument = HardklorInstrument.Fticr, Resolution = 100000 }, new MercurySpacedModel());
        var ours = SmallMs1().SelectMany(s => finder.Find(s.Mz, s.Intensity).Select(e => (s.Scan, e.Charge, e.BasePeakMz))).Order().ToList();
        Assert.Equal(1014, expected.Count);
        Assert.Equal(expected.Count, ours.Count);
        // Hardklor.exe writes the base peak to 4 decimals.
        Assert.All(expected.Zip(ours), p => Assert.True(p.First.Scan == p.Second.Scan && p.First.Charge == p.Second.Charge
            && Math.Abs(p.First.BaseMz - p.Second.BasePeakMz) <= 0.00005, $"{p.First} against {p.Second}"));
    }

    /// <remarks>
    /// small_orbitrap.hk is Skyline's configuration. Hardklor.exe and PwizEnvelopeModel use the same 13C
    /// abundance (0.01084, Dwight Matthews) but place isotope peaks differently: Hardklor's Mercury spaces them
    /// by about 1.0021 Da, PwizEnvelopeModel by Monocle's 1.00286864 Da (closer to observed isotope peaks), so a
    /// few envelopes near the matching tolerance or the correlation threshold fall on different sides of it, and
    /// each difference changes what is left of the spectrum for the next envelope. Measured: Hardklor.exe 1043,
    /// ours 1049, 97.8% of Hardklor.exe's found and 97.3% of ours matched. small_fticr_matthews.hk gave
    /// Hardklor.exe PwizEnvelopeModel's exact abundances (TestFiles/Hardklor/README.md): 97.8% and 98.6%, with
    /// identical intensities. With Hardklor's spacing the port finds exactly Hardklor.exe's envelopes (see
    /// <see cref="DecidesExactlyAsHardklorExe"/>), so these differences are the isotope model's alone.
    /// <para>The last values pin this port's own output (envelopes, matched, peaks, sum of peak indices, sum
    /// of masses, sum of intensities), recorded once it agreed with Hardklor.exe as above: the agreement
    /// thresholds alone let small changes to the algorithm through. A new Thermo reader that centroids
    /// small.RAW differently changes them too; re-record them only after checking the agreement.</para>
    /// </remarks>
    [Theory]
    [InlineData("small_orbitrap.hk", HardklorInstrument.Orbitrap, 60000, 0.975, 0.97, 0.005,
        1049, 1021, 2848, 2924373L, 1270890.180633624, 100364860.61022949)]
    [InlineData("small_fticr_matthews.hk", HardklorInstrument.Fticr, 100000, 0.975, 0.98, 0.001,
        1006, 992, 2718, 2829507L, 1203542.6905437617, 98438767.89886475)]
    public void AgreesWithHardklorExe(string hkName, HardklorInstrument instrument, double resolution,
        double minRecall, double minPrecision, double intensityTolerance,
        int pinnedCount, int pinnedMatched, int pinnedPeaks, long pinnedIndexSum, double pinnedMassSum, double pinnedIntensitySum)
    {
        var expected = ReadHk(HkFile(hkName));
        var finder = new HardklorFinder(HardklorSettings.Default with { Instrument = instrument, Resolution = resolution }, Model);
        var spectra = SmallMs1();
        Assert.Equal(14, spectra.Count);
        Assert.Equal(expected.Select(p => p.Scan).Distinct().Count(), spectra.Count);

        var ours = new List<HkPeptide>();
        long peakCount = 0, peakIndexSum = 0;
        foreach (var (scan, mz, intensity) in spectra)
            foreach (var e in finder.Find(mz, intensity))
            {
                ours.Add(new HkPeptide(scan, e.MonoMass, e.Charge, e.Intensity, e.BasePeakMz, e.Correlation));
                peakCount += e.PeakIndices.Length;
                peakIndexSum += e.PeakIndices.Sum();
            }
        var massSum = ours.Sum(o => o.MonoMass);
        var intensitySum = ours.Sum(o => o.Intensity);

        static bool Same(HkPeptide a, HkPeptide b) =>
            a.Scan == b.Scan && a.Charge == b.Charge && Math.Abs(a.MonoMass - b.MonoMass) <= a.MonoMass * 10e-6;

        var byScan = ours.ToLookup(p => p.Scan);
        var matched = expected.Where(h => byScan[h.Scan].Any(o => Same(h, o))).ToList();
        var expectedByScan = expected.ToLookup(p => p.Scan);
        var oursMatched = ours.Count(o => expectedByScan[o.Scan].Any(h => Same(h, o)));
        var recall = (double)matched.Count / expected.Count;
        var precision = (double)oursMatched / ours.Count;

        // Mass offsets and intensity ratios of the matched pairs, and how many share the base peak.
        var ppm = new List<double>();
        var intensityRatio = new List<double>();
        var sameBase = 0;
        foreach (var h in matched)
        {
            var o = byScan[h.Scan].Where(o => Same(h, o)).MinBy(o => Math.Abs(o.MonoMass - h.MonoMass))!;
            ppm.Add((o.MonoMass - h.MonoMass) / h.MonoMass * 1e6);
            intensityRatio.Add(o.Intensity / h.Intensity);
            if (Math.Abs(o.BaseMz - h.BaseMz) < 1e-3) sameBase++;
        }
        ppm.Sort();
        intensityRatio.Sort();
        double Pct(List<double> v, double p) => v[(int)(p * (v.Count - 1))];
        _out.WriteLine(FormattableString.Invariant(
            $"{hkName}: Hardklor.exe {expected.Count}, ours {ours.Count}; Hardklor.exe's found {matched.Count} ({recall:P1}), ours matched {oursMatched} ({precision:P1}); same base peak {sameBase}"));
        _out.WriteLine(FormattableString.Invariant(
            $"  peaks {peakCount}, index sum {peakIndexSum}, mass sum {massSum:R}, intensity sum {intensitySum:R}"));
        _out.WriteLine(FormattableString.Invariant(
            $"  mass offset ppm: 5% {Pct(ppm, 0.05):F2}, median {Pct(ppm, 0.5):F2}, 95% {Pct(ppm, 0.95):F2}; intensity ratio: 5% {Pct(intensityRatio, 0.05):F4}, median {Pct(intensityRatio, 0.5):F4}, 95% {Pct(intensityRatio, 0.95):F4}"));

        Assert.True(recall >= minRecall, $"recall {recall:P1}");
        Assert.True(precision >= minPrecision, $"precision {precision:P1}");
        Assert.True(sameBase >= 0.99 * matched.Count, $"same base peak {sameBase} of {matched.Count}");
        // Masses agree to a fraction of a ppm (Mercury's peak masses are close to, not exactly, the true ones).
        Assert.InRange(Pct(ppm, 0.05), -0.5, 0);
        Assert.InRange(Pct(ppm, 0.5), -0.2, 0.2);
        Assert.InRange(Pct(ppm, 0.95), 0, 0.5);
        Assert.InRange(Pct(intensityRatio, 0.05), 1 - 2 * intensityTolerance, 1.0001);
        Assert.InRange(Pct(intensityRatio, 0.95), 1 - intensityTolerance, 1 + intensityTolerance);

        Assert.Equal(pinnedCount, ours.Count);
        Assert.Equal(pinnedMatched, oursMatched);
        Assert.Equal(pinnedPeaks, peakCount);
        Assert.Equal(pinnedIndexSum, peakIndexSum);
        Assert.Equal(pinnedMassSum, massSum, pinnedMassSum * 1e-12);
        Assert.Equal(pinnedIntensitySum, intensitySum, pinnedIntensitySum * 1e-9);
    }
}

/// <summary>Counts the distributions asked for; returns a fixed one.</summary>
internal sealed class CountingModel : IEnvelopeModel
{
    public int Calls;

    public IReadOnlyList<IsotopePeak> Distribution(ElementCounts formula, double abundanceCutoff)
    {
        Interlocked.Increment(ref Calls);
        return new[] { new IsotopePeak(1000, 1) };
    }
}

/// <summary>Neutral masses 1000, 1001, 1002 at relative abundances 0.5, 1, 0.25, whatever the formula.</summary>
internal sealed class FixedModel : IEnvelopeModel
{
    public IReadOnlyList<IsotopePeak> Distribution(ElementCounts formula, double abundanceCutoff) =>
        new[] { new IsotopePeak(1000, 0.5), new IsotopePeak(1001, 1), new IsotopePeak(1002, 0.25) };
}
