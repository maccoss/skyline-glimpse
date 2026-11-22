using Glimpse.Core.Assignment;
using Glimpse.Core.Chromatograms;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Heatmaps;
using Glimpse.Core.Scans;
using Xunit;

namespace Glimpse.Tests;

/// <summary>
/// The document pass on a hand-built run where every expected number can be worked out by hand.
/// </summary>
public class DocumentSignalTests
{
    private readonly DocPrecursor _a;
    private readonly DocPrecursor _b;
    private readonly DocPrecursor _c;
    private readonly DocPrecursor _otherFile;
    private readonly DocumentModel _model;
    private readonly ScanIndex _index;
    private readonly DocumentSignals _signals;

    public DocumentSignalTests()
    {
        var p1 = new DocProtein("P1", null, null, "MoleculeGroup:/P1");
        var p2 = new DocProtein("P2", null, null, "MoleculeGroup:/P2");
        // A: 405 2+, peak 1.0-2.0, MS1 transitions M and M+1, fragments 300 and 301.
        _a = Docs.Precursor(p1, "AAAK", 2, 405.0, "run1.raw", 1.0, 2.0,
            ("precursor", 2, 405.0), ("precursor [M+1]", 2, 405.5017), ("y3", 1, 300.0), ("y2", 1, 301.0));
        // B: 406 2+, peak 1.0-2.5, no MS1 transitions (so M..M+2 are used), fragment 301 shared with A.
        _b = Docs.Precursor(p1, "CCCK", 2, 406.0, "run1.raw", 1.0, 2.5, ("y2", 1, 301.0));
        // C: 415 2+ in the other window, peak 0-0.5.
        _c = Docs.Precursor(p2, "DDDK", 2, 415.0, "run1.raw", 0.0, 0.5, ("y2", 1, 350.0));
        // A precursor whose peak is in a different file: ignored for this run.
        _otherFile = Docs.Precursor(p2, "EEEK", 2, 405.2, "run2.raw", 1.0, 2.0, ("y2", 1, 302.0));
        _model = Docs.Model(p1, p2);

        var source = new FakeSource()
            .Ms1(0.0, new[] { 405.0, 406.0, 500.0 }, new[] { 10.0, 20.0, 100.0 })                    // 0
            .Ms2(0.3, 400, 410, new[] { 300.0, 301.0, 302.0 }, new[] { 5.0, 7.0, 11.0 })              // 1
            .Ms2(0.6, 410, 420, new[] { 350.0 }, new[] { 3.0 })                                       // 2
            .Ms1(1.5, new[] { 405.0, 405.5017, 406.0, 500.0 }, new[] { 10.0, 4.0, 20.0, 100.0 })      // 3
            .Ms2(1.8, 400, 410, new[] { 300.0, 301.0, 302.0 }, new[] { 5.0, 7.0, 11.0 })              // 4
            .Ms2(1.9, 410, 420, new[] { 350.0 }, new[] { 3.0 })                                       // 5
            .Ms2(2.3, 400, 410, new[] { 300.0, 301.0 }, new[] { 5.0, 7.0 }, injectionMs: 20f)         // 6
            .Ms1(4.0, new[] { 405.0, 406.0 }, new[] { 10.0, 20.0 });                                  // 7
        var observer = new DocumentSignalObserver(_model, "run1.raw", DocumentSettings.Default, xicMarginMinutes: 1.0, maxColumns: 8);
        _index = ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        _signals = observer.Result!;
    }

    [Fact]
    public void CountsOnlyPrecursorsWithPeaksInThisFile()
    {
        Assert.Equal(3, _signals.PrecursorsInFile);
        Assert.False(_signals.Xics.ContainsKey(_otherFile));
        Assert.Equal("run1.raw", _signals.FileName);
    }

