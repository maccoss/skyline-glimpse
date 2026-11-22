using Glimpse.Core.Assignment;
using Glimpse.Core.Chemistry;
using Glimpse.Core.Chromatograms;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Scans;
using Xunit;

namespace Glimpse.Tests;

/// <summary>
/// Edges and orderings the other tests did not pin down - each case here exists because mutation
/// testing showed a change to the code that no test noticed.
/// </summary>
public class BoundaryTests
{
    private static readonly DocumentSettings HalfTh = DocumentSettings.Default with
    {
        PrecursorTolerance = MzTolerance.Th(0.5),
        ProductTolerance = MzTolerance.Th(0.5),
    };

    private static DocumentSignals Run(DocumentModel model, FakeSource source, double margin = 0.5, double mzBin = 1, DocumentSettings? settings = null)
    {
        var observer = new DocumentSignalObserver(model, "run1.raw", settings ?? HalfTh, margin, mzBin, maxColumns: 20);
        ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        return observer.Result!;
    }

    [Fact]
    public void Ms1SweepHandlesStaggeredPeaks()
    {
        var protein = new DocProtein("P", null, null, null);
        // Added late-first so document order differs from elution order.
        var late = Docs.Precursor(protein, "LATEK", 2, 600, "run1.raw", 5.0, 6.0, ("precursor", 2, 600));
        var early = Docs.Precursor(protein, "EARLYK", 2, 500, "run1.raw", 0.0, 0.5, ("precursor", 2, 500));
        var source = new FakeSource()
            .Ms1(0.2, new[] { 500.0, 600.0 }, new[] { 1.0, 2.0 })
            .Ms1(5.5, new[] { 500.0, 600.0 }, new[] { 3.0, 4.0 });
        var s = Run(Docs.Model(protein), source);
        Assert.Equal(new[] { 0 }, s.Xics[early].Ms1Scans);
        Assert.Equal(new[] { 1 }, s.Xics[late].Ms1Scans);
        Assert.Equal(1f, s.AssignedIntensity[0]);
        Assert.Equal(4f, s.AssignedIntensity[1]);
    }

    [Fact]
    public void PeakAndWindowEdgesAreInclusiveIsolationIsHalfOpen()
    {
        var protein = new DocProtein("P", null, null, null);
        var r = Docs.Precursor(protein, "RRK", 2, 400.0, "run1.raw", 1.0, 2.0, ("y2", 1, 300.0));
        var atUpper = Docs.Precursor(protein, "UUK", 2, 410.0, "run1.raw", 1.0, 2.0, ("y2", 1, 320.0));
        var source = new FakeSource()
            .Ms2(0.5, 400, 410, new[] { 300.5 }, new[] { 1.0 })     // start - margin: XIC only
            .Ms2(0.4, 400, 410, new[] { 300.5 }, new[] { 1.0 })     // before it: nothing
            .Ms2(2.0, 400, 410, new[] { 300.5, 320.0 }, new[] { 5.0, 9.0 }) // end of peak: claimed; 300.5 is the window edge
            .Ms2(2.5, 400, 410, new[] { 300.5 }, new[] { 2.0 })     // end + margin: XIC only
            .Ms2(2.6, 400, 410, new[] { 300.5 }, new[] { 2.0 });    // past it: nothing
        var s = Run(Docs.Model(protein), source);
        Assert.Equal(new[] { 0, 2, 3 }, s.Xics[r].Ms2Scans);
        Assert.Equal(new[] { 1f, 5f, 2f }, s.Xics[r].Ms2Values);
        Assert.Equal(5f, s.AssignedIntensity[2]);
        Assert.Equal(0f, s.AssignedIntensity[3]);
        // m/z 410.0 is outside [400, 410): that precursor is never a candidate in this window.
        Assert.Empty(s.Xics[atUpper].Ms2Scans);
        Assert.Equal(1, s.PeptideCount[2]);
    }

