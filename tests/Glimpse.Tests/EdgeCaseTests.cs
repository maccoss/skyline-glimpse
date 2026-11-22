using Glimpse.Core.Assignment;
using Glimpse.Core.Chemistry;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Heatmaps;
using Glimpse.Core.Scans;
using Xunit;

namespace Glimpse.Tests;

/// <summary>Second round of cases found by mutation testing.</summary>
public class EdgeCaseTests
{
    [Fact]
    public void HeatmapHonorsBinWidthCapsAndZeroInjectionTime()
    {
        var acc = new IntensityHeatmapAccumulator(mzBinTh: 2, maxColumns: 4, mzMax: 1000);
        acc.Begin(spectrumCount: 8, rtFirst: 0, rtLast: 4);
        acc.Observe(FakeSource.Spectrum(0, 1, 0.5, new[] { 501.0, 503.5, 1000.0 }, new[] { 4.0, 6.0, 2.0 }, 10f, double.NaN, double.NaN));
        acc.Observe(FakeSource.Spectrum(0, 1, 0.6, new[] { 501.0 }, new[] { 8.0 }, 0f, double.NaN, double.NaN));   // IT 0: no ion count
        acc.Observe(FakeSource.Spectrum(0, 1, 4.0000045, new[] { 501.0 }, new[] { 99.0 }, 10f, double.NaN, double.NaN)); // just past the last column
        acc.Observe(FakeSource.Spectrum(0, 2, 1.5, new[] { 300.0 }, new[] { 12.0 }, 10f, 996.0, 1000.0));          // window up to the cap
        acc.End();
        var ms1 = acc.Result!.Ms1;
        Assert.Equal(500, ms1.MzLow);                     // 501 / 2 -> row 250 -> m/z 500
        Assert.Equal(2, ms1.MzBin);
        Assert.Equal(6f, ms1.Values[ms1.RowAt(501), 0]);  // (4 + 8) over two scans
        Assert.Equal(3f, ms1.Values[ms1.RowAt(503.5), 0]);
        Assert.Equal(1f, ms1.Values[ms1.RowAt(1000), 0]); // m/z exactly at the cap is kept
        Assert.Equal(0.04f, ms1.Ions[ms1.RowAt(501), 0], 5);   // only the 10 ms scan counts: 4 x 0.01 over 1 scan
        Assert.True(float.IsNaN(ms1.Values[ms1.RowAt(501), 3]));
        var ms2 = acc.Result.Ms2;
        Assert.Equal(996, ms2.MzLow);
        Assert.Equal(2, ms2.Rows);                         // 996-998 and 998-1000
    }

    [Fact]
    public void HeatmapKeepsTheLowestRows()
    {
        var acc = new IntensityHeatmapAccumulator(mzBinTh: 1, maxColumns: 2);
        acc.Begin(2, 0, 1);
        acc.Observe(FakeSource.Spectrum(0, 1, 0.1, new[] { 0.5 }, new[] { 3.0 }, 10f, double.NaN, double.NaN));
        acc.Observe(FakeSource.Spectrum(0, 2, 0.2, new[] { 1.0 }, new[] { 5.0 }, 10f, 0.2, 0.8));
        acc.End();
        Assert.Equal(3f, acc.Result!.Ms1.Values[0, 0]);
        Assert.Equal(0, acc.Result.Ms2.MzLow);
        Assert.Equal(5f, acc.Result.Ms2.Values[0, 0]);

        var grid = new MeanGrid(2, 1, 0, 1);
        grid.Add(0, 0, 4);
        Assert.Equal(4f, grid.ToGrid().Values[0, 0]);
    }

    [Fact]
    public void HeatmapIonGridIsGapFilledToo()
    {
        var acc = new IntensityHeatmapAccumulator(mzBinTh: 1, maxColumns: 10);
        acc.Begin(10, 0, 10);
        for (var t = 0.5; t < 10; t += 2)   // MS1 in every other column
            acc.Observe(FakeSource.Spectrum(0, 1, t, new[] { 500.0 }, new[] { 100.0 }, 10f, double.NaN, double.NaN));
        acc.End();
        var g = acc.Result!.Ms1;
        Assert.Equal(1f, g.Ions[0, 1], 5);    // filled from column 0
        Assert.Equal(100f, g.Values[0, 1]);
    }

