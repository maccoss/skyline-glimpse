using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Glimpse.Core.Data;

namespace Glimpse.Core.Scans;

/// <summary>
/// The one sequential pass over a run: reads every spectrum with its peaks, records its header, and
/// hands it to each <see cref="IScanObserver"/>.
/// </summary>
/// <remarks>
/// With several readers the reading runs on several threads, each with its own open copy of the file,
/// while the observers still see every spectrum on one thread and in order. Reading is the cost: on an
/// Astral run the vendor reader takes about 400 microseconds a spectrum and the observers about 60.
/// </remarks>
public static class ScanIndexBuilder
{
    /// <summary>
    /// Read every spectrum of <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The opened file.</param>
    /// <param name="centroid">Request vendor centroiding of profile spectra (when the reader can).</param>
    /// <param name="observers">Accumulators that see every spectrum, in order, on the calling thread.</param>
    /// <param name="progress">Receives the fraction done, 0-1, about every 1% of the run.</param>
    /// <param name="ct">Cancels the pass; observers' End is then not called.</param>
    /// <param name="preview">
    /// Receives an index of the spectra read so far about every <see cref="PreviewSteps"/>th of the run,
    /// so a chromatogram can be drawn while a long file is still being read.
    /// </param>
    /// <param name="extraReaders">
    /// More open copies of the same file. Each reads a share of the spectra on its own thread alongside
    /// <paramref name="source"/>. The caller opens and disposes them.
    /// </param>
    public static ScanIndex Build(
        IMsDataSource source,
        bool centroid,
        IReadOnlyList<IScanObserver> observers,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IProgress<ScanPassPreview>? preview = null,
        IReadOnlyList<IMsDataSource>? extraReaders = null)
    {
        var count = source.SpectrumCount;
        var headers = new ScanHeader[count];
        var rtFirst = count > 0 ? source.ReadRetentionTime(0) : double.NaN;
        var rtLast = count > 0 ? source.ReadRetentionTime(count - 1) : double.NaN;
        foreach (var o in observers)
            o.Begin(count, rtFirst, rtLast);

        var reportEvery = Math.Max(1, count / 100);
        var previewEvery = Math.Max(1, count / PreviewSteps);

        void Observe(int i, SpectrumData spectrum)
        {
            headers[i] = spectrum.Header;
            foreach (var o in observers)
                o.Observe(spectrum);
            if (i % reportEvery == 0)
                progress?.Report((double)i / count);
            if (preview is not null && (i + 1) % previewEvery == 0 && i + 1 < count)
                preview.Report(new ScanPassPreview(new ScanIndex(source.FilePath, source.Run, headers[..(i + 1)]), rtFirst, rtLast));
        }

        var readers = new List<IMsDataSource> { source };
        if (extraReaders is not null)
            readers.AddRange(extraReaders);
        if (readers.Count == 1 || count < readers.Count)
        {
            for (var i = 0; i < count; i++)
            {
                if ((i & 0xFF) == 0)
                    ct.ThrowIfCancellationRequested();
                Observe(i, source.ReadSpectrum(i, centroid));
            }
        }
        else
        {
            ReadInParallel(readers, count, centroid, Observe, ct);
        }

        foreach (var o in observers)
            o.End();
        progress?.Report(1.0);
        return new ScanIndex(source.FilePath, source.Run, headers);
    }

    /// <summary>How many previews a pass reports: often enough to look live, rarely enough to cost nothing.</summary>
    public const int PreviewSteps = 25;

    /// <summary>Spectra each reader may run ahead of the observers: enough to keep it busy, few enough to hold little memory.</summary>
    public const int QueueDepth = 32;

    /// <summary>
    /// Reader t reads spectra t, t + n, t + 2n... into its own bounded queue; the calling thread takes
    /// spectrum i from queue i mod n, so it sees every spectrum in order.
    /// </summary>
    private static void ReadInParallel(
        IReadOnlyList<IMsDataSource> readers, int count, bool centroid, Action<int, SpectrumData> observe, CancellationToken ct)
    {
        var n = readers.Count;
        var queues = new BlockingCollection<SpectrumData>[n];
        for (var t = 0; t < n; t++)
            queues[t] = new BlockingCollection<SpectrumData>(QueueDepth);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ExceptionDispatchInfo? failure = null;

        var tasks = new Task[n];
        for (var t = 0; t < n; t++)
        {
            var reader = readers[t];
            var queue = queues[t];
            var first = t;
            tasks[t] = Task.Factory.StartNew(() =>
            {
                try
                {
                    for (var i = first; i < count; i += n)
                        queue.Add(reader.ReadSpectrum(i, centroid), stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    // The first failure wins; it stops the other readers and the observers' loop.
                    Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(ex), null);
                    stop.Cancel();
                }
                finally
                {
                    queue.CompleteAdding();
                }
            }, TaskCreationOptions.LongRunning);
        }

        try
        {
            for (var i = 0; i < count; i++)
            {
                SpectrumData spectrum;
                try
                {
                    spectrum = queues[i % n].Take(stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                    // A reader failed (its error is the one to report), or the pass was cancelled.
                    failure?.Throw();
                    ct.ThrowIfCancellationRequested();
                    throw;
                }
                observe(i, spectrum);
            }
        }
        finally
        {
            stop.Cancel();
            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException)
            {
                // Each reader's error was recorded above.
            }
            foreach (var q in queues)
                q.Dispose();
        }
    }
}

/// <summary>The spectra read so far in a pass, and the retention-time range the whole run will cover.</summary>
public sealed record ScanPassPreview(ScanIndex SoFar, double RtFirst, double RtLast)
{
    /// <summary>The fraction of the run's time covered so far, 0-1.</summary>
    public double Fraction => RtLast > RtFirst && double.IsFinite(SoFar.RtLast)
        ? Math.Clamp((SoFar.RtLast - RtFirst) / (RtLast - RtFirst), 0, 1)
        : 0;
}
