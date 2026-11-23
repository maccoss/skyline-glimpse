using System.Collections.Concurrent;
using Glimpse.Features;
using Pwiz.Util.Chemistry;

namespace Glimpse.Pwiz;

/// <summary>
/// Isotope distributions of peptide compositions: abundances from pwiz's isotope tables (<see cref="IsotopeTable"/>),
/// summed into one peak per nominal mass, and the peaks spaced evenly by <see cref="IsotopeSpacing"/> from the
/// monoisotopic mass. Thread-safe; distributions are cached per formula.
/// </summary>
/// <remarks>
/// <para>Element masses and abundances are pwiz's, except carbon: 13C at 1.084% (12C 98.916%), the value
/// Dwight Matthews measured, as used by IDCalc, Hardklor and Skyline. It is closer to the 13C abundance of
/// proteins than the IUPAC/NIST 1.07% in pwiz's element table, which describes terrestrial carbon in general.</para>
/// <para>Spacing: Monocle's averagine isotope spacing (Rad, Li, Mintseris, O'Connell, Gygi and Schweppe,
/// J. Proteome Res. 2021, 20, 591; github.com/gygilab/Monocle, Mass.AVERAGINE_DIFF). Measured against observed
/// isotope peaks it fits as well as or better than each cluster's abundance-weighted mean mass (which drifts
/// short at M+2 and beyond, where 34S pulls the mean down) and Hardklor's Mercury spacing (about 1.0021 Da):
/// within 0.5 ppm out to M+4 on LTQ-FT data, within about 1 ppm on Orbitrap Astral Zoom MS1.</para>
/// </remarks>
public sealed class PwizEnvelopeModel : IEnvelopeModel
{
    /// <summary>13C abundance, from Dwight Matthews (as in IDCalc and Hardklor).</summary>
    public const double Carbon13Abundance = 0.01084;

    /// <summary>Mass between successive isotope peaks of a peptide (Da), from Monocle (Schweppe and colleagues).</summary>
    public const double IsotopeSpacing = 1.00286864;

    // Fine-structure terms below this probability are dropped. The library keeps peaks above 1% of the most
    // abundant, which for a 35 kDa molecule is still above 1e-4, so the terms dropped here do not matter.
    private const double ProbabilityCutoff = 1e-9;

    // The largest atom counts a table holds (pwiz's IsotopeCalculator budget; a 35 kDa averagine has ~1,550 C).
    private static readonly (ElementType Element, int MaxAtoms)[] Budgets =
    {
        (ElementType.C, 5000), (ElementType.H, 8000), (ElementType.N, 1500), (ElementType.O, 1500), (ElementType.S, 50),
    };

    private static readonly Lazy<Dictionary<ElementType, IsotopeTable>> Tables = new(() =>
        Budgets.ToDictionary(b => b.Element, b => new IsotopeTable(Isotopes(b.Element), b.MaxAtoms, ProbabilityCutoff)));

    private readonly ConcurrentDictionary<(ElementCounts Formula, double Cutoff), IsotopePeak[]> _cache = new();

    /// <summary>The isotopes of <paramref name="element"/>: pwiz's, with carbon's abundances set to Matthews'.</summary>
    internal static IReadOnlyList<MassAbundance> Isotopes(ElementType element)
    {
        var isotopes = ElementInfo.Record(element).Isotopes;
        if (element != ElementType.C)
            return isotopes;
        return isotopes.Select(i => Math.Round(i.Mass) switch
        {
            12 => new MassAbundance(i.Mass, 1 - Carbon13Abundance),
            13 => new MassAbundance(i.Mass, Carbon13Abundance),
            _ => new MassAbundance(i.Mass, 0),
        }).ToArray();
    }

    public IReadOnlyList<IsotopePeak> Distribution(ElementCounts formula, double abundanceCutoff) =>
        _cache.GetOrAdd((formula, abundanceCutoff), key => Compute(key.Formula, key.Cutoff));

    private static IsotopePeak[] Compute(ElementCounts counts, double abundanceCutoff)
    {
        var parts = new[] { (ElementType.C, counts.C), (ElementType.H, counts.H), (ElementType.N, counts.N), (ElementType.O, counts.O), (ElementType.S, counts.S) };
        if (parts.All(p => p.Item2 <= 0))
            return Array.Empty<IsotopePeak>();

        // Each element's distribution summed by nominal shift, then combined; the monoisotopic mass is the
        // sum of each element's lightest isotope.
        var abundance = new[] { 1.0 };
        var mono = 0.0;
        foreach (var (element, count) in parts)
        {
            if (count <= 0)
                continue;
            var distribution = Tables.Value[element].Distribution(count);
            var lightest = Isotopes(element).Where(i => i.Abundance > 0).Min(i => i.Mass);
            mono += count * lightest;
            var a = new double[(int)Math.Round(distribution.Max(p => p.Mass) - count * lightest) + 1];
            foreach (var p in distribution)
                a[(int)Math.Round(p.Mass - count * lightest)] += p.Abundance;
            abundance = Combine(abundance, a);
        }

        var max = abundance.Max();
        var result = new List<IsotopePeak>(abundance.Length);
        for (var k = 0; k < abundance.Length; k++)
        {
            var relative = abundance[k] / max;
            if (abundance[k] <= 0 || relative < abundanceCutoff)
                continue;
            result.Add(new IsotopePeak(mono + k * IsotopeSpacing, relative));
        }
        return result.ToArray();
    }

    /// <summary>The nominal-shift abundances of two independent parts (a convolution).</summary>
    internal static double[] Combine(double[] a1, double[] a2)
    {
        var a = new double[a1.Length + a2.Length - 1];
        for (var i = 0; i < a1.Length; i++)
        {
            for (var j = 0; j < a2.Length; j++)
            {
                var p = a1[i] * a2[j];
                if (p >= ProbabilityCutoff)
                    a[i + j] += p;
            }
        }
        return a;
    }
}
