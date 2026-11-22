using Glimpse.Core.Data;

namespace Glimpse.Core.Scans;

/// <summary>
/// The one sequential pass over a run: reads every spectrum with its peaks, records its header, and
/// hands it to each <see cref="IScanObserver"/>.
/// </summary>
public static class ScanIndexBuilder
{
    /// <summary>
    /// Read every spectrum of <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The opened file.</param>
    /// <param name="centroid">Request vendor centroiding of profile spectra (when the reader can).</param>
    /// <param name="observers">Accumulators that see every spectrum.</param>
    /// <param name="progress">Receives the fraction done, 0-1, about every 1% of the run.</param>
    /// <param name="ct">Cancels the pass; observers' End is then not called.</param>
    /// <param name="preview">
    /// Receives an index of the spectra read so far about every <see cref="PreviewSteps"/>th of the run,
    /// so a chromatogram can be drawn while a long file is still being read.
    /// </param>
    public static ScanIndex Build(
        IMsDataSource source,
        bool centroid,
        IReadOnlyList<IScanObserver> observers,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IProgress<ScanPassPreview>? preview = null)
    {
        var count = source.SpectrumCount;
        var headers = new ScanHeader[count];
        var rtFirst = count > 0 ? source.ReadRetentionTime(0) : double.NaN;
        var rtLast = count > 0 ? source.ReadRetentionTime(count - 1) : double.NaN;
        foreach (var o in observers)
            o.Begin(count, rtFirst, rtLast);

        var reportEvery = Math.Max(1, count / 100);
        var previewEvery = Math.Max(1, count / PreviewSteps);
        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFF) == 0)
                ct.ThrowIfCancellationRequested();

            var spectrum = source.ReadSpectrum(i, centroid);
            headers[i] = spectrum.Header;
            foreach (var o in observers)
                o.Observe(spectrum);

            if (i % reportEvery == 0)
                progress?.Report((double)i / count);
            if (preview is not null && (i + 1) % previewEvery == 0 && i + 1 < count)
                preview.Report(new ScanPassPreview(new ScanIndex(source.FilePath, source.Run, headers[..(i + 1)]), rtFirst, rtLast));
        }

        foreach (var o in observers)
            o.End();
        progress?.Report(1.0);
        return new ScanIndex(source.FilePath, source.Run, headers);
    }

    /// <summary>How many previews a pass reports: often enough to look live, rarely enough to cost nothing.</summary>
    public const int PreviewSteps = 25;
}

/// <summary>The spectra read so far in a pass, and the retention-time range the whole run will cover.</summary>
public sealed record ScanPassPreview(ScanIndex SoFar, double RtFirst, double RtLast)
{
    /// <summary>The fraction of the run's time covered so far, 0-1.</summary>
    public double Fraction => RtLast > RtFirst && double.IsFinite(SoFar.RtLast)
        ? Math.Clamp((SoFar.RtLast - RtFirst) / (RtLast - RtFirst), 0, 1)
        : 0;
}
