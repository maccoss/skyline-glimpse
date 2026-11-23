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
    /// <remarks>
    /// A trace that combines several precursors never counts the same signal twice. With
    /// <paramref name="integratedOnly"/> it adds up the union of the peaks the precursors claimed (see
    /// <see cref="PeakClaims"/>), so a peak caught by two precursors' windows, or by the copies of a peptide
    /// listed under two proteins, counts once and a trace stays at or below the TIC. Without it the margins
    /// around each peak hold no claims, and each distinct precursor (sequence, charge, label) is summed once:
    /// the copies of a shared peptide no longer add up, though two precursors' overlapping windows still can.
    /// </remarks>
    public static List<XicTrace> Build(
        ScanIndex index, DocumentSignals signals, IReadOnlyList<DocPeptide> peptides, XicMode mode, bool ms1, IntensityUnits units,
        bool integratedOnly = false, IonCalibration? calibration = null)
    {
        var result = new List<XicTrace>();
        if (mode == XicMode.Off || peptides.Count == 0)
            return result;

        // A peptide listed under several proteins is one molecule: one trace, counted once.
        var distinct = DistinctMolecules(peptides);

        (double[] T, double[] Y)? Trace(IEnumerable<DocPeptide> group) =>
            Combined(index, signals, group.SelectMany(p => p.Precursors), ms1, units, integratedOnly, calibration);
        // Labels count the peptides that have an XIC in this file.
        int Measured(IEnumerable<DocPeptide> group) => group.Count(p => p.Precursors.Any(signals.Xics.ContainsKey));

        switch (mode)
        {
            case XicMode.PerPeptide:
                // One trace per charge state (and label), named as the spectrum names it ("AAK 2+").
                foreach (var peptide in distinct)
                {
                    foreach (var precursor in peptide.Precursors)
                    {
                        if (Combined(index, signals, new[] { precursor }, ms1, units, integratedOnly, calibration) is { } t)
                        {
                            result.Add(new XicTrace($"{Assignment.PeakLabels.Precursor(precursor, "")} ({peptide.Protein.Name})",
                                t.T, t.Y, precursor));
                        }
                    }
                }
                break;
            case XicMode.PerProtein:
                // Every protein keeps its own copy of a shared peptide: each protein's trace is whole.
                foreach (var group in peptides.GroupBy(p => p.Protein))
                {
                    if (Trace(group) is { } t)
                        result.Add(new XicTrace($"{group.Key.Name} ({Measured(group)} peptides)", t.T, t.Y, group.Key));
                }
                break;
            case XicMode.Summed:
                if (Trace(distinct) is { } sum)
                    result.Add(new XicTrace($"Sum of {Measured(distinct):N0} peptides", sum.T, sum.Y, null));
                break;
        }
        return result;
    }

    /// <summary>The peptides, each molecule (modified sequence) once, the first of its copies kept.</summary>
    internal static List<DocPeptide> DistinctMolecules(IEnumerable<DocPeptide> peptides)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return peptides.Where(p => seen.Add(p.ModifiedSequence)).ToList();
    }

    /// <summary>
    /// One trace for a set of precursors, each signal counted once (see <see cref="Build"/>); null when none
    /// of them has a point.
    /// </summary>
    private static (double[] T, double[] Y)? Combined(
        ScanIndex index, DocumentSignals signals, IEnumerable<DocPrecursor> precursors, bool ms1, IntensityUnits units, bool integratedOnly,
        IonCalibration? calibration)
    {
        var withXic = new List<(DocPrecursor Precursor, PrecursorXic Xic)>();
        var molecules = new HashSet<(string, int, string)>();
        foreach (var precursor in precursors)
        {
            // Copies of one precursor under different proteins: the first stands for all of them.
            if (molecules.Add((precursor.ModifiedSequence, precursor.Charge, precursor.LabelType))
                && signals.Xics.TryGetValue(precursor, out var xic))
            {
                withXic.Add((precursor, xic));
            }
        }
        if (withXic.Count == 0)
            return null;

        if (integratedOnly && withXic.All(x => (ms1 ? x.Xic.Ms1Claims : x.Xic.Ms2Claims) is not null))
            return Union(index, signals, withXic, ms1, units, calibration);

        var parts = new List<(double[] T, double[] Y)>();
        foreach (var (precursor, xic) in withXic)
        {
            var (from, to) = Bounds(precursor, signals.FileName, integratedOnly);
            var trace = Points(index, ms1 ? xic.Ms1Scans : xic.Ms2Scans, ms1 ? xic.Ms1Values : xic.Ms2Values, units, from, to, calibration);
            if (trace.T.Length > 0)
                parts.Add(trace);
        }
        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => SumOnGrid(parts, GridStep(index)),
        };
    }

    /// <summary>
    /// Per scan, the intensity of the peaks any of the precursors claimed inside its integrated peak, each
    /// peak once. Scans inside a precursor's peak where it claimed nothing are zero, so the trace returns to
    /// the baseline between peaks instead of being drawn across them.
    /// </summary>
    private static (double[] T, double[] Y)? Union(
        ScanIndex index, DocumentSignals signals, List<(DocPrecursor Precursor, PrecursorXic Xic)> precursors, bool ms1, IntensityUnits units,
        IonCalibration? calibration)
    {
        var perScan = new Dictionary<int, double>();
        var counted = new HashSet<long>();
        foreach (var (precursor, xic) in precursors)
        {
            var (from, to) = Bounds(precursor, signals.FileName, integratedOnly: true);
            foreach (var scan in ms1 ? xic.Ms1Scans : xic.Ms2Scans)
            {
                var rt = index[scan].RetentionTime;
                if (rt >= from && rt <= to)
                    perScan.TryAdd(scan, 0);
            }
            var claims = (ms1 ? xic.Ms1Claims : xic.Ms2Claims)!;
            for (var i = 0; i < claims.Peaks.Length; i++)
            {
                if (counted.Add(claims.Peaks[i]))
                {
                    var scan = PeakClaims.ScanOf(claims.Peaks[i]);
                    perScan[scan] = perScan.GetValueOrDefault(scan) + claims.Intensities[i];
                }
            }
        }
        if (perScan.Count == 0)
            return null;

        // One series per kind of scan: the MS1 scans, or the scans of one isolation window. Within a
        // series the precursors share scans, and the union above counted each peak once. Across series the
        // signals come from different scans at different times in the cycle (a peptide's 2+ and 3+ in two
        // windows), so they are added on a common time grid; adding them scan by scan would alternate
        // between one window's value and the other's, a sawtooth.
        var traces = new List<(double[] T, double[] Y)>();
        foreach (var series in perScan.Keys.GroupBy(s => ms1 ? (0.0, 0.0) : WindowOf(index[s])))
        {
            var scans = series.Order().ToArray();
            var values = scans.Select(s => (float)perScan[s]).ToArray();
            var trace = Points(index, scans, values, units, double.NegativeInfinity, double.PositiveInfinity, calibration);
            if (trace.T.Length > 0)
                traces.Add(trace);
        }
        return traces.Count switch
        {
            0 => null,
            1 => traces[0],
            _ => SumOnGrid(traces, GridStep(index)),
        };
    }

    /// <summary>A scan's isolation window, rounded so the scans of one window share a key.</summary>
    private static (double, double) WindowOf(ScanHeader h) =>
        (Math.Round(h.IsolationLower, 3), Math.Round(h.IsolationUpper, 3));

    /// <summary>The precursor's integrated peak in this file, or the whole run when not limited to it.</summary>
    private static (double From, double To) Bounds(DocPrecursor precursor, string fileName, bool integratedOnly) =>
        integratedOnly && precursor.Peaks.TryGetValue(fileName, out var peak) && peak.HasBoundaries
            ? (peak.Start, peak.End)
            : (double.NegativeInfinity, double.PositiveInfinity);

    /// <summary>A trace's points from <paramref name="from"/> to <paramref name="to"/> minutes, inclusive.</summary>
    private static (double[] T, double[] Y) Points(ScanIndex index, int[] scans, float[] values, IntensityUnits units, double from, double to,
        IonCalibration? calibration)
    {
        var t = new List<double>(scans.Length);
        var y = new List<double>(scans.Length);
        var lastT = double.NegativeInfinity;
        for (var i = 0; i < scans.Length; i++)
        {
            var h = index[scans[i]];
            var v = IntensityConversion.Convert(values[i], h, units, calibration);
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
