using Glimpse.Core.Assignment;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Heatmaps;
using Xunit;

namespace Glimpse.Tests;

public class DocumentModelTests
{
    private const string Transitions =
        "ProteinName,ProteinAccession,ProteinGene,ProteinLocator,PeptideSequence,PeptideModifiedSequenceMonoisotopicMasses,PeptideLocator,"
        + "ModifiedSequenceMonoisotopicMasses,PrecursorCharge,PrecursorMz,IsotopeLabelType,PrecursorLocator,FragmentIon,ProductCharge,ProductMz\n"
        + "sp|P1|A_HUMAN,P1,GENEA,MoleculeGroup:/sp|P1|A_HUMAN,PEPTIDEK,PEPTIDEK,Molecule:/sp|P1|A_HUMAN/PEPTIDEK,PEPTIDEK,2,465.7350,light,Precursor:/sp|P1|A_HUMAN/PEPTIDEK/2,precursor,2,465.7350\n"
        + "sp|P1|A_HUMAN,P1,GENEA,MoleculeGroup:/sp|P1|A_HUMAN,PEPTIDEK,PEPTIDEK,Molecule:/sp|P1|A_HUMAN/PEPTIDEK,PEPTIDEK,2,465.7350,light,Precursor:/sp|P1|A_HUMAN/PEPTIDEK/2,y4,1,476.2610\n"
        + "\"sp|P2|B_HUMAN, with comma\",P2,,MoleculeGroup:/sp|P2|B_HUMAN,ACDK,AC[+57.021464]DK,Molecule:/sp|P2|B_HUMAN/ACDK,AC[+57.021464]DK,2,268.1100,light,Precursor:/sp|P2|B_HUMAN/ACDK/2,y2,1,262.1500\n";

    private const string Peaks =
        "PrecursorLocator,FileName,FilePath,ReplicateName,BestRetentionTime,MinStartTime,MaxEndTime,DetectionQValue,LibraryDotProduct,IsotopeDotProduct\n"
        + "Precursor:/sp|P1|A_HUMAN/PEPTIDEK/2,run1.raw,D:\\data\\run1.raw,run1,10.5,10.2,10.9,0.001,0.95,0.99\n"
        + "Precursor:/sp|P2|B_HUMAN/ACDK/2,run1.raw,D:\\data\\run1.raw,run1,20.0,19.8,20.3,#N/A,,\n";

    private static DocumentModel Build() =>
        DocumentModelBuilder.Build("doc.sky", CsvTable.Read(new StringReader(Transitions)), CsvTable.Read(new StringReader(Peaks)));

    [Fact]
    public void BuildsHierarchyAndPeaks()
    {
        var model = Build();
        Assert.Equal(2, model.Proteins.Count);
        Assert.Equal("sp|P2|B_HUMAN, with comma", model.Proteins[1].Name);
        Assert.Equal(2, model.Peptides.Count);
        var p1 = model.Precursors[0];
        Assert.Equal(2, p1.Transitions.Count);
        Assert.True(p1.HasPrecursorTransitions);
        Assert.Equal(465.7350, p1.Mz, 4);
        Assert.True(p1.Peaks["RUN1.RAW"].Contains(10.5));
        Assert.Equal(0.001, p1.Peaks["run1.raw"].DetectionQValue);
        Assert.Null(model.Precursors[1].Peaks["run1.raw"].DetectionQValue);
        Assert.Single(model.ResultFiles);
        Assert.Equal("AC[+57.021464]DK", model.Peptides[1].ModifiedSequence);
    }

    [Fact]
    public void LocatorsSelectPeptides()
    {
        var model = Build();
        Assert.Single(model.PeptidesForLocator("MoleculeGroup:/sp|P1|A_HUMAN"));
        Assert.Single(model.PeptidesForLocator("Precursor:/sp|P1|A_HUMAN/PEPTIDEK/2"));
        Assert.Single(model.PeptidesForLocator("Molecule:/sp|P2|B_HUMAN/ACDK"));
        Assert.Empty(model.PeptidesForLocator("Molecule:/sp|P3|C_HUMAN/XYZ"));
    }

    [Fact]
    public void CandidatesNeedPeakAndWindow()
    {
        var model = Build();
        var candidates = new SpectrumCandidates(model.Precursors);
        var ms2 = Header(2, rt: 10.5, lower: 464, upper: 467);
        Assert.Single(candidates.Find(ms2, "run1.raw", CandidateOptions.Default));
        Assert.Empty(candidates.Find(Header(2, rt: 12, lower: 464, upper: 467), "run1.raw", CandidateOptions.Default));
        Assert.Single(candidates.Find(Header(1, rt: 20.1), "run1.raw", CandidateOptions.Default));
        Assert.Empty(candidates.Find(ms2, "other.raw", CandidateOptions.Default));
    }

    [Fact]
    public void HeatmapFillsColumnsNarrowerThanTheCycle()
    {
        // 10 MS1 scans spread over 40 columns: most columns hold no scan, which is binning, not missing data.
        var acc = new IntensityHeatmapAccumulator(mzBinTh: 1, maxColumns: 40);
        acc.Begin(10, 0, 9);
        for (var i = 0; i < 10; i++)
            acc.Observe(new SpectrumData(Header(1, rt: i), new[] { 500.5 }, new[] { 100.0 }, new SpectrumDetails()));
        acc.End();
        var grid = acc.Result!.Ms1;
        Assert.Equal(1, grid.Rows);
        for (var c = 0; c < grid.Columns - 1; c++)
            Assert.False(float.IsNaN(grid.Values[0, c]), $"column {c} is empty");
    }

    private static ScanHeader Header(int level, double rt, double lower = double.NaN, double upper = double.NaN) =>
        new(0, (byte)level, rt, 10f, (lower + upper) / 2, lower, upper, 0, double.NaN, 0, 0, Polarity.Positive, true);
}
