using System.Globalization;
using ScottPlot;

namespace Glimpse.Plotting;

/// <summary>
/// One look for every Glimpse plot, sized so a screenshot or saved image can go into a figure: axis text
/// that stays legible when a plot is shrunk to a column width, axis lines and traces heavy enough to
/// survive the same reduction.
/// </summary>
public static class FigureStyle
{
    public const float TickLabelFontSize = 16;
    public const float AxisLabelFontSize = 18;
    public const float LegendFontSize = 15;
    public const float FrameWidth = 2;
    public const float MajorTickLength = 8;
    public const float MajorTickWidth = 2;
    public const float MinorTickLength = 4;
    public const float MinorTickWidth = 1.5f;

    /// <summary>TIC, base peak and assigned-signal traces.</summary>
    public const float TraceWidth = 2;

    /// <summary>Spectrum sticks: unassigned peaks, and peaks assigned to a peptide.</summary>
    public const float StickWidth = 1.5f;
    public const float AssignedStickWidth = 2.5f;
    public const float PeakLabelFontSize = 14;

    /// <summary>XIC line width: heavy for a few traces, hairlines when hundreds overlap and density is the point.</summary>
    public static float XicWidth(int traceCount) => traceCount > 200 ? 1f : traceCount > 20 ? 1.5f : 2.5f;

    /// <summary>
    /// A heatmap's color scale. Peptide counts and assigned fractions use Turbo, the high-contrast map
    /// Skyline-PRISM offers for picking out fine structure in sparse values. Intensity keeps Viridis, on a
    /// linear scale with its brightest cells saturated (see HeatmapScale.IntensitySaturationQuantile).
    /// </summary>
    public static IColormap HeatmapColormap(bool intensity) =>
        intensity ? new ScottPlot.Colormaps.Viridis() : new ScottPlot.Colormaps.Turbo();

    public static void Apply(Plot plot, string xLabel, string yLabel)
    {
        plot.XLabel(xLabel);
        plot.YLabel(yLabel);
        foreach (var axis in new IAxis[] { plot.Axes.Left, plot.Axes.Right, plot.Axes.Bottom, plot.Axes.Top })
            StyleAxis(axis);
        plot.Legend.FontSize = LegendFontSize;
        plot.Grid.MajorLineColor = Colors.Black.WithAlpha(0.08);
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = TickLabel };
        plot.Axes.Right.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = TickLabel };
    }

    /// <summary>Heavy frame and ticks, large text: also used for a heatmap's color bar axis.</summary>
    public static void StyleAxis(IAxis axis)
    {
        axis.Label.FontSize = AxisLabelFontSize;
        axis.Label.Bold = true;
        axis.TickLabelStyle.FontSize = TickLabelFontSize;
        axis.FrameLineStyle.Width = FrameWidth;
        axis.MajorTickStyle.Length = MajorTickLength;
        axis.MajorTickStyle.Width = MajorTickWidth;
        axis.MinorTickStyle.Length = MinorTickLength;
        axis.MinorTickStyle.Width = MinorTickWidth;
    }

    /// <summary>
    /// A mouse position in the units plottables draw in. With <see cref="Plot.ScaleFactor"/> set to the
    /// display scale (so text and lines keep their size on a high-DPI screen), ScottPlot's
    /// GetPixel/GetCoordinates work in device pixels while axes and plottables draw in device pixels
    /// divided by the scale factor.
    /// </summary>
    public static Pixel ToRenderPixel(double deviceX, double deviceY, double scaleFactor) =>
        new(deviceX / scaleFactor, deviceY / scaleFactor);

    /// <summary>
    /// Scientific notation from 1e4 up (TIC values run to 1e10 and plain labels get unreadable), plain
    /// below; always the invariant culture.
    /// </summary>
    public static string TickLabel(double v) =>
        v == 0 ? "0"
        : Math.Abs(v) >= 1e4 || Math.Abs(v) < 1e-2 ? v.ToString("0.##E0", CultureInfo.InvariantCulture)
        : v.ToString("G4", CultureInfo.InvariantCulture);
}
