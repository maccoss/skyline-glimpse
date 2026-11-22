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
        _locatorIndex = new Lazy<LocatorIndex>(BuildLocatorIndex);
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
    /// The peptides of a set of selected proteins, peptides and precursors, each once and in document
    /// order: a protein gives all its peptides, a peptide itself, a precursor its peptide.
    /// </summary>
    public List<DocPeptide> PeptidesOf(IReadOnlySet<object> selected)
    {
        var result = new List<DocPeptide>();
        foreach (var peptide in Peptides)
        {
            if (selected.Contains(peptide) || selected.Contains(peptide.Protein) || peptide.Precursors.Any(selected.Contains))
                result.Add(peptide);
        }
        return result;
    }

    /// <summary>
    /// The peptides a Skyline selection locator refers to, in document order: a protein selects all its
    /// peptides, a peptide itself, a precursor or transition its peptide.
    /// </summary>
    /// <remarks>
    /// A lookup, not a scan: selecting every protein in Skyline sends thousands of locators, and comparing
    /// each with all 69,000 peptides of a large document hung the window.
    /// </remarks>
    public IEnumerable<DocPeptide> PeptidesForLocator(string locator)
    {
        var path = LocatorPath(locator);
        if (path.Length == 0)
            return Array.Empty<DocPeptide>();
        var index = _locatorIndex.Value;
        var found = new List<int>();
        // The peptide itself, and the peptides beneath it (path is a protein's).
        if (index.ByPath.TryGetValue(path, out var own))
            found.AddRange(own);
        if (index.ByAncestor.TryGetValue(path, out var below))
            found.AddRange(below);
        // The peptide above it (path is a precursor's or a transition's).
        for (var cut = path.IndexOf('/', 1); cut > 0; cut = path.IndexOf('/', cut + 1))
        {
            if (index.ByPath.TryGetValue(path[..cut], out var above))
                found.AddRange(above);
        }
        return found.Distinct().Order().Select(i => Peptides[i]).ToList();
    }

    /// <summary>
    /// The tree element a Skyline selection locator names: its protein, peptide or precursor, or for a
    /// transition (or anything else beneath a precursor) the nearest element above it. Null when the
    /// document has no such element.
    /// </summary>
    public object? ElementForLocator(string locator)
    {
        var path = LocatorPath(locator);
        if (path.Length == 0)
            return null;
        var index = _locatorIndex.Value;
        // The longest prefix of the path (the whole path first) that names an element.
        for (var end = path.Length; end > 0; end = path.LastIndexOf('/', end - 1))
        {
            var prefix = path[..end];
            if (index.Elements.TryGetValue(prefix, out var element))
                return element;
        }
        return null;
    }

    private readonly Lazy<LocatorIndex> _locatorIndex;

    /// <summary>
    /// Peptide positions by their locator path and by every path that path starts with; and every protein,
    /// peptide and precursor by its own path.
    /// </summary>
    private sealed record LocatorIndex(Dictionary<string, List<int>> ByPath, Dictionary<string, List<int>> ByAncestor, Dictionary<string, object> Elements);

    private LocatorIndex BuildLocatorIndex()
    {
        var byPath = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var byAncestor = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        static void Add(Dictionary<string, List<int>> map, string key, int value)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<int>();
            list.Add(value);
        }
        for (var i = 0; i < Peptides.Count; i++)
        {
            var own = LocatorPath(Peptides[i].Locator);
            if (own.Length == 0)
                continue;
            Add(byPath, own, i);
            for (var cut = own.IndexOf('/', 1); cut > 0; cut = own.IndexOf('/', cut + 1))
                Add(byAncestor, own[..cut], i);
        }
        var elements = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var protein in Proteins)
            AddElement(elements, protein.Locator, protein);
        foreach (var peptide in Peptides)
            AddElement(elements, peptide.Locator, peptide);
        foreach (var precursor in Precursors)
            AddElement(elements, precursor.Locator, precursor);
        return new LocatorIndex(byPath, byAncestor, elements);
    }

    private static void AddElement(Dictionary<string, object> elements, string? locator, object element)
    {
        var path = LocatorPath(locator);
        if (path.Length > 0)
            elements.TryAdd(path, element);
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
