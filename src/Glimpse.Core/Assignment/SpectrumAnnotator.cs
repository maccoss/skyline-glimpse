using Glimpse.Core.Chemistry;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Library;

namespace Glimpse.Core.Assignment;

/// <summary>Which fragment ions are labeled for each peptide in a spectrum (Skyline's spectrum graph offers the same choice).</summary>
public enum LabelSource
{
    /// <summary>Only the transitions the Skyline document uses (including neutral losses and custom ions).</summary>
    DocumentTransitions,

    /// <summary>Ions of the selected types and charges that are also peaks of the peptide's library spectrum.</summary>
    LibraryIons,

    /// <summary>Every ion of the selected types and charges.</summary>
    AllIons,
}

public sealed record LabelOptions
{
    public LabelSource Source { get; init; } = LabelSource.AllIons;

    public IReadOnlySet<IonType> Types { get; init; } = new HashSet<IonType> { IonType.B, IonType.Y };

    public IReadOnlySet<int> Charges { get; init; } = new HashSet<int> { 1, 2 };

    /// <summary>Also label the unfragmented precursor in MS2 spectra.</summary>
    public bool IncludePrecursor { get; init; } = true;

    /// <summary>When set, used instead of the document's tolerances for every match.</summary>
    public MzTolerance? ToleranceOverride { get; init; }

    public static LabelOptions Default { get; } = new();
}

/// <summary>One theoretical ion of one precursor matched to one peak.</summary>
public sealed record PeakMatch(PrecursorAnnotation Owner, TheoreticalIon Ion, double PpmError);

/// <summary>How one precursor explains a spectrum.</summary>
public sealed class PrecursorAnnotation
{
    internal PrecursorAnnotation(DocPrecursor precursor) => Precursor = precursor;

    public DocPrecursor Precursor { get; }

    /// <summary>Ions considered for this precursor.</summary>
    public int TheoreticalIons { get; internal set; }

    /// <summary>Ions that matched a peak.</summary>
    public int MatchedIons { get; internal set; }

    /// <summary>Summed intensity of the peaks this precursor matched (a shared peak counts for each owner).</summary>
    public double ExplainedIntensity { get; internal set; }

    /// <summary>Of <see cref="ExplainedIntensity"/>, the part in peaks no other precursor also matched.</summary>
    public double UniqueIntensity { get; internal set; }

    /// <summary>False when the modified sequence did not reproduce Skyline's precursor m/z, so only document transitions were used.</summary>
    public bool Reconciled { get; internal set; } = true;

    /// <summary>The library spectrum, when the document has a library entry for this precursor.</summary>
    public LibrarySpectrum? Library { get; internal set; }

    /// <summary>Normalized dot product of the matched peak intensities against the library, when available.</summary>
    public double? LibraryDotProduct { get; internal set; }

    /// <summary>Stable color slot: 0 for the precursor explaining the most signal, 1 for the next, ...</summary>
    public int ColorIndex { get; internal set; }

    public override string ToString() => Precursor.ToString();
}

/// <summary>The annotated spectrum: per-peak matches and per-precursor summaries.</summary>
public sealed class SpectrumAnnotation
{
    internal SpectrumAnnotation(SpectrumData spectrum, IReadOnlyList<PrecursorAnnotation> precursors, List<PeakMatch>?[] byPeak)
    {
        Spectrum = spectrum;
        Precursors = precursors;
        ByPeak = byPeak;
        TotalIntensity = spectrum.Intensity.Sum();
        var assigned = 0.0;
        for (var i = 0; i < byPeak.Length; i++)
        {
            if (byPeak[i] is { Count: > 0 })
                assigned += spectrum.Intensity[i];
        }
        AssignedIntensity = assigned;
    }

    public static SpectrumAnnotation Empty(SpectrumData spectrum) =>
        new(spectrum, Array.Empty<PrecursorAnnotation>(), new List<PeakMatch>?[spectrum.Mz.Length]);

    public SpectrumData Spectrum { get; }

    /// <summary>Precursors in the spectrum, most explained signal first.</summary>
    public IReadOnlyList<PrecursorAnnotation> Precursors { get; }

    /// <summary>Matches per peak (parallel to the spectrum's arrays); null for an unassigned peak.</summary>
    public IReadOnlyList<List<PeakMatch>?> ByPeak { get; }

    /// <summary>Intensity of every peak matched by at least one precursor, each peak counted once.</summary>
    public double AssignedIntensity { get; }

    public double TotalIntensity { get; }

    public double AssignedFraction => TotalIntensity > 0 ? AssignedIntensity / TotalIntensity : 0;

    public int AssignedPeaks => ByPeak.Count(m => m is { Count: > 0 });
}

