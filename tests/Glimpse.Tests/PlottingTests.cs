using Glimpse.Core.Document;
using Glimpse.Plotting;
using ScottPlot;
using SkiaSharp;
using Xunit;

namespace Glimpse.Tests;

public class PlottingTests
{
    [Fact]
    public void LabelsArePlacedMostImportantFirstWithoutOverlap()
    {
        var bounds = new SKRect(0, 0, 100, 100);
        var boxes = new[]
        {
            new SKRect(10, 10, 30, 20),   // kept
            new SKRect(20, 15, 40, 25),   // overlaps the first: dropped
            new SKRect(50, 10, 70, 20),   // kept
            new SKRect(90, 10, 110, 20),  // past the right edge: dropped
            new SKRect(-5, 10, 5, 20),    // past the left edge: dropped
            new SKRect(30, -5, 40, 5),    // above the top: dropped
            new SKRect(30.5f, 10, 45, 20),// touches nothing kept: kept
        };
        Assert.Equal(new[] { 0, 2, 6 }, StickPlottable.PlaceLabels(boxes, bounds));
        Assert.Empty(StickPlottable.PlaceLabels(Array.Empty<SKRect>(), bounds));
    }

    [Fact]
    public void PathPointsKeepEveryPointWhenSparse()
    {
        var x = new[] { 0.0, 1, 2, 3, 4, 5 };
        var y = new[] { 1.0, double.NaN, 3, 4, 5, 6 };
        var points = MultiTracePlottable.PathPoints(x, y, 1.5, 3.5, v => (float)(v * 100), v => (float)(-v));
        // One point beyond each visible edge (1 and 4); 1 is NaN and skipped.
        Assert.Equal(new[] { 200f, 300f, 400f }, points.Select(p => p.X));
        Assert.Equal(new[] { -3f, -4f, -5f }, points.Select(p => p.Y));
        Assert.Empty(MultiTracePlottable.PathPoints(Array.Empty<double>(), Array.Empty<double>(), 0, 1, v => 0, v => 0));
        Assert.Empty(MultiTracePlottable.PathPoints(x, y, 10, 20, v => (float)v, v => (float)v).Where(p => p.X > 5));
    }

    [Fact]
    public void PathPointsDecimateDenseTracesToTheirEnvelope()
    {
        const int n = 100_000;
        var x = Enumerable.Range(0, n).Select(i => i / (double)n).ToArray();
        var y = Enumerable.Range(0, n).Select(i => Math.Sin(i * 0.01) * 50 + (i == 54_321 ? 400 : 0)).ToArray();
        // 100 pixel columns across [0, 1].
        var points = MultiTracePlottable.PathPoints(x, y, 0, 1, v => (float)(v * 100), v => (float)(-v));
        Assert.InRange(points.Count, 100, 202);
        // The envelope keeps the extremes: the spike and the trough survive decimation.
        Assert.Contains(points, p => Math.Abs(p.Y + 450) < 1e-3 || Math.Abs(p.Y + 400 + 50 * Math.Sin(543.21)) < 1);
        Assert.Equal(-y.Max(), points.Min(p => p.Y), 3);
        Assert.Equal(-y.Min(), points.Max(p => p.Y), 3);
    }

    [Fact]
    public void NearestTraceIsFoundInPixelSpace()
    {
        var a = new TraceLine(new[] { 0.0, 1, 2 }, new[] { 0.0, 10, 0 }, Colors.Red, "a");
        var b = new TraceLine(new[] { 0.0, 1, 2 }, new[] { 0.0, 2, 0 }, Colors.Blue, "b");
        var traces = new MultiTracePlottable(new[] { a, b });
        var plot = new Plot();
        plot.Add.Plottable(traces);
        plot.Axes.SetLimits(0, 2, 0, 12);
        plot.GetImage(400, 300);                   // lays out the axes

        var peakOfA = plot.GetPixel(new Coordinates(1, 10));
        Assert.Same(a, traces.Nearest(peakOfA));
        var peakOfB = plot.GetPixel(new Coordinates(1, 2));
        Assert.Same(b, traces.Nearest(peakOfB));
        Assert.Null(traces.Nearest(plot.GetPixel(new Coordinates(1, 6))));   // between them, far from both
        Assert.Equal(new AxisLimits(0, 2, 0, 10), traces.GetAxisLimits());

        traces.Highlighted = a;
        plot.GetImage(400, 300);                   // renders the highlight path without error
        Assert.False(new MultiTracePlottable(Array.Empty<TraceLine>()).GetAxisLimits().HasArea);
    }

