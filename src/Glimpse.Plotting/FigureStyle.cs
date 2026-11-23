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
    /// <summary>The sizes in use: the user's settings, or the defaults below.</summary>
    public static FigureSettings Current { get; set; } = FigureSettings.Default;

    public static float TickLabelFontSize => Current.TickLabelFontSize;
    public static float AxisLabelFontSize => Current.AxisLabelFontSize;
    public static float LegendFontSize => Current.LegendFontSize;
    public static float FrameWidth => Current.FrameWidth;
    public static float MajorTickLength => Current.MajorTickLength;
    public static float MajorTickWidth => Current.MajorTickWidth;
    public static float MinorTickLength => Current.MinorTickLength;
    public static float MinorTickWidth => Current.MinorTickWidth;

    /// <summary>TIC, base peak and assigned-signal traces.</summary>
    public static float TraceWidth => Current.TraceWidth;

    /// <summary>Spectrum sticks: unassigned peaks, and peaks assigned to a peptide.</summary>
    public static float StickWidth => Current.StickWidth;
    public static float AssignedStickWidth => Current.AssignedStickWidth;
    public static float PeakLabelFontSize => Current.PeakLabelFontSize;

    /// <summary>
    /// XIC line width: the setting for a few traces, three fifths of it past 20 traces and two fifths past
    /// 200, where hundreds overlap and density is the point (2.5, 1.5 and 1 with the defaults).
    /// </summary>
    public static float XicWidth(int traceCount) =>
        traceCount > 200 ? Current.XicWidth * 2 / 5 : traceCount > 20 ? Current.XicWidth * 3 / 5 : Current.XicWidth;

    /// <summary>
    /// A heatmap's color scale. Peptide counts and assigned fractions use Turbo, the high-contrast map
    /// Skyline-PRISM offers for picking out fine structure in sparse values. Intensity keeps Viridis, on a
    /// linear scale with its brightest cells saturated (see HeatmapScale.IntensitySaturationQuantile).
    /// </summary>
    public static IColormap HeatmapColormap(bool intensity) =>
        intensity ? new ScottPlot.Colormaps.Viridis() : new ScottPlot.Colormaps.Turbo();

    /// <summary>The plot size, in device-independent pixels, the sizes above are chosen for.</summary>
    public const double ReferenceWidth = 1000, ReferenceHeight = 400;

    /// <summary>The most text and lines grow, however large the plot.</summary>
    public const double MaxSizeScale = 2.5;

    /// <summary>
    /// How much larger than the sizes above to draw text and lines in a plot of this size (device-independent
    /// pixels), so a large window shows the same figure scaled up rather than small text and hairlines on a
    /// big canvas. It follows the plot's area (the geometric mean of the width and height ratios to
    /// <see cref="ReferenceWidth"/> x <see cref="ReferenceHeight"/>), never below 1, at most
    /// <see cref="MaxSizeScale"/>.
    /// </summary>
    public static double SizeScale(double width, double height)
    {
        if (!Current.ScaleWithPlotSize)
            return 1;
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height))
            return 1;
        return Math.Clamp(Math.Sqrt(width / ReferenceWidth * (height / ReferenceHeight)), 1, MaxSizeScale);
    }

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