/// <summary>
/// Labels a spectrum with the ions of every document precursor in it and works out which peaks belong
/// to which precursors.
/// </summary>
public static class SpectrumAnnotator
{
    public static SpectrumAnnotation Annotate(
        SpectrumData spectrum,
        IReadOnlyList<DocPrecursor> candidates,
        LabelOptions options,
        DocumentSettings settings,
        ISpectralLibrary? library = null)
    {
        var level = spectrum.Header.MsLevel;
        var byPeak = new List<PeakMatch>?[spectrum.Mz.Length];
        var annotations = new List<PrecursorAnnotation>(candidates.Count);
        foreach (var precursor in candidates)
        {
            var a = new PrecursorAnnotation(precursor);
            var ions = IonsFor(a, level, options, settings, library);
            a.TheoreticalIons = ions.Count;
            var tolerance = options.ToleranceOverride
                ?? (level == 1 ? settings.PrecursorTolerance
                    : options.Source == LabelSource.DocumentTransitions ? settings.ProductTolerance
                    : settings.IonMatchTolerance);
            var matchedPeaks = new HashSet<int>();
            foreach (var ion in ions)
            {
                var (lo, hi) = tolerance.WindowAt(ion.Mz);
                var peak = spectrum.MostIntenseIn(lo, hi);
                if (peak < 0)
                    continue;
                a.MatchedIons++;
                var ppm = (spectrum.Mz[peak] - ion.Mz) / ion.Mz * 1e6;
                (byPeak[peak] ??= new List<PeakMatch>()).Add(new PeakMatch(a, ion, ppm));
                matchedPeaks.Add(peak);
            }
            a.ExplainedIntensity = matchedPeaks.Sum(p => spectrum.Intensity[p]);
            if (a.Library is { Mz.Length: > 0 } lib && level >= 2)
                a.LibraryDotProduct = DotProduct(spectrum, lib, settings.IonMatchTolerance);
            annotations.Add(a);
        }

        // Unique signal: peaks only one precursor claims.
        for (var i = 0; i < byPeak.Length; i++)
        {
            if (byPeak[i] is not { Count: > 0 } matches)
                continue;
            var owners = matches.Select(m => m.Owner).Distinct().ToList();
            if (owners.Count == 1)
                owners[0].UniqueIntensity += spectrum.Intensity[i];
        }

        var ordered = annotations.Where(a => a.MatchedIons > 0)
            .OrderByDescending(a => a.ExplainedIntensity)
            .Concat(annotations.Where(a => a.MatchedIons == 0))
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].ColorIndex = i;
        return new SpectrumAnnotation(spectrum, ordered, byPeak);
    }

    /// <summary>The ions to look for, for one precursor in a spectrum of the given MS level.</summary>
    internal static List<TheoreticalIon> IonsFor(
        PrecursorAnnotation a, int msLevel, LabelOptions options, DocumentSettings settings, ISpectralLibrary? library)
    {
        var precursor = a.Precursor;
        a.Library = library?.Find(precursor.Peptide.ModifiedSequence, precursor.Charge);

        if (msLevel <= 1)
        {
            var isotopes = precursor.Transitions.Where(t => t.IsPrecursor).Select(ToIon).ToList();
            return isotopes.Count > 0 ? isotopes : Fragments.PrecursorIsotopes(precursor.Mz, precursor.Charge);
        }

        var documentIons = precursor.Transitions.Where(t => !t.IsPrecursor).Select(ToIon).ToList();
        if (options.Source == LabelSource.DocumentTransitions)
            return documentIons;

        var peptide = ModifiedSequence.TryParse(precursor.ModifiedSequence);
        if (peptide is null || Math.Abs(peptide.PrecursorMz(precursor.Charge) - precursor.Mz) / precursor.Mz * 1e6 > 10)
        {
            // The sequence cannot be turned into masses that agree with Skyline (an unrecognized
            // modification, or a label written as a name): fall back to what Skyline itself extracts.
            a.Reconciled = false;
            return documentIons;
        }

        var ions = Fragments.Enumerate(peptide, options.Types.ToArray(), options.Charges.ToArray(), precursor.Charge);
        if (options.Source == LabelSource.LibraryIons)
        {
            if (a.Library is not { Mz.Length: > 0 } lib)
                return documentIons;
            var tol = options.ToleranceOverride ?? settings.IonMatchTolerance;
            ions = ions.Where(ion =>
            {
                var (lo, hi) = tol.WindowAt(ion.Mz);
                var i = SpectrumData.LowerBound(lib.Mz, lo);
                return i < lib.Mz.Length && lib.Mz[i] <= hi;
            }).ToList();
        }
        if (options.IncludePrecursor)
            ions.Add(new TheoreticalIon(precursor.Mz, IonType.Precursor, 0, precursor.Charge, "p" + new string('+', precursor.Charge)));
        return ions;
    }

    private static TheoreticalIon ToIon(DocTransition t)
    {
        var (type, ordinal, label) = Fragments.ParseSkylineFragment(t.FragmentIon, t.ProductCharge);
        return new TheoreticalIon(t.ProductMz, type, ordinal, t.ProductCharge, label);
    }

    /// <summary>
    /// Normalized dot product (sqrt intensities, as Skyline's dotp) between the library peaks and the
    /// most intense measured peak within tolerance of each.
    /// </summary>
    private static double? DotProduct(SpectrumData spectrum, LibrarySpectrum lib, MzTolerance tolerance)
    {
        double dot = 0, a2 = 0, b2 = 0;
        for (var i = 0; i < lib.Mz.Length; i++)
        {
            var (lo, hi) = tolerance.WindowAt(lib.Mz[i]);
            var peak = spectrum.MostIntenseIn(lo, hi);
            var measured = peak >= 0 ? Math.Sqrt(spectrum.Intensity[peak]) : 0;
            var expected = Math.Sqrt(lib.Intensity[i]);
            dot += measured * expected;
            a2 += measured * measured;
            b2 += expected * expected;
        }
        return a2 > 0 && b2 > 0 ? dot / Math.Sqrt(a2 * b2) : null;
    }
}
