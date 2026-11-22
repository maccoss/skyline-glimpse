using Glimpse.Core.Chemistry;
using Glimpse.Core.Document;
using Xunit;

namespace Glimpse.Tests;

/// <summary>Tolerances, sequences, fragment names, CSV, settings and the report builder at their edges.</summary>
public class ParsingTests
{
    [Fact]
    public void ResolvingPowerTolerancesFollowSkylineFormulas()
    {
        // Orbitrap: width = mz^1.5 / (sqrt(resMz) * res / 2).
        var orbitrap = new MzTolerance("orbitrap", 60000, 200);
        Assert.Equal(Math.Pow(800, 1.5) / (Math.Sqrt(200) * 60000 / 2), orbitrap.WidthAt(800), 12);
        Assert.Equal(orbitrap.WidthAt(800) / 2, new MzTolerance("orbitrap", 60000, 200, SelectiveExtraction: true).WidthAt(800), 12);
        // TOF: width = mz / (res / 2).
        Assert.Equal(800 / 10000.0, new MzTolerance("tof", 20000).WidthAt(800), 12);
        // FT-ICR: width = mz^2 / (resMz * res / 2).
        Assert.Equal(800.0 * 800 / (400 * 100000 / 2.0), new MzTolerance("ft_icr", 100000, 400).WidthAt(800), 12);
        Assert.False(new MzTolerance("orbitrap", 60000).IsUsable);
        Assert.Equal(0, new MzTolerance("orbitrap", 60000).WidthAt(800));
        Assert.Contains("Orbitrap 60000 at m/z 200", orbitrap.Describe());
        Assert.Contains("TOF", new MzTolerance("tof", 20000).Describe());
        Assert.Contains("FT-ICR", new MzTolerance("ft_icr", 1, 1).Describe());
        Assert.Equal("±10 ppm", MzTolerance.Ppm(10).Describe());
        Assert.Equal("±0.5 m/z", MzTolerance.Th(0.5).Describe());
    }