    [Fact]
    public void AssignedSignalCountsEachPeakOnce()
    {
        // Scan 0: A and B are only within the margin, not inside their peaks; C is inside its peak but
        // none of its isotopes are present. It still counts as eluting.
        Assert.Equal(0f, _signals.AssignedIntensity[0]);
        Assert.Equal(1, _signals.PeptideCount[0]);
        // Scan 1: C (other window) excluded; A/B outside their peaks.
        Assert.Equal(0f, _signals.AssignedIntensity[1]);
        // Scan 2: C is inside its peak at 0.6? No - its peak ends at 0.5.
        Assert.Equal(0f, _signals.AssignedIntensity[2]);
        // Scan 3 (MS1 1.5): A claims 405.0 and 405.5017; B claims 406.0 (its default M).
        Assert.Equal(34f, _signals.AssignedIntensity[3]);
        Assert.Equal(2, _signals.PeptideCount[3]);
        // Scan 4 (MS2 1.8): A claims 300 and 301, B claims 301 again: 5 + 7 = 12, not 19.
        Assert.Equal(12f, _signals.AssignedIntensity[4]);
        Assert.Equal(2, _signals.PeptideCount[4]);
        // Scan 5: C beyond its margin.
        Assert.Equal(0f, _signals.AssignedIntensity[5]);
        // Scan 6 (2.3): A's peak has ended; B still claims 301.
        Assert.Equal(7f, _signals.AssignedIntensity[6]);
        Assert.Equal(1, _signals.PeptideCount[6]);
        // Scan 7 (4.0): everyone is past their margin.
        Assert.Equal(0f, _signals.AssignedIntensity[7]);
    }

    [Fact]
    public void XicsCoverThePeakPlusMargin()
    {
        var a = _signals.Xics[_a];
        Assert.Equal(new[] { 0, 3 }, a.Ms1Scans);                // 4.0 is past 2.0 + 1.0
        Assert.Equal(new[] { 10f, 14f }, a.Ms1Values);
        Assert.Equal(new[] { 1, 4, 6 }, a.Ms2Scans);             // only window 400-410 scans
        Assert.Equal(new[] { 12f, 12f, 12f }, a.Ms2Values);

        var b = _signals.Xics[_b];
        Assert.Equal(new[] { 0, 3 }, b.Ms1Scans);
        Assert.Equal(new[] { 20f, 20f }, b.Ms1Values);           // 406.0 present; M+1/M+2 absent
        Assert.Equal(new[] { 7f, 7f, 7f }, b.Ms2Values);

        var c = _signals.Xics[_c];
        Assert.Equal(new[] { 2 }, c.Ms2Scans);                   // 0.6 <= 0.5 + 1.0; 1.9 is not
    }

    [Fact]
    public void HeatmapsReflectClaimsAndCounts()
    {
        Assert.False(_signals.Ms1Fraction.IsEmpty);
        Assert.False(_signals.Ms2Fraction.IsEmpty);
        // MS2 fraction for the 1.8 scan's column, window 400-410: 12 of 23.
        var grid = _signals.Ms2Fraction;
        var cell = grid.Values[grid.RowAt(405), grid.ColumnAt(1.8)];
        Assert.Equal(12f / 23f, cell, 4);
        // MS1 fraction at m/z 500 is 0 (never claimed); at 405 it is 1 in the 1.5 column.
        var ms1 = _signals.Ms1Fraction;
        Assert.Equal(0f, ms1.Values[ms1.RowAt(500.5), ms1.ColumnAt(1.5)]);
        Assert.Equal(1f, ms1.Values[ms1.RowAt(405.2), ms1.ColumnAt(1.5)]);
        // Peptides per spectrum, MS2 window 400-410 at 1.8: 2.
        var count = _signals.Ms2Count;
        Assert.Equal(2f, count.Values[count.RowAt(405), count.ColumnAt(1.8)]);
        Assert.False(_signals.Ms1Count.IsEmpty);
    }

