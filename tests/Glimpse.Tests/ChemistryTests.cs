using Glimpse.Core.Assignment;
using Glimpse.Core.Chemistry;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Library;
using Xunit;
using Xunit.Abstractions;

namespace Glimpse.Tests;

public class ChemistryTests
{
    private readonly ITestOutputHelper _out;

    public ChemistryTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void PeptideMassesMatchKnownValues()
    {
        var p = ModifiedSequence.TryParse("PEPTIDE")!;
        Assert.Equal(799.35997, p.MonoisotopicMass, 4);
        Assert.Equal(400.68726, p.PrecursorMz(2), 4);

        var ions = Fragments.Enumerate(p, new[] { IonType.B, IonType.Y, IonType.C, IonType.ZDot, IonType.A, IonType.X, IonType.Z, IonType.ZPrime },
            new[] { 1, 2 }, 2);
        double Mz(string label) => ions.Single(i => i.Label == label).Mz;
        Assert.Equal(227.10263, Mz("b2"), 4);       // P + E + proton
        Assert.Equal(148.06043, Mz("y1"), 4);       // E + water + proton
        Assert.Equal(199.10771, Mz("a2"), 4);       // b2 - CO
        Assert.Equal(244.12918, Mz("c2"), 4);       // b2 + NH3
        Assert.Equal(131.03388, Mz("z1"), 4);       // y1 - NH3
        Assert.Equal(132.04171, Mz("z•1"), 4);      // z + H
        Assert.Equal(133.04953, Mz("z'1"), 4);      // z + 2H
        Assert.Equal(174.03970, Mz("x1"), 4);       // y1 + CO - 2H
        Assert.Equal((227.10263 + Mass.Proton) / 2, Mz("b2++"), 4);
    }

    [Fact]
    public void ModificationsAndMatchKeys()
    {
        var skyline = ModifiedSequence.TryParse("AAAGELQEDSGLC[+57.021464]VLAR")!;
        var blib = ModifiedSequence.TryParse("AAAGELQEDSGLC[+57.02146]VLAR")!;
        Assert.Equal(skyline.MatchKey, blib.MatchKey);
        Assert.Equal("AAAGELQEDSGLCVLAR", skyline.Sequence);
        // Library precursorMZ for this entry is 880.435791 (2+).
        Assert.Equal(880.4358, skyline.PrecursorMz(2), 3);

        var nterm = ModifiedSequence.TryParse("[+42.010565]PEPTIDE")!;
        Assert.Equal(799.35997 + 42.010565, nterm.MonoisotopicMass, 4);
        Assert.Null(ModifiedSequence.TryParse("PEPC[Carbamidomethyl]TIDE"));
    }

    [Fact]
    public void ParsesSkylineFragmentNames()
    {
        Assert.Equal((IonType.Y, 7, "y7"), Fragments.ParseSkylineFragment("y7", 1));
        Assert.Equal((IonType.B, 4, "b4++"), Fragments.ParseSkylineFragment("b4", 2));
        Assert.Equal((IonType.Precursor, 1, "M+1"), Fragments.ParseSkylineFragment("precursor [M+1]", 2));
        Assert.Equal(IonType.Precursor, Fragments.ParseSkylineFragment("precursor", 2).Type);
    }

    [Fact]
    public void TolerancesFollowSkylineRules()
    {
        var ppm = MzTolerance.Ppm(10);
        var (lo, hi) = ppm.WindowAt(1000);
        Assert.Equal(999.99, lo, 6);
        Assert.Equal(1000.01, hi, 6);
        var th = MzTolerance.Th(0.5);
        Assert.Equal(1.0, th.WidthAt(500), 9);
        Assert.Equal(MzTolerance.Ppm(20), MzTolerance.Parse("20 ppm"));

        var settings = DocumentSettings.Parse(
            "<settings_summary><transition_settings>"
            + "<transition_filter fragment_types=\"y,b,p\" product_charges=\"1,2\" />"
            + "<transition_libraries ion_match_tolerance=\"10\" ion_match_tolerance_unit=\"ppm\" />"
            + "<transition_full_scan acquisition_method=\"DIA\" product_mass_analyzer=\"centroided\" product_res=\"10\" precursor_mass_analyzer=\"orbitrap\" precursor_res=\"60000\" precursor_res_mz=\"200\" />"
            + "</transition_settings><peptide_settings><peptide_libraries pick=\"library\"><bibliospec_lite_library name=\"lib\" file_name_hint=\"test-document.blib\" /></peptide_libraries></peptide_settings></settings_summary>");
        Assert.Equal(MzTolerance.Ppm(10), settings.ProductTolerance);
        Assert.Equal("orbitrap", settings.PrecursorTolerance.Analyzer);
        Assert.Equal(MzTolerance.Ppm(10), settings.IonMatchTolerance);
        Assert.Equal(new[] { "test-document.blib" }, settings.LibraryFiles);
        Assert.Equal("DIA", settings.AcquisitionMethod);
    }

