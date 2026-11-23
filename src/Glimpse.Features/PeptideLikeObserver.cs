using System.Collections.Concurrent;
using Glimpse.Core.Data;
using Glimpse.Core.Scans;

namespace Glimpse.Features;

/// <summary>Finds the isotope envelopes in one centroided spectrum (Hardklör, in Glimpse).</summary>
public interface IEnvelopeFinder
{
    /// <summary>
    /// The envelopes in a spectrum with ascending m/z, recorded by <paramref name="analyzer"/> (its peak widths
    /// differ). Called from several threads at once.
    /// </summary>
    IReadOnlyList<IsotopeEnvelope> Find(double[] mz, double[] intensity, MassAnalyzer analyzer);
}

/// <summary>
/// Rides on the scan pass: hands every MS1 scan, and every MS2 scan of a DIA run, to worker threads that find
/// its isotope envelopes while the file is still being read; after the pass, links envelopes across scans
/// (MS1 scans together, each DIA isolation window on its own) into persistent features. Their peaks are the
/// peptide-like signal.
/// </summary>
/// <remarks>
/// <para>MS2 scans count only in DIA, where each isolation window repeats every cycle. A window seen for the
/// first time is held until it comes back; once more than <see cref="MaxDiaWindows"/> distinct windows have
/// appeared the run is DDA (a window per precursor) and MS2 scans are left out.</para>
/// <para>The pass never waits for the envelope search beyond a bounded queue: <see cref="End"/> returns at
/// once, and <see cref="Completion"/> finishes when the last scan is searched and linked.</para>
/// </remarks>
public sealed class PeptideLikeObserver : IScanObserver, IDisposable
{
    /// <summary>More distinct MS2 isolation windows than this: DDA, no MS2 envelopes.</summary>
    public const int MaxDiaWindows = 1000;

    private const int QueueDepth = 256;

    private readonly IEnvelopeFinder _finder;
    private readonly KronikSettings _linking;
    private readonly int _workerCount;
    private readonly CancellationTokenSource _cancel;
    private readonly TaskCompletionSource<PeptideLikeSignal> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<int, ScanEnvelopes> _found = new();
    private readonly Dictionary<(long, long), int> _windowCounts = new();
    private readonly Dictionary<(long, long), SpectrumData> _firstOfWindow = new();
    private BlockingCollection<SpectrumData>? _queue;
    private Task[] _workers = Array.Empty<Task>();
    private readonly List<int> _ms1 = new();
    private readonly Dictionary<(long, long), List<int>> _ms2ByWindow = new();
    private ScanHeader[] _headers = Array.Empty<ScanHeader>();
    private bool _dda;

    /// <param name="finder">Finds the envelopes of one spectrum.</param>
    /// <param name="linking">How envelopes are linked across scans.</param>
    /// <param name="workers">Threads searching spectra (default: half the logical processors).</param>
    /// <param name="cancellation">Stops the search when the pass is abandoned.</param>
    public PeptideLikeObserver(IEnvelopeFinder finder, KronikSettings? linking = null, int workers = 0, CancellationToken cancellation = default)
    {
        _finder = finder;
        _linking = linking ?? KronikSettings.Default;
        _workerCount = workers > 0 ? workers : Math.Max(1, Environment.ProcessorCount / 2);
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    }

    /// <summary>The peptide-like signal, once every scan has been searched and linked.</summary>
    public Task<PeptideLikeSignal> Completion => _completion.Task;

    public void Begin(int spectrumCount, double rtFirst, double rtLast)
    {
        _headers = new ScanHeader[spectrumCount];
        _queue = new BlockingCollection<SpectrumData>(QueueDepth);
        var token = _cancel.Token;
        var queue = _queue;
        _workers = Enumerable.Range(0, _workerCount).Select(_ => Task.Run(() => Search(queue, token), token)).ToArray();
    }

    public void Observe(SpectrumData spectrum)
    {
        var h = spectrum.Header;
        if (h.Index >= 0 && h.Index < _headers.Length)
            _headers[h.Index] = h;
        if (h.MsLevel == 1)
        {
            _ms1.Add(h.Index);
            Enqueue(spectrum);
        }
        else if (h.MsLevel >= 2 && h.HasIsolationWindow && !_dda)
        {
            var window = WindowKey(h);
            if (!_ms2ByWindow.TryGetValue(window, out var series))
            {
                if (_ms2ByWindow.Count >= MaxDiaWindows)
                {
                    // A window per precursor: DDA. Forget the MS2 scans held or searched so far.
                    _dda = true;
                    _firstOfWindow.Clear();
                    _ms2ByWindow.Clear();
                    return;
                }
                _ms2ByWindow[window] = series = new List<int>();
            }
            series.Add(h.Index);
            var seen = _windowCounts[window] = _windowCounts.GetValueOrDefault(window) + 1;
            if (seen == 1)
            {
                _firstOfWindow[window] = spectrum;   // held until the window comes back
                return;
            }
            if (seen == 2 && _firstOfWindow.Remove(window, out var first))
                Enqueue(first);
            Enqueue(spectrum);
        }
    }

