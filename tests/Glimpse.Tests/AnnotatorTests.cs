using Glimpse.Core.Assignment;
using Glimpse.Core.Chemistry;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Xunit;

namespace Glimpse.Tests;

public class AnnotatorTests
{
    // PEPTIDE 2+: m/z 400.68726; b2 227.10263, y1 148.06043, y2 263.08737, b3 324.15539.
    private const double Y1 = 148.06043, B2 = 227.10263, Y2 = 263.08737, B3 = 324.15539;

    private static DocPrecursor Peptide(string sequence = "PEPTIDE", double mz = 400.68726, string? modified = null)
    {
        var protein = new DocProtein("P", null, null, null);
        var peptide = new DocPeptide(protein, sequence, modified ?? sequence, null);
        var precursor = new DocPrecursor(peptide, modified ?? sequence, 2, mz, "light", null);
        precursor.Transitions.Add(new DocTransition("y1", 1, Y1));
        precursor.Transitions.Add(new DocTransition("precursor", 2, mz));
        peptide.Precursors.Add(precursor);
        protein.Peptides.Add(peptide);
        return precursor;
    }

    private static SpectrumData Ms2(double[] mz, double[] intensity) =>
        FakeSource.Spectrum(0, 2, 10, mz, intensity, 5f, 399.7, 401.7);

    [Fact]
    public void LibraryIonsAreLimitedToLibraryPeaks()
    {
        var p = Peptide();
        var spectrum = Ms2(new[] { Y1, B2, Y2, B3 }, new[] { 10.0, 20.0, 30.0, 40.0 });
        var library = new FakeLibrary().Add("PEPTIDE", 2, new[] { Y2, B3 }, new[] { 0.5, 1.0 });
        var options = LabelOptions.Default with { Source = LabelSource.LibraryIons, IncludePrecursor = false };
        var a = SpectrumAnnotator.Annotate(spectrum, new[] { p }, options, DocumentSettings.Default, library);
        var ann = Assert.Single(a.Precursors);
        Assert.NotNull(ann.Library);
        Assert.Equal(2, ann.MatchedIons);
        Assert.Null(a.ByPeak[0]);                 // y1 is a document transition, but not a library peak
        Assert.Null(a.ByPeak[1]);
        Assert.NotNull(a.ByPeak[2]);
        Assert.NotNull(a.ByPeak[3]);
        // Measured y2/b3 = 30/40 against library 0.5/1.0 in sqrt space: close to but below 1.
        Assert.InRange(ann.LibraryDotProduct!.Value, 0.95, 1.0);
    }

    [Fact]
    public void LibraryModeWithoutAnEntryFallsBackToDocumentTransitions()
    {
        var p = Peptide();
        var spectrum = Ms2(new[] { Y1, B2 }, new[] { 10.0, 20.0 });
        var a = SpectrumAnnotator.Annotate(spectrum, new[] { p },
            LabelOptions.Default with { Source = LabelSource.LibraryIons }, DocumentSettings.Default, new FakeLibrary());
        Assert.Equal("y1", a.ByPeak[0]!.Single().Ion.Label);
        Assert.Null(a.ByPeak[1]);
        Assert.Null(a.Precursors[0].LibraryDotProduct);
    }

    [Fact]
    public void UnreconciledSequenceUsesOnlyDocumentTransitions()
    {
        // Skyline's m/z disagrees with the sequence (e.g. a label written as a name): no invented ions.
        var p = Peptide(mz: 410.0);
        var spectrum = Ms2(new[] { Y1, B2 }, new[] { 10.0, 20.0 });
        var a = SpectrumAnnotator.Annotate(spectrum, new[] { p }, LabelOptions.Default, DocumentSettings.Default);
        Assert.False(a.Precursors[0].Reconciled);
        Assert.Null(a.ByPeak[1]);
        Assert.NotNull(a.ByPeak[0]);

        var named = Peptide(modified: "PEPTIDE[Label]");
        Assert.False(SpectrumAnnotator.Annotate(spectrum, new[] { named }, LabelOptions.Default, DocumentSettings.Default).Precursors[0].Reconciled);
    }