    [Fact]
    public void ObserverWithDegenerateRangeAndExactMs1MarginEdge()
    {
        var protein = new DocProtein("P", null, null, null);
        var p = Docs.Precursor(protein, "AK", 2, 500.0, "run1.raw", 0.0, 1.0, ("precursor", 2, 500.0));
        // All scans at the same time: no RT range at all.
        var flat = new FakeSource().Ms1(0.5, new[] { 500.0 }, new[] { 3.0 }).Ms1(0.5, new[] { 500.0 }, new[] { 3.0 });
        var observer = new DocumentSignalObserver(Docs.Model(protein), "run1.raw", DocumentSettings.Default, 0.5, 1, 10);
        ScanIndexBuilder.Build(flat, false, new IScanObserver[] { observer });
        Assert.False(observer.Result!.Ms1Fraction.IsEmpty);
        Assert.Equal(new[] { 3f, 3f }, observer.Result.AssignedIntensity);

        // MS1 scans at exactly end + margin are still in the XIC; just past it are not.
        var edge = new FakeSource()
            .Ms1(1.5, new[] { 500.0 }, new[] { 2.0 })
            .Ms1(1.5000001, new[] { 500.0 }, new[] { 2.0 });
        var o2 = new DocumentSignalObserver(Docs.Model(protein), "run1.raw", DocumentSettings.Default, 0.5, 1, 10);
        ScanIndexBuilder.Build(edge, false, new IScanObserver[] { o2 });
        Assert.Equal(new[] { 0 }, o2.Result!.Xics[p].Ms1Scans);
    }

    [Fact]
    public void ZeroSignalBinsHaveZeroAssignedFraction()
    {
        var protein = new DocProtein("P", null, null, null);
        Docs.Precursor(protein, "AK", 2, 500.0, "run1.raw", 0.0, 1.0, ("precursor", 2, 500.0));
        var source = new FakeSource().Ms1(0.5, new[] { 500.0, 600.0 }, new[] { 3.0, 0.0 });
        var observer = new DocumentSignalObserver(Docs.Model(protein), "run1.raw", DocumentSettings.Default, 0.5, 1, 4);
        ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        var g = observer.Result!.Ms1Fraction;
        Assert.Equal(1f, g.Values[g.RowAt(500), 0]);
        Assert.Equal(0f, g.Values[g.RowAt(600), 0]);
        Assert.True(float.IsNaN(g.Values[g.RowAt(550), 0]) || g.Values[g.RowAt(550), 0] == 0f);
    }