    [Fact]
    public void CrowdedLabelsAreThinnedAndTheTallestWins()
    {
        var mz = Enumerable.Range(0, 40).Select(i => 500 + i * 0.05).ToArray();
        var intensity = mz.Select((_, i) => i == 17 ? 100.0 : 10.0 + i % 5).ToArray();
        var sticks = new StickPlottable(mz, intensity)
        {
            PeakLabels = mz.Select(m => (string?)$"y{(int)m}").ToArray(),
            PeakColors = mz.Select((_, i) => (Color?)(i % 2 == 0 ? Colors.Red : null)).ToArray(),
        };
        var plot = new Plot();
        plot.Add.Plottable(sticks);
        plot.Axes.SetLimits(499, 503, 0, 120);
        plot.GetImage(600, 400);
        Assert.Contains(17, sticks.LastPlacedLabels);
        Assert.Equal(17, sticks.LastPlacedLabels[0]);
        Assert.InRange(sticks.LastPlacedLabels.Count, 1, 39);

        Assert.Equal(17, sticks.NearestPeak(500.85, pixelsPerMz: 100));
        Assert.Equal(-1, sticks.NearestPeak(450, pixelsPerMz: 100));
        Assert.Equal(-1, new StickPlottable(Array.Empty<double>(), Array.Empty<double>()).NearestPeak(1, 1));
        Assert.False(new StickPlottable(Array.Empty<double>(), Array.Empty<double>()).GetAxisLimits().HasArea);
    }

    [Fact]
    public void PeptideColorsAreStableAndSpread()
    {
        var protein = new DocProtein("sp|P1|X", null, null, null);
        var peptide = new DocPeptide(protein, "AAK", "AAK", null);
        Assert.Equal(PeptideColors.For(peptide), PeptideColors.For(new DocPeptide(protein, "AAK", "AAK", null)));
        Assert.Equal(PeptideColors.ForKey("sp|P1|X/AAK"), PeptideColors.For(peptide));
        Assert.Equal(PeptideColors.ForKey("sp|P1|X"), PeptideColors.For(protein));
        var distinct = Enumerable.Range(0, 200).Select(i => PeptideColors.ForKey("pep" + i)).Distinct().Count();
        Assert.True(distinct >= 15, $"only {distinct} colors used");
        Assert.DoesNotContain(PeptideColors.Shared, Enumerable.Range(0, 200).Select(i => PeptideColors.ForKey("pep" + i)));
    }
}

public class HoverAndColorTests
{
    [Fact]
    public void SegmentDistanceMeasuresToTheLineNotItsEnds()
    {
        Assert.Equal(0, MultiTracePlottable.SegmentDistance(new Pixel(5, 50), 5, 0, 5, 100), 9);     // on a vertical line
        Assert.Equal(3, MultiTracePlottable.SegmentDistance(new Pixel(8, 50), 5, 0, 5, 100), 9);     // beside it
        Assert.Equal(4, MultiTracePlottable.SegmentDistance(new Pixel(3, -4), 0, 0, 10, 0), 9);      // beside a horizontal segment
        Assert.Equal(5, MultiTracePlottable.SegmentDistance(new Pixel(-3, 4), 0, 0, 10, 0), 9);      // past an end: to that end
        Assert.Equal(5, MultiTracePlottable.SegmentDistance(new Pixel(3, 4), 0, 0, 0, 0), 9);        // a degenerate segment
    }

