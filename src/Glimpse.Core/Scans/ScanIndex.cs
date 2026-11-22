using Glimpse.Core.Data;

namespace Glimpse.Core.Scans;

/// <summary>
/// Every spectrum header of one run, in file order, plus lookups the views need: nearest scan of a
/// level at a retention time, the distinct isolation windows, whether ion conversion is possible.
/// </summary>
public sealed class ScanIndex
{
    private readonly ScanHeader[] _headers;
    private readonly int[][] _byLevel;

    public ScanIndex(string filePath, RunInfo run, ScanHeader[] headers)
    {
        FilePath = filePath;
        Run = run;
        _headers = headers;

        var maxLevel = headers.Length == 0 ? 0 : headers.Max(h => h.MsLevel);
        _byLevel = new int[maxLevel + 1][];
        for (var level = 0; level <= maxLevel; level++)
        {
            var l = level;
            _byLevel[level] = headers.Where(h => h.MsLevel == l).Select(h => h.Index).ToArray();
        }

        var withTime = headers.Where(h => !double.IsNaN(h.RetentionTime)).ToArray();
        RtFirst = withTime.Length > 0 ? withTime.Min(h => h.RetentionTime) : double.NaN;
        RtLast = withTime.Length > 0 ? withTime.Max(h => h.RetentionTime) : double.NaN;
        HasInjectionTimes = headers.Length > 0 && headers.Any(h => h.HasInjectionTime);
        IsolationWindows = DistinctWindows(headers);
    }

    public string FilePath { get; }

    public RunInfo Run { get; }

    public IReadOnlyList<ScanHeader> Headers => _headers;

    public int Count => _headers.Length;

    public double RtFirst { get; }

    public double RtLast { get; }

    /// <summary>True when at least one spectrum reports an injection time, so "ions" units make sense.</summary>
    public bool HasInjectionTimes { get; }

    /// <summary>
    /// Distinct MS2 isolation windows, ascending by lower bound. Empty for MS1-only data. For DDA data
    /// this can be large (one per precursor); callers that present it as a list should cap it.
    /// </summary>
    public IReadOnlyList<(double Lower, double Upper)> IsolationWindows { get; }

    public ScanHeader this[int index] => _headers[index];

    /// <summary>
    /// The analyzer every scan of an MS level was recorded with, or Unknown when the level has no scans or
    /// mixes analyzers (an Orbitrap Fusion measuring some MS2 scans in the ion trap, some in the Orbitrap).
    /// </summary>
    public MassAnalyzer AnalyzerOf(int msLevel)
    {
        var scans = IndicesOfLevel(msLevel);
        if (scans.Count == 0)
            return MassAnalyzer.Unknown;
        var first = _headers[scans[0]].Analyzer;
        for (var i = 1; i < scans.Count; i++)
        {
            if (_headers[scans[i]].Analyzer != first)
                return MassAnalyzer.Unknown;
        }
        return first;
    }

    /// <summary>Spectrum indices of one MS level, in file (and so retention-time) order.</summary>
    public IReadOnlyList<int> IndicesOfLevel(int msLevel) =>
        msLevel >= 0 && msLevel < _byLevel.Length ? _byLevel[msLevel] : Array.Empty<int>();

    /// <summary>
    /// The spectrum passing <paramref name="filter"/> nearest to <paramref name="rt"/>, or -1 when none
    /// passes.
    /// </summary>
    public int NearestScan(double rt, ScanFilter filter)
    {
        IReadOnlyList<int> candidates = filter.MsLevel is { } level ? IndicesOfLevel(level) : Enumerable.Range(0, Count).ToArray();
        var best = -1;
        var bestDistance = double.PositiveInfinity;
        // Candidates are in RT order, so binary search to the neighborhood and scan outward while the
        // filter rejects (e.g. other DIA windows).
        var pos = LowerBoundRt(candidates, rt);
        for (var i = pos; i < candidates.Count; i++)
        {
            var h = _headers[candidates[i]];
            var d = Math.Abs(h.RetentionTime - rt);
            if (d > bestDistance)
                break;
            if (filter.Accepts(h))
            {
                best = h.Index;
                bestDistance = d;
                break;
            }
        }
        for (var i = pos - 1; i >= 0; i--)
        {
            var h = _headers[candidates[i]];
            var d = Math.Abs(h.RetentionTime - rt);
            if (d >= bestDistance)
                break;
            if (filter.Accepts(h))
            {
                best = h.Index;
                bestDistance = d;
                break;
            }
        }
        return best;
    }

    /// <summary>The next (<paramref name="step"/> = +1) or previous (-1) spectrum passing the filter, or -1.</summary>
    public int StepScan(int fromIndex, int step, ScanFilter filter)
    {
        for (var i = fromIndex + step; i >= 0 && i < Count; i += step)
        {
            if (filter.Accepts(_headers[i]))
                return i;
        }
        return -1;
    }

    private int LowerBoundRt(IReadOnlyList<int> indices, double rt)
    {
        int lo = 0, hi = indices.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_headers[indices[mid]].RetentionTime < rt)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static IReadOnlyList<(double, double)> DistinctWindows(ScanHeader[] headers)
    {
        var set = new SortedSet<(double, double)>();
        foreach (var h in headers)
        {
            if (h.MsLevel >= 2 && h.HasIsolationWindow)
                set.Add((Math.Round(h.IsolationLower, 3), Math.Round(h.IsolationUpper, 3)));
        }
        return set.ToArray();
    }
}