    [Fact]
    public void Ms2OnlyRunsLeaveTheMs1GridsEmptyAndEmptyScansHaveZeroFraction()
    {
        var protein = new DocProtein("P", null, null, null);
        var noFragments = Docs.Precursor(protein, "NOFK", 2, 405.0, "run1.raw", 0, 5);   // MS1 transitions only
        var p = Docs.Precursor(protein, "PPK", 2, 406.0, "run1.raw", 0, 5, ("y2", 1, 300.0));
        var source = new FakeSource()
            .Ms2(1, 400, 410, new[] { 300.0 }, new[] { 4.0 })
            .Ms2(2, 400, 410, Array.Empty<double>(), Array.Empty<double>());
        var s = Run(Docs.Model(protein), source);
        Assert.True(s.Ms1Count.IsEmpty);
        Assert.True(s.Ms1Fraction.IsEmpty);
        Assert.Empty(s.Xics[noFragments].Ms2Scans);           // nothing to extract: no points, not zeros
        Assert.Equal(new[] { 4f, 0f }, s.Xics[p].Ms2Values);
        var f = s.Ms2Fraction;
        Assert.Equal(1f, f.Values[f.RowAt(405), f.ColumnAt(1)]);
        Assert.Equal(0f, f.Values[f.RowAt(405), f.ColumnAt(2)]);   // no signal: 0, not NaN
    }

    [Fact]
    public void Ms1FractionRowsHonorTheBinWidth()
    {
        var protein = new DocProtein("P", null, null, null);
        Docs.Precursor(protein, "AK", 2, 405.0, "run1.raw", 0, 5, ("precursor", 2, 405.0));
        var source = new FakeSource()
            .Ms1(1, new[] { 405.0, 409.0 }, new[] { 3.0, 1.0 })
            .Ms1(2, new[] { 405.0, 409.0 }, new[] { 3.0, 1.0 });
        var s = Run(Docs.Model(protein), source, mzBin: 2);
        var g = s.Ms1Fraction;
        Assert.Equal(2, g.MzBin);
        Assert.Equal(1f, g.Values[g.RowAt(405.5), g.ColumnAt(1)]);
        Assert.Equal(0f, g.Values[g.RowAt(409.5), g.ColumnAt(1)]);
        var c = s.Ms1Count;
        Assert.Equal(1f, c.Values[c.RowAt(405), c.ColumnAt(1)]);
    }

