namespace Glimpse.Core.Document;

/// <summary>
/// Builds a <see cref="DocumentModel"/> from two Skyline report exports, using Skyline's invariant
/// column names (<see cref="TransitionColumns"/>, <see cref="PeakColumns"/>).
/// </summary>
/// <remarks>
/// Two reports rather than one: transitions do not vary by replicate, and peak boundaries are per
/// precursor, so one transition-by-replicate report would be (transitions x files) rows - 620,000
/// transitions x 17 files is over ten million - for no information the two smaller ones lack.
/// </remarks>
public static class DocumentModelBuilder
{
    /// <summary>
    /// The transition report: one row per transition, no results. No locator columns: Skyline computes
    /// those slowly (see <see cref="SkylineLocators"/>), and they are built from these names instead.
    /// </summary>
    public static readonly string[] TransitionColumns =
    {
        "ProteinName", "ProteinAccession", "ProteinGene",
        "PeptideSequence", "PeptideModifiedSequenceMonoisotopicMasses",
        "ModifiedSequenceMonoisotopicMasses", "PrecursorCharge", "PrecursorMz", "IsotopeLabelType",
        "FragmentIon", "ProductCharge", "ProductMz",
    };

    /// <summary>
    /// The peak report: one row per precursor per replicate, the precursor named by the columns its
    /// locator is built from. No FileName or FilePath: asking for a result-file column makes Skyline build
    /// the report at transition level, with the precursor peak columns pivoted across every replicate -
    /// over 250 times the size, measured on a 17 replicate document. Rows are tied to their file through
    /// the replicate name instead.
    /// </summary>
    public static readonly string[] PeakColumns =
    {
        "ProteinName", "PeptideModifiedSequenceMonoisotopicMasses", "PrecursorCharge", "IsotopeLabelType",
        "ReplicateName",
        "BestRetentionTime", "MinStartTime", "MaxEndTime",
        "DetectionQValue", "LibraryDotProduct", "IsotopeDotProduct",
    };

    public static DocumentModel Build(string? documentPath, CsvTable transitions, CsvTable? peaks)
    {
        var t = transitions;
        int cProt = t.Column("ProteinName", "Protein Name", "Protein"),
            cAcc = t.Column("ProteinAccession", "Protein Accession"),
            cGene = t.Column("ProteinGene", "Protein Gene"),
            cProtLoc = t.Column("ProteinLocator", "Protein Locator"),
            cSeq = t.Column("PeptideSequence", "Peptide Sequence"),
            cPepMod = t.Column("PeptideModifiedSequenceMonoisotopicMasses", "Peptide Modified Sequence Monoisotopic Masses"),
            cPepLoc = t.Column("PeptideLocator", "Peptide Locator"),
            cPrecMod = t.Column("ModifiedSequenceMonoisotopicMasses", "Modified Sequence Monoisotopic Masses"),
            cZ = t.Column("PrecursorCharge", "Precursor Charge"),
            cMz = t.Column("PrecursorMz", "Precursor Mz"),
            cLabel = t.Column("IsotopeLabelType", "Isotope Label Type"),
            cPrecLoc = t.Column("PrecursorLocator", "Precursor Locator"),
            cIon = t.Column("FragmentIon", "Fragment Ion"),
            cIonZ = t.Column("ProductCharge", "Product Charge"),
            cIonMz = t.Column("ProductMz", "Product Mz");
        if (cProt < 0 || cSeq < 0 || cZ < 0 || cMz < 0 || cIonMz < 0)
            throw new InvalidDataException("The transition report is missing required columns: " + string.Join(", ", t.Header));

        var proteins = new List<DocProtein>();
        var proteinByKey = new Dictionary<string, DocProtein>(StringComparer.Ordinal);
        var peptideByKey = new Dictionary<string, DocPeptide>(StringComparer.Ordinal);
        var precursorByKey = new Dictionary<string, DocPrecursor>(StringComparer.Ordinal);

        foreach (var row in t.Rows)
        {
            var protName = CsvTable.Get(row, cProt);
            var protLoc = NullIfEmpty(CsvTable.Get(row, cProtLoc)) ?? SkylineLocators.Protein(protName);
            // Every element is keyed by its locator: Skyline's when the report has one, else built the same way.
            var protKey = protLoc;
            if (!proteinByKey.TryGetValue(protKey, out var protein))
            {
                protein = new DocProtein(protName, NullIfEmpty(CsvTable.Get(row, cAcc)), NullIfEmpty(CsvTable.Get(row, cGene)), protLoc);
                proteinByKey.Add(protKey, protein);
                proteins.Add(protein);
            }

            var seq = CsvTable.Get(row, cSeq);
            var pepMod = NullIfEmpty(CsvTable.Get(row, cPepMod)) ?? seq;
            var pepLoc = NullIfEmpty(CsvTable.Get(row, cPepLoc)) ?? SkylineLocators.Peptide(protName, pepMod);
            var pepKey = pepLoc;
            if (!peptideByKey.TryGetValue(pepKey, out var peptide))
            {
                peptide = new DocPeptide(protein, seq, pepMod, pepLoc);
                peptideByKey.Add(pepKey, peptide);
                protein.Peptides.Add(peptide);
            }

            var precMod = NullIfEmpty(CsvTable.Get(row, cPrecMod)) ?? pepMod;
            var z = CsvTable.GetInt(row, cZ);
            var label = NullIfEmpty(CsvTable.Get(row, cLabel)) ?? "light";
            var precLoc = NullIfEmpty(CsvTable.Get(row, cPrecLoc)) ?? SkylineLocators.Precursor(protName, pepMod, label, z);
            var precKey = precLoc;
            if (!precursorByKey.TryGetValue(precKey, out var precursor))
            {
                precursor = new DocPrecursor(peptide, precMod, z, CsvTable.GetDouble(row, cMz), label, precLoc);
                precursorByKey.Add(precKey, precursor);
                peptide.Precursors.Add(precursor);
            }

            var productMz = CsvTable.GetDouble(row, cIonMz);
            if (double.IsFinite(productMz))
                precursor.Transitions.Add(new DocTransition(CsvTable.Get(row, cIon), CsvTable.GetInt(row, cIonZ), productMz));
        }

        var files = new List<ResultFileInfo>();
        if (peaks is not null)
            ReadPeaks(peaks, precursorByKey, files);

        return new DocumentModel(documentPath, proteins, files);
    }