    [Fact]
    public void XicTracesPerPeptideProteinAndSum()
    {
        var peptides = _model.Peptides.Where(p => p.Protein.Name == "P1").ToList();
        Assert.Empty(XicTraces.Build(_index, _signals, peptides, XicMode.Off, false, IntensityUnits.AsRecorded));
        Assert.Empty(XicTraces.Build(_index, _signals, Array.Empty<DocPeptide>(), XicMode.Summed, false, IntensityUnits.AsRecorded));

        var perPeptide = XicTraces.Build(_index, _signals, peptides, XicMode.PerPeptide, false, IntensityUnits.AsRecorded);
        Assert.Equal(2, perPeptide.Count);
        var a = perPeptide.Single(t => t.Owner == _a.Peptide);
        Assert.Equal(new[] { 0.3, 1.8, 2.3 }, a.Times);
        Assert.Equal(new[] { 12.0, 12.0, 12.0 }, a.Values);

        var perProtein = Assert.Single(XicTraces.Build(_index, _signals, peptides, XicMode.PerProtein, false, IntensityUnits.AsRecorded));
        Assert.Same(_a.Peptide.Protein, perProtein.Owner);
        Assert.Equal(0.3, perProtein.Times[0], 9);
        Assert.Equal(19.0, perProtein.Values[0], 6);           // 12 (A) + 7 (B)

        var sum = Assert.Single(XicTraces.Build(_index, _signals, _model.Peptides, XicMode.Summed, false, IntensityUnits.AsRecorded));
        Assert.Null(sum.Owner);
        Assert.Contains("3 peptides", sum.Name);

        var ms1 = XicTraces.Build(_index, _signals, peptides, XicMode.PerPeptide, true, IntensityUnits.AsRecorded);
        Assert.Equal(new[] { 10.0, 14.0 }, ms1.Single(t => t.Owner == _a.Peptide).Values);

        // Ions: scan 6 has a 20 ms injection, the others 10 ms.
        var ions = XicTraces.Build(_index, _signals, new[] { _a.Peptide }, XicMode.PerPeptide, false, IntensityUnits.Ions).Single();
        Assert.Equal(new[] { 0.12, 0.12, 0.24 }, ions.Values.Select(v => Math.Round(v, 6)));
    }