    [Fact]
    public void TypicalGapIsTheMedianSpacingAndFillingStopsAtTheLimit()
    {
        float N = float.NaN;
        var values = new float[,]
        {
            { 1, N, 2, N, 3, N, N, N, N, 4 },   // gaps 2, 2, 5
            { 5, N, 6, N, 7, N, 8, N, N, N },   // gaps 2, 2, 2
            { N, N, N, N, N, N, N, N, N, N },
        };
        Assert.Equal(2, GridGaps.TypicalGap(values));
        Assert.Equal(1, GridGaps.TypicalGap(new float[,] { { N, N }, { N, 1 } }));   // no gaps at all
        Assert.Equal(3, GridGaps.TypicalGap(new float[,] { { 1, N, N, 2, N, N, 3 } }));

        var fill = (float[,])values.Clone();
        GridGaps.FillShortGaps(fill, 4);
        Assert.Equal(1f, fill[0, 1]);                       // a gap of 2 is filled
        Assert.True(float.IsNaN(fill[0, 5]));               // a gap of 5 is not
        Assert.True(float.IsNaN(fill[1, 7]));               // trailing cells are not
        Assert.True(float.IsNaN(fill[2, 0]));
        var exact = new float[,] { { 1, N, N, N, 2 } };
        GridGaps.FillShortGaps(exact, 4);
        Assert.Equal(1f, exact[0, 3]);                      // a gap equal to the limit is filled
        var untouched = new float[,] { { 1, N, 2 } };
        GridGaps.FillShortGaps(untouched, 1);
        Assert.True(float.IsNaN(untouched[0, 1]));          // a limit of 1 fills nothing
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BuilderFindsRequiredColumnsInAnyPosition(int rotate)
    {
        var names = new[] { "ProteinName", "PeptideSequence", "PrecursorCharge", "PrecursorMz", "ProductMz" };
        var values = new[] { "P1", "AAK", "2", "200.1", "147.1" };
        var order = Enumerable.Range(0, 5).Select(i => (i + rotate) % 5).ToArray();
        var csv = string.Join(",", order.Select(i => names[i])) + "\n" + string.Join(",", order.Select(i => values[i])) + "\n";
        var model = DocumentModelBuilder.Build(null, CsvTable.Read(new StringReader(csv)), null);
        var precursor = Assert.Single(model.Precursors);
        Assert.Equal(("P1", "AAK", 2, 200.1, 147.1), (precursor.Peptide.Protein.Name, precursor.Peptide.Sequence, precursor.Charge, precursor.Mz, precursor.Transitions[0].ProductMz));

        var peaks = new[] { "PrecursorLocator", "FileName", "BestRetentionTime" };
        var peakValues = new[] { "L1", "a.raw", "5" };
        var p = Enumerable.Range(0, 3).Select(i => (i + rotate) % 3).ToArray();
        var transitions = CsvTable.Read(new StringReader("ProteinName,PeptideSequence,PrecursorCharge,PrecursorMz,ProductMz,PrecursorLocator\nP,AK,2,200,147,L1\n"));
        var withPeaks = DocumentModelBuilder.Build(null, transitions,
            CsvTable.Read(new StringReader(string.Join(",", p.Select(i => peaks[i])) + "\n" + string.Join(",", p.Select(i => peakValues[i])) + "\n")));
        Assert.Equal(5.0, withPeaks.Precursors[0].Peaks["a.raw"].Apex);
    }

    [Fact]
    public void BuilderSeparatesPeptidesWithTheSameSequenceByLocator()
    {
        var csv = "ProteinName,ProteinLocator,PeptideSequence,PeptideLocator,PrecursorCharge,PrecursorMz,ProductMz\n"
            + "P1,G1,AAK,M1,2,200.1,147.1\nP1,G1,AAK,M2,2,200.1,147.1\n";
        var model = DocumentModelBuilder.Build(null, CsvTable.Read(new StringReader(csv)), null);
        Assert.Single(model.Proteins);
        Assert.Equal(new[] { "M1", "M2" }, model.Peptides.Select(p => p.Locator));
    }

    [Fact]
    public void BuilderAcceptsShortAliases()
    {
        var csv = "Protein,Peptide Sequence,Precursor Charge,Precursor Mz,Product Mz,Fragment Ion,Product Charge,Precursor Locator\nP1,AAK,2,200.1,147.1,y1,1,L1\n";
        var peaks = "Precursor Locator,File Name,Replicate,Best Retention Time\nL1,a.raw,rep1,5\n";
        var model = DocumentModelBuilder.Build(null, CsvTable.Read(new StringReader(csv)), CsvTable.Read(new StringReader(peaks)));
        Assert.Equal("P1", model.Proteins[0].Name);
        Assert.Equal(("y1", 1), (model.Precursors[0].Transitions[0].FragmentIon, model.Precursors[0].Transitions[0].ProductCharge));
        Assert.Equal("rep1", model.ResultFiles[0].ReplicateName);
    }

    [Fact]
    public void CsvDoubledQuotesNulAndExactLength()
    {
        var t = CsvTable.Read(new StringReader("\"a\"\"\",b\nx\0y,z\n"));
        Assert.Equal(new[] { "a\"", "b" }, t.Header);
        Assert.Equal("x\0y", t.Rows[0][0]);
        Assert.Equal("", CsvTable.Get(t.Rows[0], 2));        // exactly one past the end
    }

    [Fact]
    public void SmallParsingEdges()
    {
        Assert.Equal((IonType.Other, 0, ""), Fragments.ParseSkylineFragment("", 1));
        var noUnit = DocumentSettings.Parse("<s><transition_libraries ion_match_tolerance=\"0.3\" /></s>");
        Assert.Equal(MzTolerance.Th(0.3), noUnit.IonMatchTolerance);
    }
}
