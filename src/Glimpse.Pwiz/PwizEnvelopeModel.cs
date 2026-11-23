using System.Collections.Concurrent;
using Glimpse.Features;
using Pwiz.Util.Chemistry;

namespace Glimpse.Pwiz;

/// <summary>
/// Isotope distributions from pwiz's <see cref="IsotopeCalculator"/>, summed into one peak per nominal mass:
/// the isotopic fine structure a mass analyzer does not resolve becomes a single peak at its
/// abundance-weighted mean mass. Thread-safe; distributions are cached per formula.
/// </summary>
public sealed class PwizEnvelopeModel : IEnvelopeModel
{
    // Fine-structure terms below this probability are dropped. The library keeps peaks above 1% of the most
    // abundant, which for a 35 kDa molecule is still above 1e-4, so the terms dropped here do not matter.
    private const double ProbabilityCutoff = 1e-9;

    // Fine structure closer than this is merged inside the calculator; nominal binning does the rest.
    private const double MassPrecision = 0.05;

    private readonly IsotopeCalculator _calculator = new(ProbabilityCutoff, MassPrecision);
    private readonly ConcurrentDictionary<(ElementCounts Formula, double Cutoff), IsotopePeak[]> _cache = new();

    public IReadOnlyList<IsotopePeak> Distribution(ElementCounts formula, double abundanceCutoff) =>
        _cache.GetOrAdd((formula, abundanceCutoff), key => Compute(key.Formula, key.Cutoff));

    private IsotopePeak[] Compute(ElementCounts counts, double abundanceCutoff)
    {
        var formula = new Formula
        {
            [ElementType.C] = counts.C,
            [ElementType.H] = counts.H,
            [ElementType.N] = counts.N,
            [ElementType.O] = counts.O,
            [ElementType.S] = counts.S,
        };
        if (counts.C + counts.H + counts.N + counts.O + counts.S <= 0)
            return Array.Empty<IsotopePeak>();

        var mono = formula.MonoisotopicMass;
        var distribution = _calculator.Distribution(formula);
        if (distribution.Count == 0)
            return Array.Empty<IsotopePeak>();

        // Sum fine structure into nominal-mass clusters (offset from the monoisotopic mass).
        var maxOffset = 0;
        foreach (var peak in distribution)
            maxOffset = Math.Max(maxOffset, (int)Math.Round(peak.Mass - mono));
        var abundance = new double[maxOffset + 1];
        var massTimesAbundance = new double[maxOffset + 1];
        foreach (var peak in distribution)
        {
            var offset = (int)Math.Round(peak.Mass - mono);
            if (offset < 0) continue;
            abundance[offset] += peak.Abundance;
            massTimesAbundance[offset] += peak.Abundance * peak.Mass;
        }

        var max = abundance.Max();
        var result = new List<IsotopePeak>(abundance.Length);
        for (var i = 0; i < abundance.Length; i++)
        {
            var relative = abundance[i] / max;
            if (abundance[i] <= 0 || relative < abundanceCutoff)
                continue;
            result.Add(new IsotopePeak(massTimesAbundance[i] / abundance[i], relative));
        }
        return result.ToArray();
    }
}
