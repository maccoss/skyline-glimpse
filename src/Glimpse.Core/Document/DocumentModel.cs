namespace Glimpse.Core.Document;

/// <summary>
/// The parts of a Skyline document Glimpse needs: proteins, peptides, precursors and their transitions,
/// and each precursor's integrated peak in each result file.
/// </summary>
public sealed class DocumentModel
{
    public DocumentModel(string? documentPath, IReadOnlyList<DocProtein> proteins, IReadOnlyList<ResultFileInfo> resultFiles)
    {
        DocumentPath = documentPath;
        Proteins = proteins;
        ResultFiles = resultFiles;
        Peptides = proteins.SelectMany(p => p.Peptides).ToArray();
        Precursors = Peptides.SelectMany(p => p.Precursors).ToArray();
    }

    public static DocumentModel Empty { get; } = new(null, Array.Empty<DocProtein>(), Array.Empty<ResultFileInfo>());

    public string? DocumentPath { get; }

    public IReadOnlyList<DocProtein> Proteins { get; }

    public IReadOnlyList<DocPeptide> Peptides { get; }

    public IReadOnlyList<DocPrecursor> Precursors { get; }

    public IReadOnlyList<ResultFileInfo> ResultFiles { get; }

    public int TransitionCount => Precursors.Sum(p => p.Transitions.Count);

    /// <summary>The same document with a different list of result files.</summary>
    public DocumentModel WithResultFiles(IReadOnlyList<ResultFileInfo> files) => new(DocumentPath, Proteins, files);

    /// <summary>True when any precursor has a peak for this file (its peak report has been applied).</summary>
    public bool HasPeaksFor(string fileName) => Precursors.Any(p => p.Peaks.ContainsKey(fileName));

    /// <summary>
    /// The peptides a Skyline selection locator refers to: a protein selects all its peptides, a
    /// peptide itself, a precursor or transition its peptide.
    /// </summary>
    public IEnumerable<DocPeptide> PeptidesForLocator(string locator)
    {
        var path = LocatorPath(locator);
        if (path.Length == 0)
            yield break;
        foreach (var peptide in Peptides)
        {
            var own = LocatorPath(peptide.Locator);
            if (own.Length == 0)
                continue;
            if (own == path
                || own.StartsWith(path + "/", StringComparison.Ordinal)
                || path.StartsWith(own + "/", StringComparison.Ordinal))
            {
                yield return peptide;
            }
        }
    }

    /// <summary>The path part of a locator ("Molecule:/protein/peptide" gives "/protein/peptide").</summary>
    internal static string LocatorPath(string? locator)
    {
        if (string.IsNullOrEmpty(locator))
            return "";
        var colon = locator.IndexOf(":/", StringComparison.Ordinal);
        var path = colon >= 0 ? locator[(colon + 1)..] : locator;
        // Drop query-style qualifiers (e.g. "?ResultFile=...") so result locators match their node.
        var q = path.IndexOf('?');
        return q >= 0 ? path[..q] : path;
    }
}

/// <summary>A protein (or peptide list) in document order.</summary>
public sealed class DocProtein
{
    public DocProtein(string name, string? accession, string? gene, string? locator)
    {
        Name = name;
        Accession = accession;
        Gene = gene;
        Locator = locator;
    }

    public string Name { get; }

    public string? Accession { get; }

    public string? Gene { get; }

    public string? Locator { get; }

    public List<DocPeptide> Peptides { get; } = new();

    public override string ToString() => Name;
}

public sealed class DocPeptide
{
    public DocPeptide(DocProtein protein, string sequence, string modifiedSequence, string? locator)
    {
        Protein = protein;
        Sequence = sequence;
        ModifiedSequence = modifiedSequence;
        Locator = locator;
    }

    public DocProtein Protein { get; }

    /// <summary>Unmodified sequence.</summary>
    public string Sequence { get; }

    /// <summary>Modified sequence with monoisotopic mass deltas, e.g. PEPC[+57.021464]TIDE.</summary>
    public string ModifiedSequence { get; }

    public string? Locator { get; }

    public List<DocPrecursor> Precursors { get; } = new();

    public override string ToString() => ModifiedSequence;
}

public sealed class DocPrecursor
{
    public DocPrecursor(DocPeptide peptide, string modifiedSequence, int charge, double mz, string labelType, string? locator)
    {
        Peptide = peptide;
        ModifiedSequence = modifiedSequence;
        Charge = charge;
        Mz = mz;
        LabelType = labelType;
        Locator = locator;
    }

    public DocPeptide Peptide { get; }

    /// <summary>Precursor-level modified sequence (includes isotope label modifications), monoisotopic masses.</summary>
    public string ModifiedSequence { get; }

    public int Charge { get; }

    public double Mz { get; }

    /// <summary>"light", "heavy", ...</summary>
    public string LabelType { get; }

    public string? Locator { get; }

    public List<DocTransition> Transitions { get; } = new();

    /// <summary>
    /// Integrated peak per result file, keyed by file name (case-insensitive). Concurrent: a file's peaks
    /// arrive in the background while spectra of other files may be annotated on other threads.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, PeakBounds> Peaks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasPrecursorTransitions => Transitions.Any(t => t.IsPrecursor);

    public override string ToString() => $"{ModifiedSequence}{new string('+', Math.Max(0, Charge))}" + (LabelType == "light" ? "" : $" ({LabelType})");
}

public sealed record DocTransition(string FragmentIon, int ProductCharge, double ProductMz)
{
    /// <summary>True for MS1 precursor isotope transitions ("precursor", "precursor [M+1]", ...).</summary>
    public bool IsPrecursor => FragmentIon.StartsWith("precursor", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Skyline's integrated peak for one precursor in one file. Times in minutes; NaN when absent.</summary>
public sealed record PeakBounds(double Apex, double Start, double End, double? DetectionQValue, double? LibraryDotProduct, double? IsotopeDotProduct)
{
    public bool HasBoundaries => double.IsFinite(Start) && double.IsFinite(End) && End >= Start;

    public bool Contains(double rt, double extendMinutes = 0) =>
        HasBoundaries && rt >= Start - extendMinutes && rt <= End + extendMinutes;
}

/// <summary>A replicate result file: its name as Skyline reports it, and where it is on disk when known.</summary>
public sealed record ResultFileInfo(string FileName, string? FilePath, string? ReplicateName);