    [Fact]
    public void HoveringTheSideOfAPeakFindsIt()
    {
        // A spike: points far apart in y, close in x. The pointer is on its rising side, far from any point.
        var spike = new TraceLine(new[] { 0.0, 1.0, 2.0 }, new[] { 0.0, 100.0, 0.0 }, Colors.Red, "spike");
        var traces = new MultiTracePlottable(new[] { spike });
        var plot = new Plot();
        plot.Add.Plottable(traces);
        plot.Axes.SetLimits(0, 2, 0, 100);
        plot.GetImage(400, 300);
        var midSide = plot.GetPixel(new Coordinates(0.5, 50));
        Assert.Same(spike, traces.Nearest(midSide));
        Assert.Null(traces.Nearest(plot.GetPixel(new Coordinates(1.5, 95))));   // well off the line
        var single = new MultiTracePlottable(new[] { new TraceLine(new[] { 1.0 }, new[] { 50.0 }, Colors.Blue, "one") });
        plot.Clear();
        plot.Add.Plottable(single);
        plot.GetImage(400, 300);
        Assert.NotNull(single.Nearest(plot.GetPixel(new Coordinates(1.0, 50))));
    }

    [Fact]
    public void SelectionColorsAreDistinctUpToThePaletteSize()
    {
        var protein = new Glimpse.Core.Document.DocProtein("P", null, null, null);
        var peptides = Enumerable.Range(0, PeptideColors.PaletteSize)
            .Select(i => new Glimpse.Core.Document.DocPeptide(protein, "PEP" + i, "PEP" + i, null)).ToList();
        var colors = PeptideColors.ForSelection(peptides);
        Assert.Equal(peptides.Count, colors.Values.Distinct().Count());
        // Strong hues first: the first two selected peptides are Tableau blue and orange. Deterministic.
        Assert.Equal(Color.FromHex("#1f77b4"), colors[peptides[0]]);
        Assert.Equal(Color.FromHex("#ff7f0e"), colors[peptides[1]]);
        Assert.Equal(colors, PeptideColors.ForSelection(peptides));
        // Beyond the palette, peptides simply take their usual color.
        var extra = new Glimpse.Core.Document.DocPeptide(protein, "EXTRA", "EXTRA", null);
        var more = PeptideColors.ForSelection(peptides.Append(extra).Append(peptides[0]));
        Assert.Equal(PeptideColors.For(extra), more[extra]);
        Assert.Equal(peptides.Count + 1, more.Count);
    }
}

