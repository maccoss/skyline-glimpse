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
}