    /// <returns>
    /// With <paramref name="fileName"/>, the number of precursors left without a peak because the report
    /// gave them differing peaks (a replicate of several files; see <see cref="ApplyPeaks(DocumentModel, CsvTable, string?, out int)"/>).
    /// </returns>
    private static int ReadPeaks(CsvTable p, Dictionary<string, DocPrecursor> precursorByKey, List<ResultFileInfo> files, string? fileName = null)
    {
        int cLoc = p.Column("PrecursorLocator", "Precursor Locator"),
            cProt = p.Column("ProteinName", "Protein Name", "Protein"),
            cSeq = p.Column("PeptideSequence", "Peptide Sequence"),
            cPepMod = p.Column("PeptideModifiedSequenceMonoisotopicMasses", "Peptide Modified Sequence Monoisotopic Masses"),
            cZ = p.Column("PrecursorCharge", "Precursor Charge"),
            cLabel = p.Column("IsotopeLabelType", "Isotope Label Type"),
            cFile = p.Column("FileName", "File Name"),
            cPath = p.Column("FilePath", "File Path"),
            cRep = p.Column("ReplicateName", "Replicate Name", "Replicate"),
            cApex = p.Column("BestRetentionTime", "Best Retention Time"),
            cStart = p.Column("MinStartTime", "Min Start Time"),
            cEnd = p.Column("MaxEndTime", "Max End Time"),
            cQ = p.Column("DetectionQValue", "Detection Q Value"),
            cDotp = p.Column("LibraryDotProduct", "Library Dot Product"),
            cIdotp = p.Column("IsotopeDotProduct", "Isotope Dot Product");
        // A precursor is named by its locator, or by the columns the locator is built from.
        var named = cLoc >= 0 || (cProt >= 0 && (cPepMod >= 0 || cSeq >= 0) && cZ >= 0);
        if (!named || (cFile < 0 && fileName is null))
            return 0;

        var seenFiles = new Dictionary<string, ResultFileInfo>(StringComparer.OrdinalIgnoreCase);
        // Rows taken to belong to fileName: a precursor given two different peaks is ambiguous.
        var taken = fileName is null ? null : new Dictionary<DocPrecursor, PeakBounds?>();
        foreach (var row in p.Rows)
        {
            var file = fileName ?? CsvTable.Get(row, cFile);
            if (file.Length == 0)
                continue;
            if (!seenFiles.ContainsKey(file))
            {
                var info = new ResultFileInfo(file, NullIfEmpty(CsvTable.Get(row, cPath)), NullIfEmpty(CsvTable.Get(row, cRep)));
                seenFiles.Add(file, info);
                files.Add(info);
            }
            var locator = cLoc >= 0
                ? CsvTable.Get(row, cLoc)
                : SkylineLocators.Precursor(CsvTable.Get(row, cProt),
                    NullIfEmpty(CsvTable.Get(row, cPepMod)) ?? CsvTable.Get(row, cSeq),
                    NullIfEmpty(CsvTable.Get(row, cLabel)) ?? "light", CsvTable.GetInt(row, cZ));
            if (!precursorByKey.TryGetValue(locator, out var precursor))
                continue;
            var bounds = new PeakBounds(
                CsvTable.GetDouble(row, cApex), CsvTable.GetDouble(row, cStart), CsvTable.GetDouble(row, cEnd),
                CsvTable.GetNullableDouble(row, cQ), CsvTable.GetNullableDouble(row, cDotp), CsvTable.GetNullableDouble(row, cIdotp));
            var usable = bounds.HasBoundaries || double.IsFinite(bounds.Apex);
            if (taken is not null)
            {
                if (taken.TryGetValue(precursor, out var earlier))
                {
                    // A second row for the same precursor: the replicate has more than one file, and the
                    // report cannot say which row is this file's. Leave the precursor without a peak
                    // rather than keep whichever row came last. Identical rows are harmless.
                    if (earlier != bounds)
                    {
                        taken[precursor] = null;
                        precursor.Peaks.TryRemove(file, out _);
                    }
                    continue;
                }
                taken[precursor] = bounds;
            }
            if (usable)
                precursor.Peaks[file] = bounds;
        }
        return taken?.Count(kv => kv.Value is null) ?? 0;
    }

