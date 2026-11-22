using Glimpse.Core.Data;

namespace Glimpse.Core.Scans;

/// <summary>
/// Something that wants to see every spectrum during the one sequential pass over a file: a heatmap
/// accumulator, the assigned-signal sweep, the per-precursor XIC extractor. Reading a large run is the
/// expensive part, so everything that needs the peaks rides along on the same pass.
/// </summary>
public interface IScanObserver
{
    /// <summary>Called once before the first spectrum.</summary>
    /// <param name="spectrumCount">Number of spectra in the file.</param>
    /// <param name="rtFirst">Retention time of the first spectrum (minutes), NaN when unknown.</param>
    /// <param name="rtLast">Retention time of the last spectrum (minutes), NaN when unknown.</param>
    void Begin(int spectrumCount, double rtFirst, double rtLast);

    /// <summary>Called for each spectrum, in file order.</summary>
    void Observe(SpectrumData spectrum);

    /// <summary>Called once after the last spectrum (not called when the pass is cancelled).</summary>
    void End();
}
