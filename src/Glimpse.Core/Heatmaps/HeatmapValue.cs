namespace Glimpse.Core.Heatmaps;

/// <summary>What a heatmap cell shows, in the order of the window's Value list.</summary>
public enum HeatmapValue
{
    Intensity,
    AssignedFraction,
    PeptidesPerSpectrum,
}

public static class HeatmapScale
{
    /// <summary>
    /// Whether cells are drawn as log10(1 + value). Only intensity, which spans orders of magnitude, takes
    /// the log scale the user asks for. The assigned fraction runs 0-1, and peptides per spectrum counts a
    /// few to a few tens (Skyline-PRISM draws it linear too): both are always linear.
    /// </summary>
    public static bool IsLog(HeatmapValue value, bool logRequested) =>
        value == HeatmapValue.Intensity && logRequested;

    /// <summary>Whether the log-scale choice applies to this value (the window greys it out otherwise).</summary>
    public static bool OffersLog(HeatmapValue value) => value == HeatmapValue.Intensity;

    /// <summary>
    /// On a linear intensity heatmap, the share of cells with signal drawn below the top of the color scale.
    /// A handful of very intense peaks would otherwise set the scale and leave everything else dark: the top
    /// color is set at this quantile instead, and the brighter cells saturate.
    /// </summary>
    public const double IntensitySaturationQuantile = 0.90;

    /// <summary>
    /// The value at quantile <paramref name="q"/> (0-1) of the grid's cells that have signal (finite and
    /// above zero), the nearest-rank way; NaN when no cell has signal.
    /// </summary>
    public static double Quantile(float[,] values, double q)
    {
        var signal = new List<float>(values.Length);
        foreach (var v in values)
        {
            if (v > 0 && float.IsFinite(v))
                signal.Add(v);
        }
        if (signal.Count == 0)
            return double.NaN;
        signal.Sort();
        var rank = (int)Math.Ceiling(Math.Clamp(q, 0, 1) * signal.Count) - 1;
        return signal[Math.Clamp(rank, 0, signal.Count - 1)];
    }
}