public class FigureStyleTests
{
    [Fact]
    public void PlotsAreStyledForFigures()
    {
        var plot = new Plot();
        FigureStyle.Apply(plot, "Retention time (min)", "Intensity");
        Assert.Equal("Retention time (min)", plot.Axes.Bottom.Label.Text);
        Assert.Equal("Intensity", plot.Axes.Left.Label.Text);
        foreach (var axis in new IAxis[] { plot.Axes.Left, plot.Axes.Right, plot.Axes.Bottom, plot.Axes.Top })
        {
            Assert.Equal(18, axis.Label.FontSize);
            Assert.True(axis.Label.Bold);
            Assert.Equal(16, axis.TickLabelStyle.FontSize);
            Assert.Equal(2, axis.FrameLineStyle.Width);
            Assert.Equal(8, axis.MajorTickStyle.Length);
            Assert.Equal(2, axis.MajorTickStyle.Width);
            Assert.Equal(4, axis.MinorTickStyle.Length);
            Assert.Equal(1.5f, axis.MinorTickStyle.Width);
        }
        Assert.Equal(15, plot.Legend.FontSize);
        // Both value axes use the scientific tick labels.
        Assert.Equal("1.5E4", ((ScottPlot.TickGenerators.NumericAutomatic)plot.Axes.Left.TickGenerator).LabelFormatter(15000));
        Assert.Equal("1.5E4", ((ScottPlot.TickGenerators.NumericAutomatic)plot.Axes.Right.TickGenerator).LabelFormatter(15000));
        plot.GetImage(600, 400);    // renders with the style

        // Traces: heavy for a few, hairlines for hundreds.
        Assert.Equal(2.5f, FigureStyle.XicWidth(1));
        Assert.Equal(2.5f, FigureStyle.XicWidth(20));
        Assert.Equal(1.5f, FigureStyle.XicWidth(21));
        Assert.Equal(1.5f, FigureStyle.XicWidth(200));
        Assert.Equal(1f, FigureStyle.XicWidth(201));
        Assert.IsType<ScottPlot.Colormaps.Viridis>(FigureStyle.HeatmapColormap(intensity: true));
        Assert.IsType<ScottPlot.Colormaps.Turbo>(FigureStyle.HeatmapColormap(intensity: false));
        Assert.Equal(2f, FigureStyle.TraceWidth);

        // Text and lines grow with the plot's area, from 1000 x 400 up, at most 2.5 times.
        Assert.Equal(1.0, FigureStyle.SizeScale(1000, 400));
        Assert.Equal(1.0, FigureStyle.SizeScale(700, 330));           // smaller: never shrinks
        Assert.Equal(2.0, FigureStyle.SizeScale(2000, 800), 9);       // twice as wide and tall
        Assert.Equal(1.5, FigureStyle.SizeScale(2250, 400), 9);       // only wider: by the area
        Assert.Equal(1.5, FigureStyle.SizeScale(1000, 900), 9);       // only taller: likewise
        Assert.Equal(2.5, FigureStyle.SizeScale(4000, 1600));         // capped
        Assert.Equal(1.0, FigureStyle.SizeScale(0, 400));             // not laid out yet
        Assert.Equal(1.0, FigureStyle.SizeScale(1000, double.NaN));
        Assert.Equal(1.0, FigureStyle.SizeScale(double.PositiveInfinity, 400));

        var sticks = new StickPlottable(new[] { 100.0 }, new[] { 1.0 });
        Assert.Equal(1.5f, sticks.StickWidth);
        Assert.Equal(2.5f, sticks.HighlightWidth);
        Assert.Equal(14f, sticks.LabelFontSize);
    }

    [Fact]
    public void TickLabelsAreInvariantWhateverTheCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5E4", FigureStyle.TickLabel(15000));
            Assert.Equal("2.5", FigureStyle.TickLabel(2.5));
            Assert.Equal("0", FigureStyle.TickLabel(0));
            Assert.Equal("5E-3", FigureStyle.TickLabel(0.005));
            Assert.Equal("9999", FigureStyle.TickLabel(9999));
            Assert.Equal("-2E4", FigureStyle.TickLabel(-20000));
            Assert.Equal("0.01", FigureStyle.TickLabel(0.01));
            Assert.Equal("1E4", FigureStyle.TickLabel(10000));      // scientific from 1e4 itself
            Assert.Equal("1235", FigureStyle.TickLabel(1234.5678)); // four significant figures below it
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }
}

public class HighDpiTests
{
    [Fact]
    public void HoveringFindsATraceOnAScaledPlot()
    {
        // A 200% display: ScaleFactor 2, so GetPixel answers in device pixels and plottables draw in half that.
        var trace = new TraceLine(new[] { 0.0, 1, 2 }, new[] { 0.0, 10, 0 }, Colors.Red, "a");
        var traces = new MultiTracePlottable(new[] { trace });
        var plot = new Plot { ScaleFactor = 2 };
        plot.Add.Plottable(traces);
        plot.Axes.SetLimits(0, 2, 0, 12);
        plot.GetImage(800, 600);

        var device = plot.GetPixel(new Coordinates(1, 10));     // where the mouse is, in device pixels
        Assert.Same(trace, traces.Nearest(FigureStyle.ToRenderPixel(device.X, device.Y, plot.ScaleFactor)));
        // Unconverted, the same position misses the trace entirely: the bug this guards against.
        Assert.Null(traces.Nearest(device));
        Assert.Equal(new Pixel(50, 75), FigureStyle.ToRenderPixel(100, 150, 2));
        Assert.Equal(new Pixel(100, 150), FigureStyle.ToRenderPixel(100, 150, 1));
    }
}