    [Fact]
    public void XicGridStepFollowsTheMs1Cadence()
    {
        ScanIndex Index(params double[] ms1Times)
        {
            var source = new FakeSource();
            foreach (var t in ms1Times)
                source.Ms1(t, new[] { 1.0 }, new[] { 1.0 });
            return ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>());
        }
        Assert.Equal(0.25, XicTraces.GridStep(Index(0, 0.5, 1.0)), 9);         // three MS1 scans: half the spacing
        Assert.Equal(0.25, XicTraces.GridStep(Index(0, 0.5, 1.0, 1.5, 9)), 9); // the median, not the mean
        Assert.Equal(1.0 / 4000, XicTraces.GridStep(Index(0, 1)), 12);          // too few MS1 scans: 4000 steps over the run
        Assert.Equal(0.01, XicTraces.GridStep(Index(3)), 12);                    // no range at all
        Assert.Equal(1.0 / 4000, XicTraces.GridStep(Index(0, 0, 0, 1)), 12);    // zero spacing falls through
    }

    [Fact]
    public void XicPointsMergeSimultaneousScansAndSkipUnusableOnes()
    {
        var protein = new DocProtein("P", null, null, null);
        var p = Docs.Precursor(protein, "AK", 2, 405.0, "run1.raw", 0, 5, ("y2", 1, 300.0));
        var empty = Docs.Precursor(protein, "EK", 2, 405.0, "run1.raw", 0, 5, ("y2", 1, 300.0));
        // Overlapping windows: two scans at the same time both contain the precursor.
        var source = new FakeSource()
            .Ms2(1.0, 400, 410, new[] { 300.0 }, new[] { 2.0 })
            .Ms2(1.0, 404, 414, new[] { 300.0 }, new[] { 3.0 })
            .Ms2(2.0, 400, 410, new[] { 300.0 }, new[] { 4.0 }, injectionMs: float.NaN)
            .Ms2(double.NaN, 400, 410, new[] { 300.0 }, new[] { 50.0 });
        var index = ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>());
        var xics = new Dictionary<DocPrecursor, PrecursorXic>
        {
            [p] = new PrecursorXic(p, Array.Empty<int>(), Array.Empty<float>(), new[] { 0, 1, 2, 3 }, new[] { 2f, 3f, 4f, 50f }),
            [empty] = new PrecursorXic(empty, Array.Empty<int>(), Array.Empty<float>(), Array.Empty<int>(), Array.Empty<float>()),
        };
        var signals = new DocumentSignals("run1.raw", new float[4], new ushort[4], xics,
            HeatmapGridEmpty(), HeatmapGridEmpty(), HeatmapGridEmpty(), HeatmapGridEmpty(), 2);

        var traces = XicTraces.Build(index, signals, new[] { p.Peptide, empty.Peptide }, XicMode.PerPeptide, false, IntensityUnits.AsRecorded);
        var t = Assert.Single(traces);                         // the peptide with no points is left out
        Assert.Equal(new[] { 1.0, 2.0 }, t.Times);             // the NaN-time scan is dropped
        Assert.Equal(new[] { 5.0, 4.0 }, t.Values);            // simultaneous scans summed
        var ions = Assert.Single(XicTraces.Build(index, signals, new[] { p.Peptide }, XicMode.PerPeptide, false, IntensityUnits.Ions));
        Assert.Equal(new[] { 1.0 }, ions.Times);               // no injection time: no ion count
        Assert.Empty(XicTraces.Build(index, signals, new[] { empty.Peptide }, XicMode.Summed, false, IntensityUnits.AsRecorded));
    }

    [Fact]
    public void XicsCanBeLimitedToTheIntegratedPeak()
    {
        var protein = new DocProtein("P", null, null, null);
        // Integrated from 2 to 3 min in run1.raw; extracted (peak plus margin) from 1 to 4 min.
        var p = Docs.Precursor(protein, "AK", 2, 405.0, "run1.raw", 2.0, 3.0, ("y2", 1, 300.0));
        // Integrated only in another file: nothing to limit it to here, so its whole trace stays.
        var other = Docs.Precursor(protein, "EK", 2, 405.0, "run2.raw", 2.0, 3.0, ("y2", 1, 300.0));
        var source = new FakeSource();
        foreach (var rt in new[] { 1.0, 2.0, 2.5, 3.0, 4.0 })
            source.Ms2(rt, 400, 410, new[] { 300.0 }, new[] { rt * 10 });
        var index = ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>());
        var scans = new[] { 0, 1, 2, 3, 4 };
        var values = new[] { 10f, 20f, 25f, 30f, 40f };
        var xics = new Dictionary<DocPrecursor, PrecursorXic>
        {
            [p] = new PrecursorXic(p, Array.Empty<int>(), Array.Empty<float>(), scans, values),
            [other] = new PrecursorXic(other, Array.Empty<int>(), Array.Empty<float>(), scans, values),
        };
        var signals = new DocumentSignals("run1.raw", new float[5], new ushort[5], xics,
            HeatmapGridEmpty(), HeatmapGridEmpty(), HeatmapGridEmpty(), HeatmapGridEmpty(), 2);

        var whole = XicTraces.Build(index, signals, new[] { p.Peptide }, XicMode.PerPeptide, false, IntensityUnits.AsRecorded).Single();
        Assert.Equal(new[] { 1.0, 2.0, 2.5, 3.0, 4.0 }, whole.Times);

        var peak = XicTraces.Build(index, signals, new[] { p.Peptide }, XicMode.PerPeptide, false, IntensityUnits.AsRecorded, integratedOnly: true).Single();
        Assert.Equal(new[] { 2.0, 2.5, 3.0 }, peak.Times);       // boundaries inclusive
        Assert.Equal(new[] { 20.0, 25.0, 30.0 }, peak.Values);

        var unbounded = XicTraces.Build(index, signals, new[] { other.Peptide }, XicMode.PerPeptide, false, IntensityUnits.AsRecorded, integratedOnly: true).Single();
        Assert.Equal(new[] { 1.0, 2.0, 2.5, 3.0, 4.0 }, unbounded.Times);
    }

    private static Core.Heatmaps.HeatmapGrid HeatmapGridEmpty() => Core.Heatmaps.HeatmapGrid.Empty;

    [Fact]
    public void SumOnGridPlacesSinglePointsAndSpansTheLongestTrace()
    {
        var a = (new[] { 0.0, 1.0 }, new[] { 1.0, 1.0 });
        var b = (new[] { 0.5, 4.0 }, new[] { 2.0, 2.0 });
        var single = (new[] { 2.0 }, new[] { 7.0 });
        var (t, y) = XicTraces.SumOnGrid(new[] { a, b, single, (Array.Empty<double>(), Array.Empty<double>()) }, 0.5);
        Assert.Equal(0.0, t[0]);
        Assert.Equal(4.0, t[^1]);                              // the longest trace sets the end
        Assert.Equal(9, t.Length);
        Assert.Equal(1.0, y[0], 9);                            // only a
        Assert.Equal(3.0, y[1], 9);                            // a + b
        Assert.Equal(2.0 + 7.0, y[4], 9);                      // b + the single point at t = 2
        Assert.Equal(2.0, y[^1], 9);
        var (et, ey) = XicTraces.SumOnGrid(Array.Empty<(double[], double[])>(), 1);
        Assert.Empty(et);
        Assert.Empty(ey);
    }

    [Theory]
    [InlineData("ProteinName")]
    [InlineData("PeptideSequence")]
    [InlineData("PrecursorCharge")]
    [InlineData("PrecursorMz")]
    [InlineData("ProductMz")]
    public void BuilderRejectsAReportMissingARequiredColumn(string missing)
    {
        var columns = new[] { "ProteinName", "PeptideSequence", "PrecursorCharge", "PrecursorMz", "ProductMz" }.Where(c => c != missing);
        var csv = string.Join(",", columns) + "\n" + string.Join(",", columns.Select(_ => "1"));
        Assert.Throws<InvalidDataException>(() => DocumentModelBuilder.Build(null, CsvTable.Read(new StringReader(csv)), null));
    }

    [Fact]
    public void BuilderKeysWithAndWithoutLocators()
    {
        var csv =
            "Protein Name,Protein Accession,Protein Gene,Protein Locator,Peptide Sequence,Peptide Modified Sequence Monoisotopic Masses,Peptide Locator,"
            + "Modified Sequence Monoisotopic Masses,Precursor Charge,Precursor Mz,Isotope Label Type,Precursor Locator,Fragment Ion,Product Charge,Product Mz\n"
            // Same protein name, different locators: two proteins.
            + "P1,A1,G1,MoleculeGroup:/P1#1,AAK,AAK,Molecule:/P1#1/AAK,AAK,2,200.1,light,Precursor:/P1#1/AAK/2,y1,1,147.1\n"
            + "P1,A1,G1,MoleculeGroup:/P1#2,AAK,AAK,Molecule:/P1#2/AAK,AAK,2,200.1,light,Precursor:/P1#2/AAK/2,y1,1,147.1\n"
            // A heavy precursor: its own modified sequence and label.
            + "P1,A1,G1,MoleculeGroup:/P1#2,AAK,AAK,Molecule:/P1#2/AAK,AAK[+8.014199],2,204.1,heavy,Precursor:/P1#2/AAK/2/heavy,y1,1,155.1\n";
        var model = DocumentModelBuilder.Build(null, CsvTable.Read(new StringReader(csv)), null);
        Assert.Equal(2, model.Proteins.Count);
        Assert.Equal("A1", model.Proteins[0].Accession);
        Assert.Equal("G1", model.Proteins[0].Gene);
        var heavy = model.Proteins[1].Peptides.Single().Precursors.Single(p => p.LabelType == "heavy");
        Assert.Equal("AAK[+8.014199]", heavy.ModifiedSequence);
        Assert.Equal("AAK[+8.014199]++ (heavy)", heavy.ToString());

        // Without locators: proteins by name, peptides by protein and sequence.
        var plain = "ProteinName,PeptideSequence,PrecursorCharge,PrecursorMz,ProductMz\nP1,AAK,2,200.1,147.1\nP2,AAK,2,200.1,147.1\nP2,AAK,2,200.1,218.1\n";
        var m2 = DocumentModelBuilder.Build(null, CsvTable.Read(new StringReader(plain)), null);
        Assert.Equal(2, m2.Proteins.Count);
        Assert.Equal(2, m2.Peptides.Count);
        Assert.Equal(2, m2.Proteins[1].Peptides.Single().Precursors.Single().Transitions.Count);
    }

    [Fact]
    public void PeakRowsNeedLocatorAndFileAndKeepPartialPeaks()
    {
        var transitions = CsvTable.Read(new StringReader(
            "ProteinName,PeptideSequence,PrecursorCharge,PrecursorMz,ProductMz,PrecursorLocator\nP,AK,2,200,147,L1\n"));
        DocumentModel With(string peaks) => DocumentModelBuilder.Build(null, transitions, CsvTable.Read(new StringReader(peaks)));

        Assert.Empty(With("FileName,BestRetentionTime\na.raw,5\n").ResultFiles);           // no locator column
        Assert.Empty(With("PrecursorLocator,BestRetentionTime\nL1,5\n").ResultFiles);       // no file column
        var displayNames = With("Precursor Locator,File Name,File Path,Replicate Name,Best Retention Time,Min Start Time,Max End Time,Detection Q Value,Library Dot Product,Isotope Dot Product\n"
            + "L1,a.raw,C:\\a.raw,rep,5,4,6,0.01,0.9,0.8\n");
        var peak = displayNames.Precursors[0].Peaks["a.raw"];
        Assert.Equal((5.0, 4.0, 6.0, 0.01, 0.9, 0.8), (peak.Apex, peak.Start, peak.End, peak.DetectionQValue!.Value, peak.LibraryDotProduct!.Value, peak.IsotopeDotProduct!.Value));
        Assert.Equal(("a.raw", "C:\\a.raw", "rep"), (displayNames.ResultFiles[0].FileName, displayNames.ResultFiles[0].FilePath, displayNames.ResultFiles[0].ReplicateName));

        var partial = With("PrecursorLocator,FileName,BestRetentionTime,MinStartTime,MaxEndTime\nL1,apex.raw,5,,\nL1,bounds.raw,,4,6\nL1,none.raw,,,\n");
        Assert.Equal(new[] { "apex.raw", "bounds.raw" }, partial.Precursors[0].Peaks.Keys.OrderBy(k => k));
        Assert.Equal(3, partial.ResultFiles.Count);
    }

    [Fact]
    public void LocatorsMatchWholePathSegments()
    {
        var protein = new DocProtein("P1", null, null, "MoleculeGroup:/P1");
        var aak = new DocPeptide(protein, "AAK", "AAK", "Molecule:/P1/AAK");
        var aakr = new DocPeptide(protein, "AAKR", "AAKR", "Molecule:/P1/AAKR");
        var noLocator = new DocPeptide(protein, "XK", "XK", null);
        protein.Peptides.AddRange(new[] { aak, aakr, noLocator });
        var model = new DocumentModel(null, new[] { protein }, Array.Empty<ResultFileInfo>());
        Assert.Equal(new[] { aak }, model.PeptidesForLocator("Molecule:/P1/AAK"));
        Assert.Equal(new[] { aakr }, model.PeptidesForLocator("Transition:/P1/AAKR/2/y4"));
        Assert.Equal(new[] { aak }, model.PeptidesForLocator("Molecule:/P1/AAK?ResultFile=a.raw"));
        Assert.Equal(new[] { aak, aakr }, model.PeptidesForLocator("MoleculeGroup:/P1"));
        Assert.Empty(model.PeptidesForLocator(null!));
        Assert.Equal(new[] { aak }, model.PeptidesForLocator("/P1/AAK"));   // no type prefix
    }

    [Fact]
    public void PeakBoundsEdges()
    {
        Assert.True(new PeakBounds(5, 5, 5, null, null, null).HasBoundaries);            // a zero-width peak is a peak
        Assert.False(new PeakBounds(5, double.NaN, 6, null, null, null).HasBoundaries);
        Assert.False(new PeakBounds(5, 4, double.NaN, null, null, null).HasBoundaries);
        var b = new PeakBounds(5, 4, 6, null, null, null);
        Assert.True(b.Contains(3.5, 0.5));
        Assert.False(b.Contains(3.4, 0.5));
        Assert.True(b.Contains(4));
        Assert.False(new PeakBounds(5, double.NaN, double.NaN, null, null, null).Contains(5, 100));
    }

    [Fact]
    public void ScanIndexEdges()
    {
        var empty = new ScanIndex("x", new RunInfo(), Array.Empty<ScanHeader>());
        Assert.True(double.IsNaN(empty.RtFirst));
        Assert.False(empty.HasInjectionTimes);
        Assert.Empty(empty.IndicesOfLevel(0));
        Assert.Equal(-1, empty.NearestScan(1, ScanFilter.All));

        var source = new FakeSource()
            .Ms1(double.NaN, new[] { 1.0 }, new[] { 1.0 }, float.NaN)
            .Ms1(1, new[] { 1.0 }, new[] { 1.0 }, float.NaN)
            .Ms1(2, new[] { 1.0 }, new[] { 1.0 }, float.NaN);
        var withoutWindow = FakeSource.Spectrum(3, 2, 3, new[] { 1.0 }, new[] { 1.0 }, float.NaN, double.NaN, double.NaN);
        var headers = ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>()).Headers.Append(withoutWindow.Header).ToArray();
        var index = new ScanIndex("x", new RunInfo(), headers);
        Assert.Equal(1, index.RtFirst);
        Assert.Equal(3, index.RtLast);
        Assert.False(index.HasInjectionTimes);
        Assert.Empty(index.IsolationWindows);                  // an MS2 scan without a window adds none
        Assert.Equal(new[] { 0, 1, 2 }, index.IndicesOfLevel(1));
        // Ties go to the later scan; otherwise the nearer one on either side.
        Assert.Equal(2, index.NearestScan(1.5, ScanFilter.Ms1));
        Assert.Equal(1, index.NearestScan(1.4, ScanFilter.Ms1));
        Assert.Equal(2, index.NearestScan(1.6, ScanFilter.Ms1));
        Assert.False(ScanFilter.Ms2Window(1, 2).Accepts(withoutWindow.Header));
    }

    [Fact]
    public void BuilderPassesTheRunRangeAndProgress()
    {
        var recorder = new Recorder();
        ScanIndexBuilder.Build(new FakeSource(), false, new IScanObserver[] { recorder });
        Assert.Equal((0, double.NaN, double.NaN), recorder.Begun);
        Assert.True(recorder.Ended);

        var source = new FakeSource();
        for (var i = 0; i < 200; i++)
            source.Ms1(1 + i * 0.01, new[] { 1.0 }, new[] { 1.0 });
        var reports = new List<double>();
        ScanIndexBuilder.Build(source, false, new IScanObserver[] { recorder }, new ListProgress(reports));
        Assert.Equal((200, 1.0, 2.99), (recorder.Begun.Count, recorder.Begun.First, Math.Round(recorder.Begun.Last, 6)));
        Assert.Equal(0.0, reports[0]);
        Assert.Equal(0.5, reports[50], 9);                    // every 1% of 200 spectra = every 2
        Assert.Equal(200, recorder.Observed);
    }

    private sealed class Recorder : IScanObserver
    {
        public (int Count, double First, double Last) Begun { get; private set; }

        public bool Ended { get; private set; }

        public int Observed { get; private set; }

        public void Begin(int spectrumCount, double rtFirst, double rtLast)
        {
            Begun = (spectrumCount, rtFirst, rtLast);
            Observed = 0;
        }

        public void Observe(SpectrumData spectrum) => Observed++;

        public void End() => Ended = true;
    }

    private sealed class ListProgress : IProgress<double>
    {
        private readonly List<double> _list;

        public ListProgress(List<double> list) => _list = list;

        public void Report(double value) => _list.Add(value);
    }

    [Fact]
    public void ResidueMassesAreTheMonoisotopicValues()
    {
        var expected = new Dictionary<char, double>
        {
            ['G'] = 57.02146, ['A'] = 71.03711, ['S'] = 87.03203, ['P'] = 97.05276, ['V'] = 99.06841,
            ['T'] = 101.04768, ['C'] = 103.00919, ['L'] = 113.08406, ['I'] = 113.08406, ['N'] = 114.04293,
            ['D'] = 115.02694, ['Q'] = 128.05858, ['K'] = 128.09496, ['E'] = 129.04259, ['M'] = 131.04049,
            ['H'] = 137.05891, ['F'] = 147.06841, ['R'] = 156.10111, ['Y'] = 163.06333, ['W'] = 186.07931,
            ['U'] = 150.95364, ['O'] = 237.14773,
        };
        foreach (var (aa, mass) in expected)
            Assert.Equal(mass, Mass.Residue(aa), 4);
        foreach (var aa in "BJXZ")
            Assert.True(double.IsNaN(Mass.Residue(aa)), aa.ToString());
    }

    [Fact]
    public void FragmentEdges()
    {
        Assert.True(Fragments.IsNTerminal(IonType.A));
        Assert.True(Fragments.IsNTerminal(IonType.B));
        Assert.False(Fragments.IsNTerminal(IonType.Y));
        var ak = ModifiedSequence.TryParse("AK")!;
        var ions = Fragments.Enumerate(ak, new[] { IonType.B, IonType.Y }, new[] { 1 }, 1);
        Assert.Equal(new[] { "b1", "y1" }, ions.Select(i => i.Label).OrderBy(l => l));
        var peptide = ModifiedSequence.TryParse("PEPTIDE")!;
        Assert.Equal(6, Fragments.Enumerate(peptide, new[] { IonType.Y }, new[] { 1 }, 2).Max(i => i.Ordinal));
        // No precursor charge known: no cap.
        Assert.Contains(Fragments.Enumerate(peptide, new[] { IonType.Y }, new[] { 3 }, 0), i => i.Charge == 3);
        Assert.Empty(Fragments.Enumerate(peptide, new[] { IonType.Y }, new[] { 0 }, 2));
        Assert.Empty(Fragments.Enumerate(peptide, new[] { IonType.Precursor }, new[] { 1 }, 2));
        Assert.Equal(IonType.Other, Fragments.ParseSkylineFragment("y", 1).Type);
        Assert.Equal("M", Fragments.ParseSkylineFragment("precursor", 2).Label);
    }

    [Fact]
    public void SpectrumLookupsAreInclusiveAndPreferTheFirstOfEqualPeaks()
    {
        var s = FakeSource.Spectrum(0, 2, 1, new[] { 100.0, 100.0, 101.0, 102.0 }, new[] { 5.0, 5.0, 9.0, 1.0 }, 1, 0, 1);
        Assert.Equal(0, s.MostIntenseIn(99, 100));              // equal intensities: the first
        Assert.Equal(2, s.MostIntenseIn(100, 101));             // the high edge is inclusive
        Assert.Equal(19.0, s.SumIn(100, 101), 9);
        Assert.Equal(20.0, s.SumIn(99, 102), 9);
        Assert.Equal(0, SpectrumData.LowerBound(s.Mz, 100));    // the first of equal values
        Assert.Equal(4, SpectrumData.LowerBound(s.Mz, 103));
    }

    [Fact]
    public void SettingsParseListsAndLibraryAttributes()
    {
        Assert.Equal(new[] { "y", "b" }, DocumentSettings.Default.FragmentTypes);
        var s = DocumentSettings.Parse("<s><transition_filter fragment_types=\" y , b ,p\" product_charges=\" 1, 2 ,0,3\" />"
            + "<peptide_libraries><bibliospec_lite_library file_path=\"C:\\libs\\a.blib\" />"
            + "<bibliospec_lite_library file_name_hint=\"b.blib\" file_path=\"C:\\x\\ignored.blib\" /><other /></peptide_libraries></s>");
        Assert.Equal(new[] { "y", "b", "p" }, s.FragmentTypes);
        Assert.Equal(new[] { 1, 2, 3 }, s.ProductCharges);    // 0 dropped, spaces trimmed
        Assert.Equal(new[] { "C:\\libs\\a.blib", "b.blib" }, s.LibraryFiles);
    }

    [Fact]
    public void ToleranceAndHeaderEdges()
    {
        Assert.False(new MzTolerance("centroided", 0).IsUsable);
        Assert.False(new MzTolerance("ft_icr", 1, 0).IsUsable);
        Assert.Null(MzTolerance.Parse("0 ppm"));
        Assert.Null(MzTolerance.Parse("5 m"));
        Assert.Equal("7 (weird)", new MzTolerance("weird", 7).Describe());

        var h = FakeSource.Spectrum(0, 2, 1, Array.Empty<double>(), Array.Empty<double>(), 0f, 400, double.NaN).Header;
        Assert.False(h.HasIsolationWindow);                   // both edges are needed
        Assert.False(h.HasInjectionTime);                     // zero is not an injection time
        Assert.False(h.IsolationContains(450));
    }

    [Fact]
    public void CsvFileWithByteOrderMarkAndTrailingEmptyField()
    {
        var path = Path.Combine(Path.GetTempPath(), $"glimpse-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "h1,h2\n1,", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var t = CsvTable.Read(path);
            Assert.Equal("h1", t.Header[0]);                  // no BOM character on the first header
            Assert.Equal(new[] { "1", "" }, t.Rows.Single());
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Equal(new[] { "abc" }, CsvTable.Read(new StringReader("abc")).Header);
        Assert.Equal("x\"y", CsvTable.Read(new StringReader("\"x\"\"y\"")).Header[0]);
    }

    [Fact]
    public void TraceMaxIgnoresNonFiniteValues()
    {
        Assert.Equal(0, new Trace("t", Array.Empty<double>(), Array.Empty<double>(), Array.Empty<int>()).MaxValue);
        Assert.Equal(3, new Trace("t", new[] { 1.0, 2, 3 }, new[] { 3.0, double.NaN, 1 }, new[] { 0, 1, 2 }).MaxValue);
        Assert.Equal(0, new Trace("t", new[] { 1.0 }, new[] { double.NaN }, new[] { 0 }).MaxValue);
    }

    [Fact]
    public void IgnoreRtListsEachPrecursorOnce()
    {
        var protein = new DocProtein("P", null, null, null);
        var p = Docs.Precursor(protein, "AK", 2, 405.0, "run1.raw", 0, 5, ("y2", 1, 300.0));
        var candidates = new SpectrumCandidates(new[] { p });
        var scan = FakeSource.Spectrum(0, 2, 1, Array.Empty<double>(), Array.Empty<double>(), 1, 400, 410).Header;
        Assert.Single(candidates.Find(scan, "run1.raw", new CandidateOptions { IgnoreRetentionTime = true }));
        Assert.Single(candidates.Find(scan, "run1.raw", new CandidateOptions { ExtendMinutes = 0 }));
        var outside = FakeSource.Spectrum(0, 2, 6, Array.Empty<double>(), Array.Empty<double>(), 1, 400, 410).Header;
        Assert.Single(candidates.Find(outside, "run1.raw", new CandidateOptions { ExtendMinutes = 1 }));
        Assert.Empty(candidates.Find(FakeSource.Spectrum(0, 2, 1, Array.Empty<double>(), Array.Empty<double>(), 1, double.NaN, double.NaN).Header,
            "run1.raw", CandidateOptions.Default));
    }

    [Fact]
    public void AnnotatorDefaultsAndLibraryEdges()
    {
        Assert.Equal(new HashSet<IonType> { IonType.B, IonType.Y }, LabelOptions.Default.Types);
        Assert.Equal(new HashSet<int> { 1, 2 }, LabelOptions.Default.Charges);
        Assert.True(LabelOptions.Default.IncludePrecursor);
        Assert.Equal(LabelSource.AllIons, LabelOptions.Default.Source);

        var protein = new DocProtein("P", null, null, null);
        var peptide = new DocPeptide(protein, "PEPTIDE", "PEPTIDE", null);
        // 9 ppm off Skyline's m/z still reconciles; 11 ppm does not.
        var close = new DocPrecursor(peptide, "PEPTIDE", 2, 400.68726 * (1 + 9e-6), "light", null);
        var far = new DocPrecursor(peptide, "PEPTIDE", 2, 400.68726 * (1 + 11e-6), "light", null);
        var spectrum = FakeSource.Spectrum(0, 2, 1, new[] { 148.06043, 400.7 }, new[] { 10.0, 1.0 }, 1, 399, 402);
        var a = SpectrumAnnotator.Annotate(spectrum, new[] { close, far }, LabelOptions.Default, DocumentSettings.Default);
        Assert.True(a.Precursors.Single(p => p.Precursor == close).Reconciled);
        Assert.False(a.Precursors.Single(p => p.Precursor == far).Reconciled);
        Assert.Contains(a.ByPeak[0]!, m => m.Owner.Precursor == close && m.Ion.Label == "y1");

        // Library mode with a tolerance override wide enough to reach an offset library peak; the
        // library's matching peak is the spectrum's first peak.
        var library = new FakeLibrary().Add("PEPTIDE", 2, new[] { 148.08 }, new[] { 1.0 });
        var options = LabelOptions.Default with { Source = LabelSource.LibraryIons, ToleranceOverride = MzTolerance.Th(0.05), IncludePrecursor = true };
        var lib = SpectrumAnnotator.Annotate(spectrum, new[] { close }, options, DocumentSettings.Default, library);
        Assert.Contains(lib.ByPeak[0]!, m => m.Ion.Label == "y1");
        Assert.Contains(lib.ByPeak[1]!, m => m.Ion.Label == "p++");
        Assert.Equal(1.0, lib.Precursors[0].LibraryDotProduct!.Value, 9);
        // A library entry with nothing in the spectrum has no dot product.
        var missing = new FakeLibrary().Add("PEPTIDE", 2, new[] { 900.0 }, new[] { 1.0 });
        Assert.Null(SpectrumAnnotator.Annotate(spectrum, new[] { close }, options, DocumentSettings.Default, missing).Precursors[0].LibraryDotProduct);
    }
}