    [Theory]
    [InlineData("10 ppm", "centroided", 10)]
    [InlineData(" 0.02 m/z ", "qit", 0.02)]
    [InlineData("0.5 Th", "qit", 0.5)]
    [InlineData("0.5Da", "qit", 0.5)]
    [InlineData("0.7", "qit", 0.7)]
    public void ParsesToleranceText(string text, string analyzer, double value)
    {
        var t = MzTolerance.Parse(text)!;
        Assert.Equal(analyzer, t.Analyzer);
        Assert.Equal(value, t.Resolution, 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ppm")]
    [InlineData("-5 ppm")]
    [InlineData("5 furlongs")]
    public void RejectsBadToleranceText(string? text) => Assert.Null(MzTolerance.Parse(text));

    [Fact]
    public void FromSkylineRejectsUnusableSettings()
    {
        Assert.Null(MzTolerance.FromSkyline(null, "10", null, null));
        Assert.Null(MzTolerance.FromSkyline("centroided", "x", null, null));
        Assert.Null(MzTolerance.FromSkyline("orbitrap", "60000", null, null));
        Assert.True(MzTolerance.FromSkyline("Orbitrap", "60000", "200", "TRUE")!.SelectiveExtraction);
    }

    [Fact]
    public void ModifiedSequenceEdgeCases()
    {
        Assert.Null(ModifiedSequence.TryParse(null));
        Assert.Null(ModifiedSequence.TryParse("  "));
        Assert.Null(ModifiedSequence.TryParse("PEP[+1"));
        Assert.Null(ModifiedSequence.TryParse("PEPB1"));
        Assert.Null(ModifiedSequence.TryParse("[+42.0]"));
        var lower = ModifiedSequence.TryParse("pep(+1.0)T{-2.0}")!;
        Assert.Equal("PEPT", lower.Sequence);
        Assert.Equal(-1.0, lower.ModificationDeltas.Sum(), 9);
        Assert.Equal(ModifiedSequence.TryParse("PEPT")!.MonoisotopicMass - 1.0, lower.MonoisotopicMass, 9);
        var nterm = ModifiedSequence.TryParse("_[+42.0106]AK-")!;
        Assert.Equal(42.0106, nterm.NTerminalDelta, 9);
        Assert.Contains("|0:42.01", nterm.MatchKey);
        Assert.Equal("AK", ModifiedSequence.TryParse("AK")!.MatchKey);
        Assert.NotEqual(ModifiedSequence.TryParse("AC[+57.02]K")!.MatchKey, ModifiedSequence.TryParse("AC[+58.02]K")!.MatchKey);
        Assert.True(double.IsNaN(Mass.Residue('1')));
        Assert.True(double.IsNaN(Mass.Residue('B')));
    }

    [Fact]
    public void FragmentNamesAndIsotopes()
    {
        Assert.Equal((IonType.ZDot, 5, "z•5"), Fragments.ParseSkylineFragment("z•5", 1));
        Assert.Equal((IonType.Z, 5, "z5"), Fragments.ParseSkylineFragment("z5", 1));
        Assert.Equal((IonType.ZPrime, 4, "z'4++"), Fragments.ParseSkylineFragment("z'4", 2));
        Assert.Equal(IonType.Other, Fragments.ParseSkylineFragment("z•", 1).Type);
        Assert.Equal((IonType.C, 3, "c3"), Fragments.ParseSkylineFragment("c3", 1));
        Assert.Equal((IonType.A, 2, "a2"), Fragments.ParseSkylineFragment("a2", 1));
        Assert.Equal((IonType.X, 2, "x2"), Fragments.ParseSkylineFragment("x2", 1));
        Assert.Equal((IonType.Y, 7, "y7 -98"), Fragments.ParseSkylineFragment("y7 -98", 1));
        Assert.Equal((IonType.Other, 0, "Custom++"), Fragments.ParseSkylineFragment("Custom", 2));
        Assert.Equal((IonType.Other, 0, "q4"), Fragments.ParseSkylineFragment("q4", 1));
        Assert.Equal(IonType.Precursor, Fragments.ParseSkylineFragment("precursor [M+2]", 3).Type);
        Assert.Equal(2, Fragments.ParseSkylineFragment("precursor [M+2]", 3).Ordinal);

        var iso = Fragments.PrecursorIsotopes(500, 2, 3);
        Assert.Equal(new[] { "M", "M+1", "M+2", "M+3" }, iso.Select(i => i.Label));
        Assert.Equal(500 + 3 * Mass.IsotopeSpacing / 2, iso[3].Mz, 9);
        Assert.Equal(new[] { "a", "b", "c", "x", "y", "z", "z•", "z'", "precursor", "?" },
            new[] { IonType.A, IonType.B, IonType.C, IonType.X, IonType.Y, IonType.Z, IonType.ZDot, IonType.ZPrime, IonType.Precursor, IonType.Other }.Select(Fragments.Name));
        Assert.True(Fragments.IsNTerminal(IonType.C));
        Assert.False(Fragments.IsNTerminal(IonType.ZDot));
    }

    [Fact]
    public void CsvHandlesQuotingAndLineEndings()
    {
        var csv = "a,b,c\r\n\"x, y\",\"he said \"\"hi\"\"\",\"two\nlines\"\r\n1,2,3";
        var t = CsvTable.Read(new StringReader(csv));
        Assert.Equal(new[] { "a", "b", "c" }, t.Header);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("x, y", t.Rows[0][0]);
        Assert.Equal("he said \"hi\"", t.Rows[0][1]);
        Assert.Equal("two\nlines", t.Rows[0][2]);
        Assert.Equal(3, CsvTable.GetInt(t.Rows[1], 2));
        Assert.Equal("", CsvTable.Get(t.Rows[1], 7));
        Assert.Equal("", CsvTable.Get(t.Rows[1], -1));
        Assert.True(double.IsNaN(CsvTable.GetDouble(t.Rows[0], 0)));
        Assert.Null(CsvTable.GetNullableDouble(t.Rows[0], 0));
        Assert.Equal(0, CsvTable.GetInt(t.Rows[0], 0));
        Assert.Equal(1, t.Column("B", "b"));
        Assert.Equal(-1, t.Column("zzz"));
        Assert.Empty(CsvTable.Read(new StringReader("")).Header);
        Assert.Single(CsvTable.Read(new StringReader("h\nv\n")).Rows);
    }

    [Fact]
    public void BuilderRequiresColumnsAndAcceptsDisplayNames()
    {
        Assert.Throws<InvalidDataException>(() => DocumentModelBuilder.Build(null, CsvTable.Read(new StringReader("Foo\n1")), null));
        // Display-name headers, no locators: proteins/peptides/precursors keyed by name and sequence.
        var t = CsvTable.Read(new StringReader(
            "Protein Name,Peptide Sequence,Precursor Charge,Precursor Mz,Fragment Ion,Product Charge,Product Mz\n"
            + "P1,AAK,2,200.1,y1,1,147.1\nP1,AAK,2,200.1,y2,1,218.1\nP1,AAK,3,133.7,y1,1,147.1\nP1,AAK,3,133.7,bad,1,\n"));
        var model = DocumentModelBuilder.Build(null, t, null);
        var peptide = Assert.Single(model.Peptides);
        Assert.Equal("AAK", peptide.ModifiedSequence);      // falls back to the plain sequence
        Assert.Equal(2, peptide.Precursors.Count);
        Assert.Equal(2, peptide.Precursors[0].Transitions.Count);
        Assert.Single(peptide.Precursors[1].Transitions);   // the row without a product m/z is skipped
        Assert.Equal("light", peptide.Precursors[0].LabelType);
        Assert.Empty(model.ResultFiles);
        Assert.Equal(3, model.TransitionCount);
        Assert.Empty(model.PeptidesForLocator(""));
        Assert.Equal("AAK++", peptide.Precursors[0].ToString());
    }

    [Fact]
    public void PeakRowsWithUnknownPrecursorsOrNoFileAreSkipped()
    {
        var transitions = CsvTable.Read(new StringReader(
            "ProteinName,PeptideSequence,PrecursorCharge,PrecursorMz,FragmentIon,ProductCharge,ProductMz,PrecursorLocator\n"
            + "P1,AAK,2,200.1,y1,1,147.1,Precursor:/P1/AAK/2\n"));
        var peaks = CsvTable.Read(new StringReader(
            "PrecursorLocator,FileName,BestRetentionTime,MinStartTime,MaxEndTime\n"
            + "Precursor:/P1/AAK/2,a.raw,5,4.5,5.5\nPrecursor:/P9/X/2,a.raw,1,0.5,1.5\nPrecursor:/P1/AAK/2,,1,1,1\n"
            + "Precursor:/P1/AAK/2,b.raw,,,\n"));
        var model = DocumentModelBuilder.Build(null, transitions, peaks);
        Assert.Single(model.Precursors[0].Peaks);
        Assert.Equal(new[] { "a.raw", "b.raw" }, model.ResultFiles.Select(f => f.FileName));
        Assert.False(new PeakBounds(double.NaN, 2, 1, null, null, null).HasBoundaries);
        Assert.True(new PeakBounds(5, 4, 6, null, null, null).Contains(6.5, extendMinutes: 0.5));
        Assert.False(new PeakBounds(5, 4, 6, null, null, null).Contains(6.5));
        // A peak-report without the required columns adds nothing.
        Assert.Empty(DocumentModelBuilder.Build(null, transitions, CsvTable.Read(new StringReader("X\n1"))).ResultFiles);
    }

    [Fact]
    public void SettingsFallBackToDefaults()
    {
        var empty = DocumentSettings.Parse("<settings_summary />");
        Assert.Equal(DocumentSettings.Default.ProductTolerance, empty.ProductTolerance);
        Assert.Empty(empty.LibraryFiles);
        Assert.Null(empty.AcquisitionMethod);
        var mz = DocumentSettings.Parse("<s><transition_libraries ion_match_tolerance=\"0.5\" ion_match_tolerance_unit=\"Mz\" />"
            + "<transition_full_scan product_mass_analyzer=\"nonsense\" product_res=\"x\" /><transition_filter fragment_types=\"\" product_charges=\"x\" /></s>");
        Assert.Equal(MzTolerance.Th(0.5), mz.IonMatchTolerance);
        Assert.Equal(DocumentSettings.Default.ProductTolerance, mz.ProductTolerance);
        Assert.Equal(DocumentSettings.Default.FragmentTypes, mz.FragmentTypes);
        Assert.Equal(DocumentSettings.Default.ProductCharges, mz.ProductCharges);
    }
}
