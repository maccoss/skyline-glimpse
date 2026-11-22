namespace Glimpse.Core.Data;

/// <summary>
/// One opened mass spectrometry data file. This is the reader seam: only Glimpse.Pwiz implements it,
/// so moving from bundled pwiz-sharp to Skyline's own installed readers touches nothing else.
/// </summary>
/// <remarks>
/// Implementations need not be thread-safe. Callers that read from more than one thread open more than
/// one source.
/// </remarks>
public interface IMsDataSource : IDisposable
{
    /// <summary>The path the source was opened from.</summary>
    string FilePath { get; }

    /// <summary>Number of spectra in the run.</summary>
    int SpectrumCount { get; }

    /// <summary>Run-level metadata: instrument, start time and so on.</summary>
    RunInfo Run { get; }

    /// <summary>True when the reader can centroid profile spectra with the vendor's own algorithm.</summary>
    bool CanVendorCentroid { get; }

    /// <summary>
    /// Read one spectrum with its peak arrays. With <paramref name="centroid"/> true and
    /// <see cref="CanVendorCentroid"/> true, profile spectra are centroided by the vendor library;
    /// otherwise the peaks come back as stored.
    /// </summary>
    SpectrumData ReadSpectrum(int index, bool centroid);

    /// <summary>Retention time (minutes) of one spectrum without decoding its peaks. NaN when absent.</summary>
    double ReadRetentionTime(int index);
}