    [Fact]
    public void Ms1UsesPrecursorTransitionsOrIsotopes()
    {
        var p = Peptide();
        var spectrum = FakeSource.Spectrum(0, 1, 10, new[] { 400.68726, 401.18894 }, new[] { 100.0, 50.0 }, 5f, double.NaN, double.NaN);
        var withTransition = SpectrumAnnotator.Annotate(spectrum, new[] { p }, LabelOptions.Default, DocumentSettings.Default);
        Assert.Equal("M", withTransition.ByPeak[0]!.Single().Ion.Label);
        Assert.Null(withTransition.ByPeak[1]);        // only M is a document transition

        p.Transitions.RemoveAll(t => t.IsPrecursor);
        var isotopes = SpectrumAnnotator.Annotate(spectrum, new[] { p }, LabelOptions.Default, DocumentSettings.Default);
        Assert.Equal("M+1", isotopes.ByPeak[1]!.Single().Ion.Label);
        Assert.Equal(1.0, isotopes.AssignedFraction, 9);
    }

    [Fact]
    public void SharedPeaksAreSplitIntoExplainedAndUnique()
    {
        var a = Peptide();
        var b = Peptide();   // same ions: every matched peak is shared
        var spectrum = Ms2(new[] { Y1, B2, 500.0 }, new[] { 10.0, 20.0, 70.0 });
        var options = LabelOptions.Default with { IncludePrecursor = false };
        var result = SpectrumAnnotator.Annotate(spectrum, new[] { a, b }, options, DocumentSettings.Default);
        Assert.All(result.Precursors, p =>
        {
            Assert.Equal(30.0, p.ExplainedIntensity, 9);
            Assert.Equal(0.0, p.UniqueIntensity, 9);
        });
        Assert.Equal(30.0, result.AssignedIntensity, 9);   // shared peaks counted once
        Assert.Equal(2, result.AssignedPeaks);
        Assert.Equal(2, result.ByPeak[0]!.Count);
        Assert.Equal(new[] { 0, 1 }, result.Precursors.Select(p => p.ColorIndex));
    }

    [Fact]
    public void MostExplainedPrecursorComesFirst()
    {
        var weak = Peptide();
        var strongProtein = new DocProtein("Q", null, null, null);
        var strongPeptide = new DocPeptide(strongProtein, "X", "X", null);
        var strong = new DocPrecursor(strongPeptide, "X", 2, 400.7, "light", null);
        strong.Transitions.Add(new DocTransition("y5", 1, 500.0));
        var none = new DocPrecursor(strongPeptide, "X", 3, 400.8, "light", null);
        none.Transitions.Add(new DocTransition("y9", 1, 900.0));
        var spectrum = Ms2(new[] { Y1, 500.0 }, new[] { 10.0, 70.0 });
        var result = SpectrumAnnotator.Annotate(spectrum, new[] { none, weak, strong },
            LabelOptions.Default with { Source = LabelSource.DocumentTransitions }, DocumentSettings.Default);
        Assert.Equal(new[] { strong, weak, none }, result.Precursors.Select(p => p.Precursor));
        Assert.Equal(0, result.Precursors[2].MatchedIons);
    }

    [Fact]
    public void ToleranceOverrideAndChargeCap()
    {
        var p = Peptide();
        var spectrum = Ms2(new[] { Y1 + 0.01 }, new[] { 10.0 });        // 67 ppm off
        Assert.Null(SpectrumAnnotator.Annotate(spectrum, new[] { p }, LabelOptions.Default, DocumentSettings.Default with { IonMatchTolerance = MzTolerance.Ppm(10) }).ByPeak[0]);
        var wide = SpectrumAnnotator.Annotate(spectrum, new[] { p }, LabelOptions.Default with { ToleranceOverride = MzTolerance.Th(0.02) }, DocumentSettings.Default);
        var match = wide.ByPeak[0]!.Single(m => m.Ion.Label == "y1");
        Assert.InRange(match.PpmError, 60, 75);

        // Charges above the precursor's are never labeled.
        var ions = Fragments.Enumerate(ModifiedSequence.TryParse("PEPTIDE")!, new[] { IonType.Y }, new[] { 1, 2, 3 }, 2);
        Assert.DoesNotContain(ions, i => i.Charge == 3);
        Assert.Empty(Fragments.Enumerate(ModifiedSequence.TryParse("K")!, new[] { IonType.Y }, new[] { 1 }, 2));
    }

    [Fact]
    public void EmptyAnnotationHasNothingAssigned()
    {
        var spectrum = Ms2(new[] { 100.0 }, new[] { 5.0 });
        var empty = SpectrumAnnotation.Empty(spectrum);
        Assert.Empty(empty.Precursors);
        Assert.Equal(0, empty.AssignedFraction);
        Assert.Equal(5.0, empty.TotalIntensity);
        var zero = SpectrumAnnotation.Empty(Ms2(Array.Empty<double>(), Array.Empty<double>()));
        Assert.Equal(0, zero.AssignedFraction);
    }
}