    [Fact]
    public void CombinedXicsNeverCountAPeakTwice()
    {
        // AAK under P1 and its copy under P3 (a shared peptide), and CCK under P2, whose precursor window
        // (2.5 ppm away) catches the same MS1 peak as AAK's. One unrelated peak at 450 makes up the TIC.
        var p1 = new DocProtein("P1", null, null, null);
        var p2 = new DocProtein("P2", null, null, null);
        var p3 = new DocProtein("P3", null, null, null);
        // Integrated from 0 to 2.5 min; the XICs run a minute past that, so the scan at 3 min is in the
        // margin. All three share the y2 fragment at 300 too.
        var aak = Docs.Precursor(p1, "AAK", 2, 400.0, "run1.raw", 0, 2.5, ("precursor", 2, 400.0), ("y2", 1, 300.0));
        var copy = Docs.Precursor(p3, "AAK", 2, 400.0, "run1.raw", 0, 2.5, ("precursor", 2, 400.0), ("y2", 1, 300.0));
        var cck = Docs.Precursor(p2, "CCK", 2, 400.001, "run1.raw", 0, 2.5, ("precursor", 2, 400.001), ("y2", 1, 300.0));
        var source = new FakeSource()
            .Ms1(1, new[] { 400.0, 450.0 }, new[] { 100.0, 50.0 })
            .Ms2(1.5, 395, 405, new[] { 300.0, 350.0 }, new[] { 40.0, 5.0 })
            .Ms1(2, new[] { 400.0, 450.0 }, new[] { 60.0, 50.0 })
            .Ms1(3, new[] { 400.0, 450.0 }, new[] { 30.0, 50.0 });
        var observer = new DocumentSignalObserver(Docs.Model(p1, p2, p3), "run1.raw", DocumentSettings.Default);
        var index = ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        var signals = observer.Result!;
        var all = new[] { aak.Peptide, cck.Peptide, copy.Peptide };

        // Integrated only: the union of claimed peaks. Three precursors claim the 400 peak; it counts once,
        // the sum stays under the TIC (150, then 110), and the margin scan at 3 min is left out.
        var sum = Assert.Single(XicTraces.Build(index, signals, all, XicMode.Summed, true, IntensityUnits.AsRecorded, integratedOnly: true));
        Assert.Equal(new[] { 1.0, 2.0 }, sum.Times);
        Assert.Equal(new[] { 100.0, 60.0 }, sum.Values);
        Assert.Equal("Sum of 2 peptides", sum.Name);   // AAK and CCK: the copy is the same molecule
        var ms1Tic = index.Headers.Where(h => h.MsLevel == 1).ToDictionary(h => h.RetentionTime, h => h.TotalIonCurrent);
        Assert.All(sum.Times.Select((t, i) => sum.Values[i] <= ms1Tic[t]), Assert.True);

        // MS2 the same way: the shared fragment counts once (40, not 120), and only claimed peaks count.
        var ms2 = Assert.Single(XicTraces.Build(index, signals, all, XicMode.Summed, false, IntensityUnits.AsRecorded, integratedOnly: true));
        Assert.Equal(new[] { 1.5 }, ms2.Times);
        Assert.Equal(new[] { 40.0 }, ms2.Values);

        // A shared peptide is drawn once per peptide, but each protein keeps it in its own trace.
        var perPeptide = XicTraces.Build(index, signals, all, XicMode.PerPeptide, true, IntensityUnits.AsRecorded, integratedOnly: true);
        Assert.Equal(new[] { aak.Peptide, cck.Peptide }, perPeptide.Select(t => t.Owner));
        Assert.Equal(new[] { "AAK (P1)", "CCK (P2)" }, perPeptide.Select(t => t.Name));
        var perProtein = XicTraces.Build(index, signals, all, XicMode.PerProtein, true, IntensityUnits.AsRecorded, integratedOnly: true);
        Assert.Equal(new object[] { p1, p2, p3 }, perProtein.Select(t => t.Owner));
        Assert.Equal(new[] { "P1 (1 peptides)", "P2 (1 peptides)", "P3 (1 peptides)" }, perProtein.Select(t => t.Name));
        Assert.All(perProtein, t => Assert.Equal(new[] { 100.0, 60.0 }, t.Values));

        // Not limited to the peak: the margins hold no claims, so each distinct precursor is summed once.
        // The copy no longer adds, but AAK's and CCK's overlapping windows still do (100 + 100), out to the
        // margin scan at 3 min (30 + 30).
        var margins = Assert.Single(XicTraces.Build(index, signals, all, XicMode.Summed, true, IntensityUnits.AsRecorded, integratedOnly: false));
        Assert.Equal(1.0, margins.Times[0], 9);
        Assert.Equal(3.0, margins.Times[^1], 9);
        Assert.Equal(200.0, margins.Values[0], 6);
        Assert.Equal(60.0, margins.Values[^1], 6);

        // Ions: the union is converted per scan (10 ms injections: x 0.01).
        var ions = Assert.Single(XicTraces.Build(index, signals, all, XicMode.Summed, true, IntensityUnits.Ions, integratedOnly: true));
        Assert.Equal(new[] { 1.0, 0.6 }, ions.Values.Select(v => Math.Round(v, 6)));
    }

    [Fact]
    public void ACombinedXicWithoutRecordedClaimsFallsBackToSummingEachPrecursorOnce()
    {
        // XICs from before claims were recorded (or built by hand): no union is possible, so the traces are
        // summed, each distinct precursor once. XX's 3+ precursor has no XIC in this file: XX still counts.
        var protein = new DocProtein("P", null, null, null);
        var claimed = Docs.Precursor(protein, "AAK", 2, 400.0, "run1.raw", 0, 5);
        var unclaimed = Docs.Precursor(protein, "XXK", 2, 410.0, "run1.raw", 0, 5);
        Docs.Precursor(protein, "XXK", 3, 273.7, "run1.raw", 0, 5);
        var source = new FakeSource().Ms1(1, new[] { 500.0 }, new[] { 1.0 }).Ms1(2, new[] { 500.0 }, new[] { 1.0 });
        var index = ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>());
        var xics = new Dictionary<DocPrecursor, PrecursorXic>
        {
            [claimed] = new PrecursorXic(claimed, new[] { 0, 1 }, new[] { 5f, 7f }, Array.Empty<int>(), Array.Empty<float>())
            {
                Ms1Claims = new PeakClaims(new[] { PeakClaims.Key(0, 0), PeakClaims.Key(1, 0) }, new[] { 5f, 7f }),
            },
            [unclaimed] = new PrecursorXic(unclaimed, new[] { 0, 1 }, new[] { 2f, 3f }, Array.Empty<int>(), Array.Empty<float>()),
        };
        var signals = new DocumentSignals("run1.raw", new float[2], new ushort[2], xics,
            HeatmapGrid.Empty, HeatmapGrid.Empty, HeatmapGrid.Empty, HeatmapGrid.Empty, 2);

