using Glimpse.Core.Assignment;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Scans;
using Xunit;

namespace Glimpse.Tests;

/// <summary>
/// A peptide listed under two proteins is one molecule in the data: one candidate in a spectrum, one in
/// the peptides-per-spectrum count, and an XIC for each copy.
/// </summary>
public class SharedPeptideTests
{
    private readonly DocPrecursor _first;
    private readonly DocPrecursor _copy;
    private readonly DocPrecursor _other;
    private readonly DocumentModel _model;

    public SharedPeptideTests()
    {
        var p1 = new DocProtein("P1", null, null, "MoleculeGroup:/P1");
        var p2 = new DocProtein("P2", null, null, "MoleculeGroup:/P2");
        // AAAK 2+ under both proteins, CCCK 2+ under P1 only; both in the 400-410 window, peaks 1.0-2.0.
        _first = Docs.Precursor(p1, "AAAK", 2, 405.0, "run1.raw", 1.0, 2.0, ("y2", 1, 300.0));
        _other = Docs.Precursor(p1, "CCCK", 2, 408.0, "run1.raw", 1.0, 2.0, ("y2", 1, 301.0));
        _copy = Docs.Precursor(p2, "AAAK", 2, 405.0, "run1.raw", 1.0, 2.0, ("y2", 1, 300.0));
        _model = Docs.Model(p1, p2);
    }

    private static FakeSource Run() => new FakeSource()
        .Ms1(1.5, new[] { 405.0, 408.0 }, new[] { 10.0, 20.0 })
        .Ms2(1.6, 400, 410, new[] { 300.0, 301.0 }, new[] { 5.0, 7.0 });

    [Fact]
    public void ASharedPeptideIsOneCandidateWithItsOwnPeaks()
    {
        var source = Run();
        var index = ScanIndexBuilder.Build(source, false, Array.Empty<IScanObserver>());
        var found = new SpectrumCandidates(_model.Precursors).Find(index.Headers[1], "run1.raw", CandidateOptions.Default);
        Assert.Equal(new[] { _first, _other }, found);

        // Annotated once, its y2 peak is its own: not shared with a second copy of itself.
        var spectrum = source.ReadSpectrum(1, false);
        var annotation = SpectrumAnnotator.Annotate(spectrum, found,
            LabelOptions.Default with { Source = LabelSource.DocumentTransitions }, DocumentSettings.Default);
        Assert.Equal(2, annotation.Precursors.Count);
        var aaak = annotation.Precursors.Single(a => a.Precursor == _first);
        Assert.Equal(5.0, aaak.UniqueIntensity);
        Assert.Single(annotation.ByPeak[0]!);
    }

    [Fact]
    public void DistinctMoleculesKeepsTheFirstCopyAndEveryLabelAndCharge()
    {
        var heavy = new DocPrecursor(_first.Peptide, "AAAK[+8.014199]", 2, 409.0, "heavy", null);
        var triple = new DocPrecursor(_first.Peptide, "AAAK", 3, 270.3, "light", null);
        var sameSequenceOtherLabel = new DocPrecursor(_first.Peptide, "AAAK", 2, 405.0, "heavy", null);
        Assert.Equal(new[] { _copy, _other, heavy, triple, sameSequenceOtherLabel },
            SpectrumCandidates.DistinctMolecules(new[] { _copy, _first, _other, heavy, triple, sameSequenceOtherLabel }));
    }

    [Fact]
    public void TheDocumentPassCountsASharedPeptideOnceAndGivesEachCopyItsXic()
    {
        var observer = new DocumentSignalObserver(_model, "run1.raw", DocumentSettings.Default);
        ScanIndexBuilder.Build(Run(), false, new IScanObserver[] { observer });
        var signals = observer.Result!;

        Assert.Equal(2, signals.PrecursorsInFile);
        Assert.Equal(2, signals.PeptideCount[0]);   // MS1: AAAK and CCCK
        Assert.Equal(2, signals.PeptideCount[1]);   // MS2 400-410: AAAK and CCCK
        Assert.Equal(new[] { 10f }, signals.Xics[_first].Ms1Values);
        Assert.Equal(new[] { 10f }, signals.Xics[_copy].Ms1Values);
        Assert.Equal(new[] { 5f }, signals.Xics[_copy].Ms2Values);
        Assert.Same(_copy, signals.Xics[_copy].Precursor);
        Assert.Same(_first, signals.Xics[_first].Precursor);
    }

    [Fact]
    public void CopiesIntegratedDifferentlyAreMeasuredApart()
    {
        _copy.Peaks["run1.raw"] = new PeakBounds(1.5, 1.4, 1.8, null, null, null);
        var observer = new DocumentSignalObserver(_model, "run1.raw", DocumentSettings.Default);
        ScanIndexBuilder.Build(Run(), false, new IScanObserver[] { observer });
        Assert.Equal(3, observer.Result!.PrecursorsInFile);
        Assert.Equal(3, observer.Result.PeptideCount[0]);
    }
}
