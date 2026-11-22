using Glimpse.Core.Data;
using Glimpse.Core.Document;

namespace Glimpse.Core.Assignment;

/// <summary>How a spectrum's candidate peptides are chosen from the document.</summary>
public sealed record CandidateOptions
{
    /// <summary>Widen each integrated peak by this many minutes on both sides.</summary>
    public double ExtendMinutes { get; init; }

    /// <summary>
    /// Ignore retention time: every precursor whose m/z falls in an MS2 scan's isolation window (or, for
    /// MS1, every precursor) is a candidate. For exploring what could be in a spectrum.
    /// </summary>
    public bool IgnoreRetentionTime { get; init; }

    public static CandidateOptions Default { get; } = new();
}

/// <summary>
/// Finds the document precursors that belong in a spectrum: eluting at its retention time (inside
/// Skyline's integrated peak in this file) and, for MS2, with precursor m/z in the range Skyline extracts
/// from the scan (its isolation scheme's window, or the scan's own isolation window when there is none).
/// </summary>
public sealed class SpectrumCandidates
{
    private readonly DocPrecursor[] _byMz;
    private readonly double[] _mz;
    private readonly IsolationScheme? _scheme;

    public SpectrumCandidates(IEnumerable<DocPrecursor> precursors, IsolationScheme? scheme = null)
    {
        _byMz = precursors.Where(p => double.IsFinite(p.Mz)).OrderBy(p => p.Mz).ToArray();
        _mz = _byMz.Select(p => p.Mz).ToArray();
        _scheme = scheme;
    }

    public IReadOnlyList<DocPrecursor> Find(in ScanHeader scan, string fileName, CandidateOptions options)
    {
        var result = new List<DocPrecursor>();
        int from = 0, to = _byMz.Length;
        if (scan.MsLevel >= 2)
        {
            if (IsolationScheme.RangeFor(_scheme, scan) is not { } range)
                return result;
            from = SpectrumData.LowerBound(_mz, range.Lo);
            to = SpectrumData.LowerBound(_mz, range.Hi);
        }
        for (var i = from; i < to; i++)
        {
            var p = _byMz[i];
            if (options.IgnoreRetentionTime)
            {
                result.Add(p);
                continue;
            }
            if (p.Peaks.TryGetValue(fileName, out var peak) && peak.Contains(scan.RetentionTime, options.ExtendMinutes))
                result.Add(p);
        }
        return result;
    }
}