    /// <summary>
    /// Add the peaks of a peak report to an existing model. Precursors are matched by locator. With
    /// <paramref name="fileName"/>, every row is taken to belong to that file (a report filtered to one
    /// replicate carries no file column). Returns the number of peaks added.
    /// </summary>
    public static int ApplyPeaks(DocumentModel model, CsvTable peaks, string? fileName = null) =>
        ApplyPeaks(model, peaks, fileName, out _);

    /// <summary>
    /// <see cref="ApplyPeaks(DocumentModel, CsvTable, string?)"/>, also counting the precursors left
    /// without a peak in <paramref name="fileName"/> because the report gave them more than one. That
    /// happens when the replicate holds several files: a report filtered to the replicate has a row per
    /// file and no column saying which file each row is.
    /// </summary>
    public static int ApplyPeaks(DocumentModel model, CsvTable peaks, string? fileName, out int ambiguous)
    {
        var byLocator = new Dictionary<string, DocPrecursor>(StringComparer.Ordinal);
        foreach (var p in model.Precursors)
        {
            if (p.Locator is not null)
                byLocator.TryAdd(p.Locator, p);
        }
        var before = model.Precursors.Sum(p => p.Peaks.Count);
        ambiguous = ReadPeaks(peaks, byLocator, new List<ResultFileInfo>(), fileName);
        return model.Precursors.Sum(p => p.Peaks.Count) - before;
    }

    /// <summary>The replicate-level report columns that list the document's result files.</summary>
    public static readonly string[] ResultFileColumns = { "ReplicateName", "FileName", "FilePath" };

    /// <summary>Result files from the replicate-level report, in report order, one per file name.</summary>
    public static List<ResultFileInfo> ReadResultFiles(CsvTable table)
    {
        int cRep = table.Column("ReplicateName", "Replicate Name", "Replicate"),
            cFile = table.Column("FileName", "File Name"),
            cPath = table.Column("FilePath", "File Path");
        var files = new List<ResultFileInfo>();
        if (cFile < 0)
            return files;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            var file = CsvTable.Get(row, cFile);
            if (file.Length > 0 && seen.Add(file))
                files.Add(new ResultFileInfo(file, NullIfEmpty(CsvTable.Get(row, cPath)), NullIfEmpty(CsvTable.Get(row, cRep))));
        }
        return files;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
