using System.Globalization;
using Glimpse.Features;
using Xunit;

namespace Glimpse.Tests;

/// <summary>Glimpse's port of Kronik (envelopes linked across scans into persistent features).</summary>
public class KronikTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static EnvelopeKey E(double mass, int charge, float intensity) => new(mass, charge, intensity);

    private static IReadOnlyList<EnvelopeKey>[] Scans(params EnvelopeKey[][] scans) => scans;

    [Fact]
    public void AnEnvelopeInThreeScansPersists()
    {
        var result = Kronik.Link(Scans(
            new[] { E(1000, 2, 10) },
            new[] { E(1000.004, 2, 30), E(1500, 3, 5) },     // 4 ppm away: the same feature
            new[] { E(1000, 2, 20) },
            new[] { E(2000, 2, 99) }), KronikSettings.Default);

        var f = Assert.Single(result.Features);
        Assert.Equal(new PersistentFeature(1000.004, 2, 1, 30, 0, 2, 3, 60), f);
        Assert.Equal(new[] { 0 }, result.FeatureOf[0]);
        Assert.Equal(new[] { 0, -1 }, result.FeatureOf[1]);
        Assert.Equal(new[] { 0 }, result.FeatureOf[2]);
        Assert.Equal(new[] { -1 }, result.FeatureOf[3]);   // one scan only
    }

    [Fact]
    public void OneMissedScanIsBridgedTwoEndTheFeature()
    {
        // Found in scans 0, 2 and 3 (one gap): persists. Scans 6 and 7 are two misses past scan 3: not linked.
        var x = E(800, 1, 50);
        var result = Kronik.Link(Scans(new[] { x }, Array.Empty<EnvelopeKey>(), new[] { x }, new[] { x },
            Array.Empty<EnvelopeKey>(), Array.Empty<EnvelopeKey>(), new[] { x }, new[] { x }), KronikSettings.Default);
        var f = Assert.Single(result.Features);
        Assert.Equal((0, 3, 3), (f.FirstScan, f.LastScan, f.ScanCount));
        Assert.Equal(new[] { -1 }, result.FeatureOf[6]);
        Assert.Equal(new[] { -1 }, result.FeatureOf[7]);   // only two scans together
        // With no gap allowed, scans 0 and 2 are apart: scans 2-3 alone are too few.
        Assert.Empty(Kronik.Link(Scans(new[] { x }, Array.Empty<EnvelopeKey>(), new[] { x }, new[] { x }),
            KronikSettings.Default with { GapTolerance = 0 }).Features);
    }

    [Fact]
    public void ChargeAndToleranceMustAgree()
    {
        // Same mass at another charge, and the same charge 11 ppm away: neither links.
        var result = Kronik.Link(Scans(
            new[] { E(1000, 2, 10) },
            new[] { E(1000, 3, 30), E(1000.011, 2, 20) },
            new[] { E(1000, 2, 20) }), KronikSettings.Default);
        Assert.Empty(result.Features);
        // A tighter minimum: two scans are enough.
        Assert.Single(Kronik.Link(Scans(new[] { E(1000, 2, 10) }, new[] { E(1000, 2, 20) }),
            KronikSettings.Default with { MinScans = 2 }).Features);
    }

    [Fact]
    public void TheMostIntenseMatchJoinsAndARejectedSeedLeavesItsNeighbors()
    {
        // Scan 1 has two candidates within tolerance: the more intense joins the feature.
        var result = Kronik.Link(Scans(
            new[] { E(1000, 2, 100) },
            new[] { E(1000.002, 2, 5), E(999.998, 2, 7) },
            new[] { E(1000, 2, 50) }), KronikSettings.Default);
        Assert.Equal(new[] { -1, 0 }, result.FeatureOf[1]);

        // The seed at scan 4 (alone with scan 3) is rejected; scan 3's envelope still joins the later feature
        // seeded at scan 1 (scans 1-3).
        var y = E(1200, 2, 10);
        var second = Kronik.Link(Scans(Array.Empty<EnvelopeKey>(), new[] { E(1200, 2, 20) }, new[] { y }, new[] { y },
            new[] { E(1200, 2, 90) }), KronikSettings.Default with { GapTolerance = 0 });
        // Seed 90 (scan 4) looks left: scans 3, 2, 1 all match -> it persists with all of them.
        var f = Assert.Single(second.Features);
        Assert.Equal((1, 4, 4, 4), (f.FirstScan, f.LastScan, f.ScanCount, f.BestScan));
    }

    [Fact]
    public void AScanWindowThatMissesTheFeatureIsSkippedNotAGap()
    {
        var x = E(1000, 2, 10);   // m/z 501.007
        var scans = Scans(new[] { x }, Array.Empty<EnvelopeKey>(), Array.Empty<EnvelopeKey>(), new[] { x }, new[] { x });
        // Scans 1 and 2 are SIM windows elsewhere: skipped, so scans 0, 3 and 4 link.
        var windows = new (double, double)[] { (0, 0), (700, 800), (300, 400), (0, 0), (400, 600) };
        Assert.Single(Kronik.Link(scans, KronikSettings.Default, windows).Features);
        Assert.Empty(Kronik.Link(scans, KronikSettings.Default).Features);   // two plain misses in a row
    }

    /// <summary>
    /// The same features as BullseyeSharp finds in a synthetic Hardklor file (TestFiles/make-kronik-synthetic.py
    /// made it; Skyline's BullseyeSharp.exe wrote the .bs.kro).
    /// </summary>
    [Fact]
    public void SameFeaturesAsBullseyeSharp()
    {
        var dir = Path.Combine(TestData.RepoRoot, "tests", "Glimpse.Tests", "TestFiles");
        var scanNumbers = new List<int>();
        var scans = new List<List<EnvelopeKey>>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "kronik-synthetic.hk")))
        {
            var t = line.Split('\t');
            if (t[0] == "S")
            {
                scanNumbers.Add(int.Parse(t[1], Inv));
                scans.Add(new List<EnvelopeKey>());
            }
            else
            {
                scans[^1].Add(new EnvelopeKey(double.Parse(t[1], Inv), int.Parse(t[2], Inv), (float)double.Parse(t[3], Inv)));
            }
        }
        var result = Kronik.Link(scans, KronikSettings.Default);

        // First scan, last scan, scans spanned (Kronik counts missed scans in between), charge, mass; then the
        // best intensity, which the .NET Framework BullseyeSharp wrote rounded to 7 digits its own way.
        var expected = File.ReadLines(Path.Combine(dir, "kronik-synthetic.hk.bs.kro")).Skip(1)
            .Select(l => l.Split('\t'))
            .Select(t => (Key: (int.Parse(t[1], Inv), int.Parse(t[2], Inv), int.Parse(t[3], Inv), int.Parse(t[4], Inv),
                Math.Round(double.Parse(t[5], Inv), 4)), Intensity: double.Parse(t[7], Inv)))
            .OrderBy(x => x.Key).ToList();
        var actual = result.Features
            .Select(f => (Key: (scanNumbers[f.FirstScan], scanNumbers[f.LastScan], f.LastScan - f.FirstScan + 1, f.Charge,
                Math.Round(f.MonoMass, 4)), Intensity: (double)f.BestIntensity))
            .OrderBy(x => x.Key).ToList();
        Assert.Equal(98, expected.Count);
        Assert.Equal(expected.Select(x => x.Key), actual.Select(x => x.Key));
        Assert.All(expected.Zip(actual), p => Assert.Equal(1.0, p.Second.Intensity / p.First.Intensity, 6));
    }
}