    [Fact]
    public void AnnotatorLabelsAndSharesPeaks()
    {
        var protein = new DocProtein("P1", null, null, null);
        var peptide = new DocPeptide(protein, "PEPTIDE", "PEPTIDE", null);
        var precursor = new DocPrecursor(peptide, "PEPTIDE", 2, 400.68726, "light", null);
        precursor.Transitions.Add(new DocTransition("y1", 1, 148.06043));
        peptide.Precursors.Add(precursor);

        var spectrum = new SpectrumData(
            new ScanHeader(0, 2, 10, 5f, 400.7, 399.7, 401.7, 0, double.NaN, 0, 4, Polarity.Positive, true),
            new[] { 148.0605, 227.1027, 300.0, 400.6873 },
            new[] { 100.0, 50.0, 25.0, 10.0 },
            new SpectrumDetails());

        var all = SpectrumAnnotator.Annotate(spectrum, new[] { precursor }, LabelOptions.Default, DocumentSettings.Default);
        var a = Assert.Single(all.Precursors);
        Assert.True(a.Reconciled);
        Assert.Contains(all.ByPeak[0]!, m => m.Ion.Label == "y1");
        Assert.Contains(all.ByPeak[1]!, m => m.Ion.Label == "b2");
        Assert.Contains(all.ByPeak[3]!, m => m.Ion.Type == IonType.Precursor);
        Assert.Null(all.ByPeak[2]);
        Assert.Equal(160.0 / 185.0, all.AssignedFraction, 6);

        var doc = SpectrumAnnotator.Annotate(spectrum, new[] { precursor },
            LabelOptions.Default with { Source = LabelSource.DocumentTransitions }, DocumentSettings.Default);
        Assert.Equal(1, doc.AssignedPeaks);
        Assert.Equal(100.0 / 185.0, doc.AssignedFraction, 6);
    }

    [Fact]
    public void ReadsTheTestDocumentLibrary()
    {
        const string path = @"D:\2026-0211-apex-flex-tests-astral\test-document.blib";
        if (!File.Exists(path))
        {
            _out.WriteLine("test-document.blib not present; skipped.");
            return;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lib = BlibLibrary.Open(path);
        _out.WriteLine($"{lib.Count:N0} entries indexed in {clock.ElapsedMilliseconds} ms");
        Assert.True(lib.Count > 1000);
        var entry = lib.Find("AAAGELQEDSGLC[+57.021464]VLAR", 2);
        Assert.NotNull(entry);
        Assert.Equal(18, entry!.Mz.Length);
        Assert.Equal(1.0, entry.Intensity.Max(), 6);
        Assert.Equal("GENERIC Q-VALUE", entry.ScoreType);
        Assert.NotNull(entry.SourceFile);
        _out.WriteLine($"{entry.ModifiedSequence} {entry.Charge}+ from {entry.SourceFile} ({entry.IdFile}), score {entry.Score} {entry.ScoreType}, RT {entry.RetentionTime}");
    }
}

public class XicTests
{
    [Fact]
    public void SumOnGridAddsOnlyInsideEachTrace()
    {
        var a = (new[] { 1.0, 2.0, 3.0 }, new[] { 10.0, 20.0, 10.0 });
        var b = (new[] { 2.0, 3.0 }, new[] { 5.0, 5.0 });
        var (t, y) = Glimpse.Core.Chromatograms.XicTraces.SumOnGrid(new[] { a, b }, 0.5);
        Assert.Equal(1.0, t[0], 9);
        Assert.Equal(10.0, y[0], 9);          // only a at t=1
        Assert.Equal(15.0, y[1], 9);          // a interpolated at 1.5, b not yet started
        Assert.Equal(25.0, y[2], 9);          // a=20 + b=5 at t=2
        Assert.Equal(15.0, y[^1], 9);         // a=10 + b=5 at t=3
    }
}
