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
