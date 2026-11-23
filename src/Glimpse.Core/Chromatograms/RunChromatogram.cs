using Glimpse.Core.Data;
using Glimpse.Core.Scans;

namespace Glimpse.Core.Chromatograms;

/// <summary>Which whole-run trace to draw.</summary>
public enum RunTraceKind
{
    /// <summary>Total ion current: the sum of every peak in each scan.</summary>
    Tic,

    /// <summary>Base peak: the most intense peak in each scan.</summary>
    BasePeak,
}

/// <summary>One chromatogram trace: retention times, values, and the spectrum each point came from.</summary>
public sealed record Trace(string Name, double[] Times, double[] Values, int[] ScanIndices)
{
    public int Count => Times.Length;

    public double MaxValue => Values.Length == 0 ? 0 : Values.Where(double.IsFinite).DefaultIfEmpty(0).Max();
}

/// <summary>Builds TIC and base-peak traces from a <see cref="ScanIndex"/>, without re-reading the file.</summary>
public static class RunChromatogram
{
    /// <param name="calibration">The run's α factors, applied to ions (null: uncalibrated).</param>
    public static Trace Build(ScanIndex index, RunTraceKind kind, ScanFilter filter, IntensityUnits units, IonCalibration? calibration = null)
    {
        var times = new List<double>();
        var values = new List<double>();
        var scans = new List<int>();
        IEnumerable<int> candidates = filter.MsLevel is { } level
            ? index.IndicesOfLevel(level)
            : Enumerable.Range(0, index.Count);

        foreach (var i in candidates)
        {
            var h = index[i];
            if (!filter.Accepts(h) || double.IsNaN(h.RetentionTime))
                continue;
            var raw = kind == RunTraceKind.Tic ? h.TotalIonCurrent : h.BasePeakIntensity;
            var value = IntensityConversion.Convert(raw, h, units, calibration);
            if (double.IsNaN(value))
                continue;
            times.Add(h.RetentionTime);
            values.Add(value);
            scans.Add(i);
        }

        var name = $"{(kind == RunTraceKind.Tic ? "TIC" : "BPC")} ({filter.Describe()})";
        return new Trace(name, times.ToArray(), values.ToArray(), scans.ToArray());
    }
}
