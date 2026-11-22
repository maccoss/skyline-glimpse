using Glimpse.Core.Assignment;
using Glimpse.Core.Document;
using Glimpse.Skyline;
using Parquet.Schema;
using Xunit;

namespace Glimpse.Tests;

/// <summary>
/// The pieces that took the document load from 45 s to 7 s: locators built from names instead of asked
/// for, reports read as Parquet, Glimpse's own requests queued, and the first file opened by itself.
/// </summary>
public class FastLoadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("glimpse-fastload-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void LocatorsAreBuiltTheWaySkylineBuildsThem()
    {
        // Strings Skyline returned for this document's PeptideLocator and PrecursorLocator columns.
        Assert.Equal("Molecule:/sp|O95487|SC24B_HUMAN/VYAMC[+57.021464]QIK",
            SkylineLocators.Peptide("sp|O95487|SC24B_HUMAN", "VYAMC[+57.021464]QIK"));
        Assert.Equal("Precursor:/sp|O95487|SC24B_HUMAN/NILPMTPVWAPVPNLNADLKK/light+++",
            SkylineLocators.Precursor("sp|O95487|SC24B_HUMAN", "NILPMTPVWAPVPNLNADLKK", "light", 3));
        Assert.Equal("MoleculeGroup:/sp|O95487|SC24B_HUMAN", SkylineLocators.Protein("sp|O95487|SC24B_HUMAN"));

        // ElementLocator.QuoteIfSpecial: / ? & = and " force quotes, and quotes are doubled.
        Assert.Equal("plain name|x", SkylineLocators.Quote("plain name|x"));
        Assert.Equal("\"a/b\"", SkylineLocators.Quote("a/b"));
        Assert.Equal("\"a?b\"", SkylineLocators.Quote("a?b"));
        Assert.Equal("\"a&b\"", SkylineLocators.Quote("a&b"));
        Assert.Equal("\"a=b\"", SkylineLocators.Quote("a=b"));
        Assert.Equal("\"say \"\"hi\"\"\"", SkylineLocators.Quote("say \"hi\""));
        Assert.Equal("MoleculeGroup:/\"Decoys/1\"", SkylineLocators.Protein("Decoys/1"));

        // Transition.GetChargeIndicator in the invariant culture.
        Assert.Equal("", SkylineLocators.ChargeIndicator(0));
        Assert.Equal("+", SkylineLocators.ChargeIndicator(1));
        Assert.Equal("++++", SkylineLocators.ChargeIndicator(4));
        Assert.Equal(", +5", SkylineLocators.ChargeIndicator(5));
        Assert.Equal("-", SkylineLocators.ChargeIndicator(-1));
        Assert.Equal("--", SkylineLocators.ChargeIndicator(-2));
        Assert.Equal(", -3", SkylineLocators.ChargeIndicator(-3));
    }

    [Fact]
    public void AReplicateOfSeveralFilesLeavesItsAmbiguousPrecursorsWithoutAPeak()
    {
        var transitions = CsvTable.Read(new StringReader(
            "ProteinName,PeptideSequence,PrecursorCharge,PrecursorMz,ProductMz\n"
            + "P1,AAK,2,200,147\nP1,CCK,2,210,147\nP1,DDK,2,220,147\n"));
        var model = DocumentModelBuilder.Build(null, transitions, null);
        // A report filtered to a replicate of two files: a row per file and nothing saying which is which.
        // AAK differs between the files, CCK is the same in both, DDK has a single row.
        var peaks = CsvTable.Read(new StringReader(
            "ProteinName,PeptideModifiedSequenceMonoisotopicMasses,PrecursorCharge,BestRetentionTime,MinStartTime,MaxEndTime\n"
            + "P1,AAK,2,5,4,6\n"
            + "P1,CCK,2,7,6,8\n"
            + "P1,DDK,2,9,8,10\n"
            + "P1,AAK,2,15,14,16\n"
            + "P1,CCK,2,7,6,8\n"
            + "P1,AAK,2,25,24,26\n"));
        Assert.Equal(2, DocumentModelBuilder.ApplyPeaks(model, peaks, "f1.raw", out var ambiguous));
        Assert.Equal(1, ambiguous);
        Assert.Empty(model.Precursors[0].Peaks);                        // not whichever AAK row came last
        Assert.Equal(7, model.Precursors[1].Peaks["f1.raw"].Apex);      // identical rows are harmless
        Assert.Equal(9, model.Precursors[2].Peaks["f1.raw"].Apex);

        // A one-file replicate is unaffected, and nothing is ambiguous.
        var single = CsvTable.Read(new StringReader(
            "ProteinName,PeptideModifiedSequenceMonoisotopicMasses,PrecursorCharge,BestRetentionTime,MinStartTime,MaxEndTime\n"
            + "P1,AAK,2,5,4,6\n"));
        Assert.Equal(1, DocumentModelBuilder.ApplyPeaks(model, single, "f2.raw", out var none));
        Assert.Equal(0, none);
        Assert.Equal(5, model.Precursors[0].Peaks["f2.raw"].Apex);
    }

    [Fact]
    public void SkylineSelectionsAreLookedUpNotScanned()
    {
        var p1 = new DocProtein("P1", null, null, "MoleculeGroup:/P1");
        var slash = new DocProtein("Decoys/1", null, null, SkylineLocators.Protein("Decoys/1"));
        var aak = new DocPeptide(p1, "AAK", "AAK", "Molecule:/P1/AAK");
        var ccr = new DocPeptide(p1, "CCR", "CCR", "Molecule:/P1/CCR");
        var decoy = new DocPeptide(slash, "KAA", "KAA", SkylineLocators.Peptide("Decoys/1", "KAA"));
        p1.Peptides.AddRange(new[] { aak, ccr });
        slash.Peptides.Add(decoy);
        var model = new DocumentModel(null, new[] { p1, slash }, Array.Empty<ResultFileInfo>());

        // Document order, whatever the order of the lookups that found them.
        Assert.Equal(new[] { aak, ccr }, model.PeptidesForLocator("MoleculeGroup:/P1"));
        // A quoted name holding a slash: its protein, the peptide, and a precursor beneath it.
        Assert.Equal(new[] { decoy }, model.PeptidesForLocator("MoleculeGroup:/\"Decoys/1\""));
        Assert.Equal(new[] { decoy }, model.PeptidesForLocator("Molecule:/\"Decoys/1\"/KAA"));
        Assert.Equal(new[] { decoy }, model.PeptidesForLocator("Precursor:/\"Decoys/1\"/KAA/light++"));
        Assert.Empty(model.PeptidesForLocator("MoleculeGroup:/P2"));
        Assert.Empty(model.PeptidesForLocator("Molecule:/P1/AA"));

        // Every protein of a 5,000-protein, 50,000-peptide document selected in Skyline. Comparing each
        // locator with every peptide took 10 s here; a lookup takes milliseconds.
        var proteins = Enumerable.Range(0, 5000).Select(i => new DocProtein($"P{i}", null, null, $"MoleculeGroup:/P{i}")).ToArray();
        foreach (var protein in proteins)
        {
            for (var j = 0; j < 10; j++)
                protein.Peptides.Add(new DocPeptide(protein, $"K{j}", $"K{j}", $"Molecule:/{protein.Name}/K{j}"));
        }
        var big = new DocumentModel(null, proteins, Array.Empty<ResultFileInfo>());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var selected = proteins.SelectMany(p => big.PeptidesForLocator(p.Locator!)).Distinct().Count();
        clock.Stop();
        Assert.Equal(50000, selected);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Selection lookup took {clock.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public void SkylineLocatorsNameTheTreeElementToSelect()
    {
        var transitions = CsvTable.Read(new StringReader(
            "ProteinName,PeptideSequence,PeptideModifiedSequenceMonoisotopicMasses,PrecursorCharge,PrecursorMz,IsotopeLabelType,FragmentIon,ProductMz\n"
            + "P1,AAK,AAK,2,200,light,y2,147\n"
            + "P1,AAK,AAK,3,134,light,y2,147\n"
            + "Decoys/1,KAA,KAA,2,200,light,y2,147\n"));
        var model = DocumentModelBuilder.Build(null, transitions, null);
        var protein = model.Proteins[0];
        var peptide = model.Peptides[0];

        Assert.Same(protein, model.ElementForLocator("MoleculeGroup:/P1"));
        Assert.Same(peptide, model.ElementForLocator("Molecule:/P1/AAK"));
        Assert.Same(model.Precursors[1], model.ElementForLocator("Precursor:/P1/AAK/light+++"));
        // A transition selects its precursor; one of a precursor the document lacks, the peptide above it.
        Assert.Same(model.Precursors[0], model.ElementForLocator("Transition:/P1/AAK/light++/y2"));
        Assert.Same(peptide, model.ElementForLocator("Precursor:/P1/AAK/heavy++"));
        // Result qualifiers are ignored, quoted names with a slash work, and unknown elements give null.
        Assert.Same(peptide, model.ElementForLocator("Molecule:/P1/AAK?ResultFile=a.raw"));
        Assert.Same(model.Proteins[1], model.ElementForLocator("MoleculeGroup:/\"Decoys/1\""));
        Assert.Same(model.Peptides[1], model.ElementForLocator("Molecule:/\"Decoys/1\"/KAA"));
        Assert.Null(model.ElementForLocator("MoleculeGroup:/P9"));
        Assert.Null(model.ElementForLocator(""));
    }

    [Fact]
    public void ASelectionOfProteinsPeptidesAndPrecursorsGivesEachPeptideOnceInDocumentOrder()
    {
        var p1 = new DocProtein("P1", null, null, null);
        var p2 = new DocProtein("P2", null, null, null);
        var a = Docs.Precursor(p1, "AAK", 2, 300, "f.raw", 1, 2);
        var b = Docs.Precursor(p1, "CCK", 2, 310, "f.raw", 1, 2);
        var c = Docs.Precursor(p2, "DDK", 2, 320, "f.raw", 1, 2);
        var d = Docs.Precursor(p2, "EEK", 3, 330, "f.raw", 1, 2);
        var d4 = Docs.Precursor(p2, "EEK", 4, 248, "f.raw", 1, 2);   // a second charge state of EEK
        var model = new DocumentModel(null, new[] { p1, p2 }, Array.Empty<ResultFileInfo>());

        // Several proteins: all their peptides.
        Assert.Equal(new[] { a.Peptide, b.Peptide, c.Peptide, d.Peptide }, model.PeptidesOf(new HashSet<object> { p2, p1 }));
        // Peptides from different proteins, given out of order: document order.
        Assert.Equal(new[] { a.Peptide, d.Peptide }, model.PeptidesOf(new HashSet<object> { d.Peptide, a.Peptide }));
        // A precursor selects its peptide; a protein and one of its own peptides give that peptide once.
        Assert.Equal(new[] { a.Peptide, b.Peptide, c.Peptide }, model.PeptidesOf(new HashSet<object> { p1, a.Peptide, c }));
        // One of a peptide's two precursors is enough to select it.
        Assert.Equal(new[] { d.Peptide }, model.PeptidesOf(new HashSet<object> { d4 }));
        Assert.Empty(model.PeptidesOf(new HashSet<object>()));
    }

    [Fact]
    public void PeaksMatchPrecursorsByName()
    {
        var transitions = CsvTable.Read(new StringReader(
            "ProteinName,PeptideSequence,PeptideModifiedSequenceMonoisotopicMasses,PrecursorCharge,PrecursorMz,IsotopeLabelType,ProductMz\n"
            + "P1,ACK,AC[+57.021464]K,2,200,light,147\n"
            + "P1,ACK,AC[+57.021464]K,2,204,heavy,151\n"
            + "P2,DEK,,3,150,,147\n"));
        var model = DocumentModelBuilder.Build(null, transitions, null);
        Assert.Equal(new[] { "Precursor:/P1/AC[+57.021464]K/light++", "Precursor:/P1/AC[+57.021464]K/heavy++", "Precursor:/P2/DEK/light+++" },
            model.Precursors.Select(p => p.Locator));

        // The heavy precursor's peak lands on the heavy precursor; a missing modified sequence falls back
        // to the plain sequence and a missing label to "light"; an unknown precursor is skipped.
        var peaks = CsvTable.Read(new StringReader(
            "ProteinName,PeptideSequence,PeptideModifiedSequenceMonoisotopicMasses,PrecursorCharge,IsotopeLabelType,BestRetentionTime\n"
            + "P1,ACK,AC[+57.021464]K,2,heavy,7\n"
            + "P2,DEK,,3,,9\n"
            + "P3,XXK,XXK,2,light,11\n"));
        Assert.Equal(2, DocumentModelBuilder.ApplyPeaks(model, peaks, "a.raw"));
        Assert.Empty(model.Precursors[0].Peaks);
        Assert.Equal(7, model.Precursors[1].Peaks["a.raw"].Apex);
        Assert.Equal(9, model.Precursors[2].Peaks["a.raw"].Apex);

        // Any column order works, and a modified sequence alone, or a plain sequence alone, names the peptide.
        var seqFirst = CsvTable.Read(new StringReader("PeptideSequence,ProteinName,PrecursorCharge,BestRetentionTime\nDEK,P2,3,12\n"));
        Assert.Equal(1, DocumentModelBuilder.ApplyPeaks(model, seqFirst, "g.raw"));
        var reordered = CsvTable.Read(new StringReader("PrecursorCharge,PeptideSequence,ProteinName,BestRetentionTime\n3,DEK,P2,10\n"));
        Assert.Equal(1, DocumentModelBuilder.ApplyPeaks(model, reordered, "d.raw"));
        var modFirst = CsvTable.Read(new StringReader("PeptideModifiedSequenceMonoisotopicMasses,ProteinName,PrecursorCharge,IsotopeLabelType,BestRetentionTime\nAC[+57.021464]K,P1,2,light,8\n"));
        Assert.Equal(1, DocumentModelBuilder.ApplyPeaks(model, modFirst, "e.raw"));
        Assert.Equal(8, model.Precursors[0].Peaks["e.raw"].Apex);

        // Without a charge column a row names no precursor, and nothing is applied.
        var unnamed = CsvTable.Read(new StringReader("ProteinName,PeptideSequence,BestRetentionTime\nP2,DEK,9\n"));
        Assert.Equal(0, DocumentModelBuilder.ApplyPeaks(model, unnamed, "b.raw"));
        // Nor without any sequence column.
        var noSequence = CsvTable.Read(new StringReader("ProteinName,PrecursorCharge,BestRetentionTime\nP2,3,9\n"));
        Assert.Equal(0, DocumentModelBuilder.ApplyPeaks(model, noSequence, "c.raw"));

        // A peak report that names no precursors lists no result files either.
        var withFile = "ProteinName,PeptideSequence,PrecursorCharge,FileName,BestRetentionTime\nP2,DEK,3,f.raw,9\n";
        Assert.Equal(new[] { "f.raw" }, DocumentModelBuilder.Build(null, transitions, CsvTable.Read(new StringReader(withFile))).ResultFiles.Select(f => f.FileName));
        foreach (var unnamedReport in new[]
        {
            "ProteinName,PeptideSequence,FileName,BestRetentionTime\nP2,DEK,f.raw,9\n",       // no charge
            "PeptideSequence,PrecursorCharge,FileName,BestRetentionTime\nDEK,3,f.raw,9\n",     // no protein
            "ProteinName,PrecursorCharge,FileName,BestRetentionTime\nP2,3,f.raw,9\n",          // no sequence
        })
        {
            Assert.Empty(DocumentModelBuilder.Build(null, transitions, CsvTable.Read(new StringReader(unnamedReport))).ResultFiles);
        }
    }

    [Fact]
    public void ParquetCellsReadAsTheCsvExportWritesThem()
    {
        var path = Path.Combine(_dir, "report.parquet");
        ReportFiles.WriteParquet(path,
            new Field[]
            {
                new DataField<string>("ProteinName"), new DataField<int>("PrecursorCharge"), new DataField<double>("ProductMz"),
                new DataField<double?>("DetectionQValue"), new DataField<float>("F"), new DataField<long>("L"),
                new DataField<bool>("B"), new DataField<DateTime>("T"), new DataField<int?>("N"),
            },
            new Array[]
            {
                new[] { "sp|P1|A", null! }, new[] { 2, 3 }, new[] { 147.112804, 0.1 },
                new double?[] { 0.0123, null }, new[] { 1.5f, 2f }, new[] { 12345678901L, -1L },
                new[] { true, false }, new[] { new DateTime(2026, 2, 11, 21, 1, 0), new DateTime(2026, 2, 12) }, new int?[] { null, 7 },
            });
        Assert.True(SkylineDocumentLoader.IsParquet(path));
        var table = SkylineDocumentLoader.ReadReport(path);
        Assert.Equal(new[] { "ProteinName", "PrecursorCharge", "ProductMz", "DetectionQValue", "F", "L", "B", "T", "N" }, table.Header);
        Assert.Equal(new[] { "sp|P1|A", "2", "147.112804", "0.0123", "1.5", "12345678901", "True", "2026-02-11T21:01:00.0000000", "" }, table.Rows[0]);
        Assert.Equal(new[] { "", "3", "0.1", "", "2", "-1", "False", "2026-02-12T00:00:00.0000000", "7" }, table.Rows[1]);
        Assert.Equal(147.112804, CsvTable.GetDouble(table.Rows[0], table.Column("ProductMz")));

        // A column type Skyline does not write is refused rather than guessed at.
        var odd = Path.Combine(_dir, "odd.parquet");
        ReportFiles.WriteParquet(odd, new Field[] { new DataField<decimal>("Money") }, new Array[] { new[] { 1.5m } });
        var error = Assert.Throws<NotSupportedException>(() => ParquetReport.Read(odd));
        Assert.Contains("Money", error.Message);
    }

    [Fact]
    public void SkylinesOwnPathWinsWhenItExists()
    {
        // The file exists both where Skyline says and next to the document: Skyline's path is kept.
        var docDir = Directory.CreateDirectory(Path.Combine(_dir, "doc")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_dir, "data")).FullName;
        File.WriteAllText(Path.Combine(docDir, "run.raw"), "");
        File.WriteAllText(Path.Combine(elsewhere, "run.raw"), "");
        var resolved = SkylineDocumentLoader.ResolvePaths(
            new[] { new ResultFileInfo("run.raw", Path.Combine(elsewhere, "run.raw") + "|0", "rep") }, Path.Combine(docDir, "d.sky"));
        Assert.Equal(Path.Combine(elsewhere, "run.raw"), resolved.Single().FilePath);
        // A Bruker .d folder is a directory, and counts as existing too.
        var folder = Directory.CreateDirectory(Path.Combine(elsewhere, "run.d")).FullName;
        Directory.CreateDirectory(Path.Combine(docDir, "run.d"));
        Assert.Equal(folder, SkylineDocumentLoader.ResolvePaths(new[] { new ResultFileInfo("run.d", folder, "rep") }, Path.Combine(docDir, "d.sky")).Single().FilePath);
    }

    [Fact]
    public void AReportFromASkylineWithoutParquetIsReadAsCsv()
    {
        // Older Skylines pick the format from the extension and write CSV for one they do not know.
        var path = Path.Combine(_dir, "old.parquet");
        File.WriteAllText(path, "ProteinName,PrecursorCharge\nP1,2\n");
        Assert.False(SkylineDocumentLoader.IsParquet(path));
        Assert.Equal(new[] { "P1", "2" }, SkylineDocumentLoader.ReadReport(path).Rows.Single());
        File.WriteAllText(path, "PA");     // shorter than the magic number
        Assert.False(SkylineDocumentLoader.IsParquet(path));
        foreach (var almost in new[] { "PAR0", "XAR1", "PXR1", "PAX1" })   // every byte of "PAR1" counts
        {
            File.WriteAllText(path, almost + "...");
            Assert.False(SkylineDocumentLoader.IsParquet(path), almost);
        }
        Assert.Equal("", ParquetReport.Format(null));
    }

    [Fact]
    public void GlimpseQueuesItsOwnRequestsAndPollingSkipsWhileBusy()
    {
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        using var server = new FakeSkylineServer((method, _) =>
        {
            if (method == "GetDocumentPath")
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));   // a long report
            }
            return "x";
        });
        var skyline = SkylineConnection.FromArguments(new[] { server.RawName })!;
        Assert.False(skyline.IsBusy);
        var longCall = Task.Run(() => skyline.GetDocumentPath());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(skyline.IsBusy);

        // A poll does not wait behind it, and does not reach Skyline.
        Assert.False(skyline.TryCall(c => c.GetVersion(), out var skipped));
        Assert.Null(skipped);
        // A second real request waits its turn instead of failing against the busy pipe.
        var queued = Task.Run(() => skyline.GetVersion());
        Assert.False(queued.Wait(300));
        release.Set();
        Assert.Equal("x", longCall.Result);
        Assert.Equal("x", queued.Result);
        Assert.False(skyline.IsBusy);
        Assert.True(skyline.TryCall(c => c.GetVersion(), out var answered));
        Assert.Equal("x", answered);
        Assert.Equal(new[] { "GetDocumentPath", "GetVersion", "GetVersion" }, server.Methods);
    }

    [Fact]
    public void TheActiveReplicatesFileOpensFirst()
    {
        var a = Path.Combine(_dir, "a.raw");
        var b = Path.Combine(_dir, "b.raw");
        File.WriteAllText(a, "");
        File.WriteAllText(b, "");
        var files = new List<ResultFileInfo>
        {
            new("missing.raw", Path.Combine(_dir, "missing.raw"), "rep0"),
            new("a.raw", a, "repA"),
            new("b.raw", b, "repB"),
            new("none.raw", null, "repN"),
        };
        Assert.Equal("b.raw", SkylineDocumentLoader.FirstFileToOpen(files, "repB")!.FileName);
        Assert.Equal("a.raw", SkylineDocumentLoader.FirstFileToOpen(files, "rep0")!.FileName);   // not on disk: the first that is
        Assert.Equal("a.raw", SkylineDocumentLoader.FirstFileToOpen(files, null)!.FileName);
        Assert.Equal("a.raw", SkylineDocumentLoader.FirstFileToOpen(files, "repb")!.FileName);   // names are exact
        Assert.Null(SkylineDocumentLoader.FirstFileToOpen(files.Take(1).Concat(files.Skip(3)).ToList(), "repN"));
    }
}