        var sum = Assert.Single(XicTraces.Build(index, signals, protein.Peptides, XicMode.Summed, true, IntensityUnits.AsRecorded, integratedOnly: true));
        Assert.Equal("Sum of 2 peptides", sum.Name);
        Assert.Equal(7.0, sum.Values[0], 6);    // 5 + 2
        Assert.Equal(10.0, sum.Values[^1], 6);  // 7 + 3
    }

    [Fact]
    public void PeptideWithTwoChargesIsSummed()
    {
        var protein = new DocProtein("P", null, null, null);
        var z2 = Docs.Precursor(protein, "AAAK", 2, 405.0, "run1.raw", 0, 3, ("y2", 1, 300.0));
        var z3 = Docs.Precursor(protein, "AAAK", 3, 270.3, "run1.raw", 0, 3, ("y2", 1, 300.0));
        Assert.Same(z2.Peptide, z3.Peptide);
        var source = new FakeSource()
            .Ms1(0, new[] { 500.0 }, new[] { 1.0 })
            .Ms2(0.5, 265, 410, new[] { 300.0 }, new[] { 4.0 })
            .Ms1(1, new[] { 500.0 }, new[] { 1.0 })
            .Ms2(1.5, 265, 410, new[] { 300.0 }, new[] { 6.0 });
        var observer = new DocumentSignalObserver(Docs.Model(protein), "run1.raw", DocumentSettings.Default);
        var index = ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        var trace = XicTraces.Build(index, observer.Result!, new[] { z2.Peptide }, XicMode.PerPeptide, false, IntensityUnits.AsRecorded).Single();
        // Both charge states extract the same peak: the peptide trace is their sum on a common grid.
        Assert.Equal(8.0, trace.Values[0], 6);
        Assert.Equal(12.0, trace.Values[^1], 6);
    }

    [Fact]
    public void MeanGridAveragesAndFillsShortGaps()
    {
        var grid = new MeanGrid(columns: 10, mzBin: 1, rtLow: 0, rtBin: 1);
        Assert.Equal(-1, grid.ColumnAt(-0.5));
        Assert.Equal(-1, grid.ColumnAt(10.5));
        Assert.True(grid.ToGrid().IsEmpty);
        grid.Add(500, 0, 2);
        grid.Add(500, 0, 4);
        grid.Add(500, 2, 6);
        grid.Add(500, 9, 1);
        for (var c = 0; c < 10; c++)
            grid.Add(600, c, 1);   // a fully sampled row: the typical spacing is one column
        grid.Add(-1, 0, 99);   // ignored
        grid.AddRange(400.5, 402.5, 0, 5);
        var g = grid.ToGrid();
        Assert.Equal(400, g.MzLow);
        Assert.Equal(3f, g.Values[g.RowAt(500.5), 0]);          // mean of 2 and 4
        Assert.Equal(3f, g.Values[g.RowAt(500.5), 1]);          // short gap filled from the left
        Assert.Equal(6f, g.Values[g.RowAt(500.5), 2]);
        Assert.True(float.IsNaN(g.Values[g.RowAt(500.5), 5]));  // long gap stays empty
        Assert.Equal(5f, g.Values[g.RowAt(402.2), 0]);
        Assert.True(float.IsNaN(g.Values[g.RowAt(450), 0]));
    }
}