    public void End()
    {
        _queue?.CompleteAdding();
        var workers = _workers;
        var ms1 = _ms1.ToArray();
        var dda = _dda;
        // DIA windows: the ones that came back (a window seen once cannot persist anyway).
        var windows = dda ? new List<int[]>() : _ms2ByWindow.Where(p => p.Value.Count > 1).Select(p => p.Value.ToArray()).ToList();
        var headers = _headers;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
                _completion.TrySetResult(Link(headers, ms1, windows, dda));
            }
            catch (OperationCanceledException)
            {
                _completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        });
    }

    /// <summary>Stops the search (an abandoned pass never calls <see cref="End"/>).</summary>
    public void Dispose()
    {
        _cancel.Cancel();
        _queue?.CompleteAdding();
        _completion.TrySetCanceled();
        _cancel.Dispose();
    }

    private void Enqueue(SpectrumData spectrum)
    {
        try
        {
            _queue!.Add(spectrum, _cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // Abandoned: the pass is stopping too.
        }
    }

    private void Search(BlockingCollection<SpectrumData> queue, CancellationToken token)
    {
        foreach (var spectrum in queue.GetConsumingEnumerable(token))
        {
            var envelopes = spectrum.Mz.Length == 0 ? Array.Empty<IsotopeEnvelope>() : _finder.Find(spectrum.Mz, spectrum.Intensity, spectrum.Header.Analyzer);
            _found[spectrum.Header.Index] = ScanEnvelopes.From(envelopes, spectrum.Intensity);
        }
    }

    private PeptideLikeSignal Link(ScanHeader[] headers, int[] ms1, List<int[]> windows, bool dda)
    {
        var series = new List<int[]> { ms1 };
        series.AddRange(windows);
        var featureOf = new Dictionary<int, int[]>();
        var features = new List<(int Series, PersistentFeature Feature)>();
        for (var s = 0; s < series.Count; s++)
        {
            var scans = series[s];
            var keys = scans.Select(i => _found.TryGetValue(i, out var e) ? e.Keys : Array.Empty<EnvelopeKey>()).ToArray();
            var result = Kronik.Link(keys, _linking);
            var offset = features.Count;
            features.AddRange(result.Features.Select(f => (s, f)));
            for (var k = 0; k < scans.Length; k++)
                featureOf[scans[k]] = result.FeatureOf[k].Select(id => id < 0 ? -1 : id + offset).ToArray();
        }
        return new PeptideLikeSignal(headers, new Dictionary<int, ScanEnvelopes>(_found), featureOf, features, series, dda);
    }

    /// <summary>An isolation window to 0.01 m/z: DIA repeats it exactly.</summary>
    private static (long, long) WindowKey(in ScanHeader h) =>
        ((long)Math.Round(h.IsolationLower * 100), (long)Math.Round(h.IsolationUpper * 100));
}

/// <summary>
/// The envelopes found in one scan, kept compact: what linking needs, and which of the scan's peaks each
/// envelope covers (as positions in <see cref="PeakIntensity"/>, the envelopes' distinct peaks).
/// </summary>
public sealed class ScanEnvelopes
{
    private ScanEnvelopes(EnvelopeKey[] keys, int[] peakIndex, float[] peakIntensity, int[] start, ushort[] members)
    {
        Keys = keys;
        PeakIndex = peakIndex;
        PeakIntensity = peakIntensity;
        _start = start;
        _members = members;
    }

    private readonly int[] _start;
    private readonly ushort[] _members;

    public static ScanEnvelopes Empty { get; } = new(Array.Empty<EnvelopeKey>(), Array.Empty<int>(), Array.Empty<float>(), new[] { 0 }, Array.Empty<ushort>());

    /// <summary>Mass, charge and intensity of each envelope, in the order found.</summary>
    public EnvelopeKey[] Keys { get; }

    /// <summary>The distinct peaks the envelopes cover: their index in the spectrum, ascending.</summary>
    public int[] PeakIndex { get; }

    /// <summary>The intensity of each of those peaks.</summary>
    public float[] PeakIntensity { get; }

    /// <summary>Positions (in <see cref="PeakIndex"/>) of envelope <paramref name="envelope"/>'s peaks.</summary>
    public ReadOnlySpan<ushort> PeaksOf(int envelope) => _members.AsSpan(_start[envelope], _start[envelope + 1] - _start[envelope]);

