using Glimpse.Core.Data;

namespace Glimpse.Features;

/// <summary>The peptide-like isotope envelopes of one spectrum, with their peaks, for labeling it.</summary>
public static class PeptideLikeEnvelopes
{
    /// <summary>
    /// The envelopes of scan <paramref name="scan"/> that belong to persistent features, with their peaks. The
    /// spectrum is searched again with <paramref name="finder"/> (about a millisecond): Hardklör is deterministic,
    /// so it finds the same envelopes, in the same order, as the run's search, and each is checked against the
    /// one the search linked before it is used. A search read from a .hk file keeps no peaks, so this is how its
    /// envelopes get them back. Empty when the scan was not searched or the envelopes found differ (a spectrum
    /// read another way, another finder).
    /// </summary>
    /// <param name="ms1MinCharge">Lowest charge an MS1 envelope may have (MS2 envelopes of every charge count).</param>
    public static IReadOnlyList<IsotopeEnvelope> Persistent(PeptideLikeSignal signal, int scan, double[] mz, double[] intensity,
        MassAnalyzer analyzer, IEnvelopeFinder finder, int ms1MinCharge = 1)
    {
        if (!signal.Covers(scan) || !signal.Envelopes.TryGetValue(scan, out var linked) || linked.Keys.Length == 0)
            return Array.Empty<IsotopeEnvelope>();
        var found = finder.Find(mz, intensity, analyzer);
        if (found.Count != linked.Keys.Length)
            return Array.Empty<IsotopeEnvelope>();
        for (var e = 0; e < found.Count; e++)
        {
            var key = linked.Keys[e];
            if (found[e].Charge != key.Charge || found[e].MonoMass != key.MonoMass || found[e].Intensity != key.Intensity)
                return Array.Empty<IsotopeEnvelope>();
        }
        var ids = signal.FeatureOf[scan];
        var minCharge = signal.Headers[scan].MsLevel == 1 ? ms1MinCharge : 1;
        return found.Where((e, i) => ids[i] >= 0 && e.Charge >= minCharge).ToArray();
    }

    /// <summary>The index of an envelope's base peak in the spectrum (its matched peak nearest the base peak m/z).</summary>
    public static int BasePeakIndex(IsotopeEnvelope envelope, double[] mz) =>
        envelope.PeakIndices.MinBy(p => Math.Abs(mz[p] - envelope.BasePeakMz));
}