public class PeakLabelTests
{
    [Fact]
    public void Ms1PeaksSayWhosePrecursorTheyAre()
    {
        var protein = new DocProtein("sp|O95487|SC24B_HUMAN", null, null, null);
        var peptide = new DocPeptide(protein, "VYAMCQIK", "VYAMC[+57.021464]QIK", null);
        var light = new DocPrecursor(peptide, "VYAMC[+57.021464]QIK", 2, 506.7514, "light", null);
        var heavy = new DocPrecursor(peptide, "VYAMC[+57.021464]QIK[+8.014199]", 3, 340.5, "heavy", null);

        Assert.Equal("VYAMC[+57]QIK 2+", PeakLabels.Precursor(light, "M"));
        Assert.Equal("VYAMC[+57]QIK 2+ M+1", PeakLabels.Precursor(light, "M+1"));
        Assert.Equal("VYAMC[+57]QIK 2+", PeakLabels.Precursor(light, ""));
        Assert.Equal("VYAMC[+57]QIK heavy 3+ M+2", PeakLabels.Precursor(heavy, "M+2"));

        var owner = new Glimpse.Core.Assignment.PrecursorAnnotation(light);
        var isotope = new PeakMatch(owner, new Glimpse.Core.Chemistry.TheoreticalIon(507.25, Glimpse.Core.Chemistry.IonType.Precursor, 1, 2, "M+1"), 1.0);
        var y7 = new PeakMatch(owner, new Glimpse.Core.Chemistry.TheoreticalIon(800.4, Glimpse.Core.Chemistry.IonType.Y, 7, 1, "y7"), 1.0);
        Assert.Equal("VYAMC[+57]QIK 2+ M+1", PeakLabels.For(isotope, msLevel: 1));
        Assert.Equal("M+1", PeakLabels.For(isotope, msLevel: 2));    // MS2 keeps the short names
        Assert.Equal("y7", PeakLabels.For(y7, msLevel: 2));
        Assert.Equal("y7", PeakLabels.For(y7, msLevel: 1));
    }

    [Fact]
    public void ModificationMassesAreRoundedToTheNearestDalton()
    {
        Assert.Equal("VYAMC[+57]QIK", PeakLabels.CompactSequence("VYAMC[+57.021464]QIK"));
        Assert.Equal("M[+16]PEPTIDEK", PeakLabels.CompactSequence("M[+15.994915]PEPTIDEK"));
        Assert.Equal("Q[-17]GK", PeakLabels.CompactSequence("Q[-17.026549]GK"));
        Assert.Equal("S[+80]K[+1]", PeakLabels.CompactSequence("S[+79.966331]K[+0.5]"));   // halves round away from zero
        Assert.Equal("S[+80]K", PeakLabels.CompactSequence("S[+80]K"));
        Assert.Equal("PEPTIDEK", PeakLabels.CompactSequence("PEPTIDEK"));
        Assert.Equal("C[Carbamidomethyl]K", PeakLabels.CompactSequence("C[Carbamidomethyl]K"));  // names left alone
    }
}
