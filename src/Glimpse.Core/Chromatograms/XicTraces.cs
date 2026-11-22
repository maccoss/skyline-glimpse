using Glimpse.Core.Assignment;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Scans;

namespace Glimpse.Core.Chromatograms;

public enum XicMode
{
    Off,
    Summed,
    PerProtein,
    PerPeptide,
}

/// <summary>One XIC trace and what it stands for (a peptide, a protein, or the whole selection).</summary>
public sealed record XicTrace(string Name, double[] Times, double[] Values, object? Owner);

/// <summary>Builds XIC traces for a peptide selection from the per-precursor XICs of the document pass.</summary>
public static class XicTraces
{
    /// <param name="ms1">True for the MS1 precursor-isotope traces, false for the MS2 fragment traces.</param>
    /// <param name="integratedOnly">
    /// Keep only each precursor's points between Skyline's integration boundaries in this file, instead of
    /// the whole extracted window (the peak plus a margin on each side).
    /// </param>
    public static List<XicTrace> Build(
        ScanIndex index, DocumentSignals signals, IReadOnlyList<DocPeptide> peptides, XicMode mode, bool ms1, IntensityUnits units,
        bool integratedOnly = false)
    {
        var result = new List<XicTrace>();
        if (mode == XicMode.Off || peptides.Count == 0)
            return result;

        // Each peptide's own trace: its precursors (charge states, labels) summed.
        var perPeptide = new List<(DocPeptide Peptide, double[] T, double[] Y)>();
        foreach (var peptide in peptides)
        {
            var parts = new List<(double[] T, double[] Y)>();
            foreach (var precursor in peptide.Precursors)
            {
                if (signals.Xics.TryGetValue(precursor, out var xic))
                {
                    var (from, to) = (double.NegativeInfinity, double.PositiveInfinity);
                    if (integratedOnly && precursor.Peaks.TryGetValue(signals.FileName, out var peak) && peak.HasBoundaries)
                        (from, to) = (peak.Start, peak.End);
                    var trace = Points(index, ms1 ? xic.Ms1Scans : xic.Ms2Scans, ms1 ? xic.Ms1Values : xic.Ms2Values, units, from, to);
                    if (trace.T.Length > 0)
                        parts.Add(trace);
                }
            }
            if (parts.Count == 1)
                perPeptide.Add((peptide, parts[0].T, parts[0].Y));
            else if (parts.Count > 1)
            {
                var (t, y) = SumOnGrid(parts, GridStep(index));
                perPeptide.Add((peptide, t, y));
            }
        }

        switch (mode)
        {
            case XicMode.PerPeptide:
                foreach (var (peptide, t, y) in perPeptide)
                    result.Add(new XicTrace($"{peptide.ModifiedSequence} ({peptide.Protein.Name})", t, y, peptide));
                break;
            case XicMode.PerProtein:
                foreach (var group in perPeptide.GroupBy(p => p.Peptide.Protein))
                {
                    var (t, y) = SumOnGrid(group.Select(g => (g.T, g.Y)).ToList(), GridStep(index));
                    result.Add(new XicTrace($"{group.Key.Name} ({group.Count()} peptides)", t, y, group.Key));
                }
                break;
            case XicMode.Summed:
                if (perPeptide.Count > 0)
                {
                    var (t, y) = SumOnGrid(perPeptide.Select(p => (p.T, p.Y)).ToList(), GridStep(index));
                    result.Add(new XicTrace($"Sum of {perPeptide.Count:N0} peptides", t, y, null));
                }
                break;
        }
        return result;
    }

    /// <summary>A trace's points from <paramref name="from"/> to <paramref name="to"/> minutes, inclusive.</summary>
    private static (double[] T, double[] Y) Points(ScanIndex index, int[] scans, float[] values, IntensityUnits units, double from, double to)
    {
        var t = new List<double>(scans.Length);
        var y = new List<double>(scans.Length);
        var lastT = double.NegativeInfinity;
        for (var i = 0; i < scans.Length; i++)
        {
            var h = index[scans[i]];
            var v = IntensityConversion.Convert(values[i], h.InjectionTimeMs, units);
            if (double.IsNaN(v) || double.IsNaN(h.RetentionTime) || h.RetentionTime < from || h.RetentionTime > to)
                continue;
            if (h.RetentionTime <= lastT)
            {
                // Overlapping isolation windows give two points per cycle at nearly the same time; keep
                // the trace monotonic in time by summing a point into its predecessor.
                y[^1] += v;
                continue;
            }
            t.Add(h.RetentionTime);
            y.Add(v);
            lastT = h.RetentionTime;
        }
        return (t.ToArray(), y.ToArray());
    }

    /// <summary>A time step fine enough to keep chromatographic peak shape: about half the median MS1 spacing.</summary>
    internal static double GridStep(ScanIndex index)
    {
        var ms1 = index.IndicesOfLevel(1);
        if (ms1.Count >= 3)
        {
            var gaps = new List<double>();
            for (var i = 1; i < ms1.Count; i += Math.Max(1, ms1.Count / 500))
                gaps.Add(index[ms1[i]].RetentionTime - index[ms1[i - 1]].RetentionTime);
            gaps.Sort();
            var median = gaps[gaps.Count / 2];
            if (median > 0)
                return median / 2;
        }
        var span = index.RtLast - index.RtFirst;
        return span > 0 ? span / 4000 : 0.01;
    }

    /// <summary>
    /// Sum traces sampled at different times by interpolating each onto a common grid. A trace
    /// contributes only inside its own time range (zero elsewhere), so short peak-window XICs add up
    /// without inventing signal between them.
    /// </summary>
    internal static (double[] T, double[] Y) SumOnGrid(IReadOnlyList<(double[] T, double[] Y)> traces, double step)
    {
        var live = traces.Where(t => t.T.Length > 0).ToList();
        if (live.Count == 0)
            return (Array.Empty<double>(), Array.Empty<double>());
        var t0 = live.Min(t => t.T[0]);
        var t1 = live.Max(t => t.T[^1]);
        // The grid runs exactly from the first to the last time: a last point past the data would end
        // every summed trace with a spurious drop to zero, and one short of it would clip the end. The
        // step is adjusted (slightly) so a whole number of steps spans the range.
        var n = Math.Max(2, (int)Math.Round((t1 - t0) / step) + 1);
        step = t1 > t0 ? (t1 - t0) / (n - 1) : step;
        var grid = new double[n];
        var sum = new double[n];
        for (var i = 0; i < n; i++)
            grid[i] = i == n - 1 ? t1 : t0 + i * step;
        foreach (var (T, Y) in live)
        {
            if (T.Length == 1)
            {
                var k = (int)Math.Round((T[0] - t0) / step);
                if (k >= 0 && k < n)
                    sum[k] += Y[0];
                continue;
            }
            var first = (int)Math.Ceiling((T[0] - t0) / step - 1e-9);
            var last = (int)Math.Floor((T[^1] - t0) / step + 1e-9);
            var j = 0;
            for (var k = Math.Max(0, first); k <= Math.Min(n - 1, last); k++)
            {
                var x = grid[k];
                while (j < T.Length - 2 && T[j + 1] < x)
                    j++;
                var span = T[j + 1] - T[j];
                var f = span > 0 ? (x - T[j]) / span : 0;
                sum[k] += Y[j] + f * (Y[j + 1] - Y[j]);
            }
        }
        return (grid, sum);
    }
}