    public static ScanEnvelopes From(IReadOnlyList<IsotopeEnvelope> envelopes, double[] intensity)
    {
        if (envelopes.Count == 0)
            return Empty;
        var distinct = envelopes.SelectMany(e => e.PeakIndices).Distinct().Order().ToArray();
        if (distinct.Length > ushort.MaxValue)
            throw new InvalidOperationException($"{distinct.Length} envelope peaks in one spectrum.");
        var start = new int[envelopes.Count + 1];
        var members = new ushort[envelopes.Sum(e => e.PeakIndices.Length)];
        var k = 0;
        for (var e = 0; e < envelopes.Count; e++)
        {
            start[e] = k;
            foreach (var p in envelopes[e].PeakIndices)
                members[k++] = (ushort)Array.BinarySearch(distinct, p);
        }
        start[^1] = k;
        return new ScanEnvelopes(
            envelopes.Select(e => new EnvelopeKey(e.MonoMass, e.Charge, e.Intensity)).ToArray(),
            distinct, distinct.Select(i => (float)intensity[i]).ToArray(), start, members);
    }
}

/// <summary>
/// The peptide-like signal of a run: per scan, the intensity of the peaks belonging to persistent isotope
/// envelopes, and the features themselves.
/// </summary>
public sealed class PeptideLikeSignal
{
    private readonly ScanHeader[] _headers;
    private readonly IReadOnlyDictionary<int, ScanEnvelopes> _envelopes;
    private readonly IReadOnlyDictionary<int, int[]> _featureOf;
    private readonly Dictionary<int, double[]> _intensityByMinCharge = new();

    internal PeptideLikeSignal(
        ScanHeader[] headers, IReadOnlyDictionary<int, ScanEnvelopes> envelopes, IReadOnlyDictionary<int, int[]> featureOf,
        IReadOnlyList<(int Series, PersistentFeature Feature)> features, IReadOnlyList<int[]> series, bool dda)
    {
        _headers = headers;
        _envelopes = envelopes;
        _featureOf = featureOf;
        Features = features;
        Series = series;
        IsDda = dda;
    }

    /// <summary>The persistent features and the series (0: MS1; then each DIA window) each was found in.</summary>
    public IReadOnlyList<(int Series, PersistentFeature Feature)> Features { get; }

    /// <summary>Scan indices of each series: MS1 first, then each DIA isolation window.</summary>
    public IReadOnlyList<int[]> Series { get; }

    /// <summary>True when MS2 scans were left out because the run is DDA.</summary>
    public bool IsDda { get; }

    /// <summary>True when the scan was searched for envelopes (MS1 scans, and DIA MS2 scans).</summary>
    public bool Covers(int scan) => _featureOf.ContainsKey(scan);

    /// <summary>
    /// Per scan of the run, the summed intensity of its peaks in persistent envelopes (each peak once); NaN for
    /// a scan not searched. MS1 envelopes below <paramref name="ms1MinCharge"/> do not count; MS2 envelopes of
    /// any charge do (fragments are mostly 1+).
    /// </summary>
    public double[] IntensityByScan(int ms1MinCharge = 1)
    {
        lock (_intensityByMinCharge)
        {
            if (_intensityByMinCharge.TryGetValue(ms1MinCharge, out var cached))
                return cached;
            var result = new double[_headers.Length];
            Array.Fill(result, double.NaN);
            foreach (var (scan, ids) in _featureOf)
                result[scan] = PeptideLikeIntensity(scan, ids, _headers[scan].MsLevel == 1 ? ms1MinCharge : 1);
            return _intensityByMinCharge[ms1MinCharge] = result;
        }
    }

    /// <summary>Indices (in the scan's spectrum) of its peaks in persistent envelopes, ascending; empty if none.</summary>
    public int[] PeptideLikePeaks(int scan, int ms1MinCharge = 1)
    {
        if (!_featureOf.TryGetValue(scan, out var ids) || !_envelopes.TryGetValue(scan, out var env))
            return Array.Empty<int>();
        var minCharge = _headers[scan].MsLevel == 1 ? ms1MinCharge : 1;
        var covered = Covered(env, ids, minCharge);
        return Enumerable.Range(0, covered.Length).Where(i => covered[i]).Select(i => env.PeakIndex[i]).ToArray();
    }

    private double PeptideLikeIntensity(int scan, int[] ids, int minCharge)
    {
        if (!_envelopes.TryGetValue(scan, out var env))
            return 0;
        var covered = Covered(env, ids, minCharge);
        var sum = 0.0;
        for (var i = 0; i < covered.Length; i++)
        {
            if (covered[i])
                sum += env.PeakIntensity[i];
        }
        return sum;
    }

    private static bool[] Covered(ScanEnvelopes env, int[] ids, int minCharge)
    {
        var covered = new bool[env.PeakIndex.Length];
        for (var e = 0; e < ids.Length; e++)
        {
            if (ids[e] < 0 || env.Keys[e].Charge < minCharge)
                continue;
            foreach (var p in env.PeaksOf(e))
                covered[p] = true;
        }
        return covered;
    }
}
