using System.Collections.Concurrent;
using Glimpse.Features;
using Pwiz.Util.Chemistry;

namespace Glimpse.Pwiz;

/// <summary>
/// Isotope distributions of peptide compositions, from pwiz's isotope tables (<see cref="IsotopeTable"/>),
/// summed into one peak per nominal mass: the isotopic fine structure a mass analyzer does not resolve becomes
/// a single peak at its abundance-weighted mean mass. Thread-safe; distributions are cached per formula.
/// </summary>
/// <remarks>
/// Element masses and abundances are pwiz's, except carbon: 13C at 1.084% (12C 98.916%), the value
/// Dwight Matthews measured, as used by IDCalc, Hardklor and Skyline. It is closer to the 13C abundance of
/// proteins than the IUPAC/NIST 1.07% in pwiz's element table, which describes terrestrial carbon in general.
/// </remarks>
public sealed class PwizEnvelopeModel : IEnvelopeModel
{
    /// <summary>13C abundance, from Dwight Matthews (as in IDCalc and Hardklor).</summary>
    public const double Carbon13Abundance = 0.01084;

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

        // Each element's distribution summed by nominal shift (abundance, and abundance x mass), then combined.
        var abundance = new[] { 1.0 };
        var massTimes = new[] { 0.0 };
        foreach (var (element, count) in parts)
        {
            if (count <= 0)
                continue;
            var table = Tables.Value[element];
            var distribution = table.Distribution(count);
            var lightest = distribution.Min(p => p.Mass);
            var shifts = (int)Math.Round(distribution.Max(p => p.Mass) - lightest) + 1;
            var a = new double[shifts];
            var m = new double[shifts];
            foreach (var p in distribution)
            {
                var k = (int)Math.Round(p.Mass - lightest);
                a[k] += p.Abundance;
                m[k] += p.Abundance * p.Mass;
            }
            (abundance, massTimes) = Combine(abundance, massTimes, a, m);
        }

        var max = abundance.Max();
        var result = new List<IsotopePeak>(abundance.Length);
        for (var i = 0; i < abundance.Length; i++)
        {
            var relative = abundance[i] / max;
            if (abundance[i] <= 0 || relative < abundanceCutoff)
                continue;
            result.Add(new IsotopePeak(massTimes[i] / abundance[i], relative));
        }
        return result.ToArray();
    }

    /// <summary>
    /// The nominal-shift distribution of two independent parts: abundances multiply, and mean masses add
    /// (tracked as abundance x mass, which convolves as a1 m2 + m1 a2).
    /// </summary>
    internal static (double[] Abundance, double[] MassTimesAbundance) Combine(double[] a1, double[] m1, double[] a2, double[] m2)
    {
        var a = new double[a1.Length + a2.Length - 1];
        var m = new double[a.Length];
        for (var i = 0; i < a1.Length; i++)
        {
            for (var j = 0; j < a2.Length; j++)
            {
                var p = a1[i] * a2[j];
                if (p < ProbabilityCutoff)
                    continue;
                a[i + j] += p;
                m[i + j] += a1[i] * m2[j] + m1[i] * a2[j];
            }
        }
        return (a, m);
    }
}
