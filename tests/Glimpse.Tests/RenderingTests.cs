using Glimpse.Plotting;
using ScottPlot;
using SkiaSharp;
using Xunit;

namespace Glimpse.Tests;

/// <summary>What the plottables actually draw, checked pixel by pixel on rendered images.</summary>
[Collection(FigureStyleCollection.Name)]
public class RenderingTests
{
    private static readonly SKColor White = new(255, 255, 255);

    private static (Plot Plot, SKBitmap Bitmap) Render(IPlottable plottable, double xMin, double xMax, double yMin, double yMax)
    {
        var plot = new Plot();
        plot.HideGrid();
        plot.Add.Plottable(plottable);
        plot.Axes.SetLimits(xMin, xMax, yMin, yMax);
        var png = plot.GetImage(800, 400).GetImageBytes();
        return (plot, SKBitmap.Decode(png));
    }

    private static SKColor At(SKBitmap bitmap, Pixel p, int dx = 0, int dy = 0) =>
        bitmap.GetPixel((int)Math.Floor(p.X) + dx, (int)Math.Floor(p.Y) + dy);

    [Fact]
    public void SticksUseTheirColorWidthAndDrawColoredPeaksOnTop()
    {
        var mz = new[] { 100.0, 150.0, 150.0, 200.0 };
        var intensity = new[] { 50.0, 90.0, 40.0, 80.0 };
        var sticks = new StickPlottable(mz, intensity)
        {
            PeakColors = new Color?[] { Colors.Red, null, Colors.Blue, null },
            HighlightWidth = 2,
        };
        var (plot, bmp) = Render(sticks, 0, 400, 0, 100);

        var red = plot.GetPixel(new Coordinates(100, 25));
        var x = (int)MathF.Round((float)red.X);
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(x, (int)red.Y));
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(x + 1, (int)red.Y));      // 2 px wide
        Assert.Equal(White, bmp.GetPixel(x + 3, (int)red.Y));

        var gray = plot.GetPixel(new Coordinates(200, 25));
        var gx = (int)MathF.Round((float)gray.X);
        Assert.Equal(new SKColor(0x9a, 0x9a, 0x9a), bmp.GetPixel(gx, (int)gray.Y)); // the default color, 1 px
        Assert.Equal(White, bmp.GetPixel(gx + 1, (int)gray.Y));
        Assert.Equal(White, bmp.GetPixel(gx, (int)plot.GetPixel(new Coordinates(200, 85)).Y)); // above its top

        // Two peaks at m/z 150: the colored one is drawn over the gray one where they overlap.
        var both = plot.GetPixel(new Coordinates(150, 20));
        Assert.Equal(new SKColor(0, 0, 255), bmp.GetPixel((int)MathF.Round((float)both.X), (int)both.Y));
        var grayOnly = plot.GetPixel(new Coordinates(150, 70));
        Assert.Equal(new SKColor(0x9a, 0x9a, 0x9a), bmp.GetPixel((int)MathF.Round((float)grayOnly.X), (int)grayOnly.Y));
    }

    [Fact]
    public void LabelsAreDrawnAbovePeaksInThePeakColor()
    {
        var sticks = new StickPlottable(new[] { 100.0, 300.0 }, new[] { 50.0, 50.0 })
        {
            PeakColors = new Color?[] { Colors.Red, null },
            PeakLabels = new[] { "y12", "b7" },
            LabelFontSize = 14,
        };
        var (plot, bmp) = Render(sticks, 0, 400, 0, 100);

        bool AnyPixel(Coordinates peakTop, Func<SKColor, bool> test)
        {
            var top = plot.GetPixel(peakTop);
            for (var px = (int)top.X - 15; px <= (int)top.X + 15; px++)
            {
                for (var py = (int)top.Y - 18; py <= (int)top.Y - 2; py++)
                {
                    if (test(bmp.GetPixel(px, py)))
                        return true;
                }
            }
            return false;
        }

        Assert.True(AnyPixel(new Coordinates(100, 50), c => c.Red > 200 && c.Green < 80 && c.Blue < 80), "red label above the red peak");
        Assert.True(AnyPixel(new Coordinates(300, 50), c => c.Red < 200 && Math.Abs(c.Red - c.Green) < 12 && Math.Abs(c.Red - c.Blue) < 12), "gray label above the gray peak");
        Assert.False(AnyPixel(new Coordinates(300, 50), c => c.Red > 200 && c.Green < 80), "no red near the gray peak");
        Assert.Equal(new[] { 0, 1 }, sticks.LastPlacedLabels.OrderBy(i => i));
    }

    [Fact]
    public void TracesAreDrawnAndTheHighlightIsWiderAndOnTop()
    {
        var blue = new TraceLine(new[] { 0.0, 10 }, new[] { 5.0, 5 }, Colors.Blue, "blue");
        var red = new TraceLine(new[] { 0.0, 10 }, new[] { 5.0, 5 }, Colors.Red, "red");
        var other = new TraceLine(new[] { 0.0, 10 }, new[] { 2.0, 2 }, Colors.Green, "green");
        var traces = new MultiTracePlottable(new[] { blue, red, other }) { Highlighted = blue, LineWidth = 1 };
        var (plot, bmp) = Render(traces, 0, 10, 0, 10);

        var onLine = plot.GetPixel(new Coordinates(5, 5));
        Assert.Equal(new SKColor(0, 0, 255), At(bmp, onLine));        // highlighted blue drawn over red
        var green = plot.GetPixel(new Coordinates(5, 2));
        var g = At(bmp, green);
        Assert.True(g.Green > g.Red + 40 && g.Green > g.Blue + 40, $"green trace drawn: {g}");   // anti-aliased
        // Wider than one pixel: blue one pixel off the line too.
        var near = Enumerable.Range(-2, 5).Select(d => At(bmp, onLine, 0, d)).Count(c => c.Blue > 150 && c.Blue > c.Red + 60 && c.Blue > c.Green + 60);   // anti-aliased edges included
        Assert.True(near >= 2, $"highlight width {near}px");
    }

    [Fact]
    public void EachTraceKeepsItsOwnColor()
    {
        // Drawn in order red then green: green's path must not carry red's line with it.
        var red = new TraceLine(new[] { 0.0, 10 }, new[] { 8.0, 8 }, Colors.Red, "red");
        var green = new TraceLine(new[] { 0.0, 10 }, new[] { 2.0, 2 }, Colors.Green, "green");
        var (plot, bmp) = Render(new MultiTracePlottable(new[] { red, green }) { LineWidth = 3 }, 0, 10, 0, 10);
        var r = At(bmp, plot.GetPixel(new Coordinates(5, 8)));
        Assert.True(r.Red > r.Green + 60, $"red trace stays red: {r}");
    }

    [Theory]
    [InlineData(1, 255)]
    [InlineData(20, 255)]
    [InlineData(40, 214)]
    [InlineData(80, 180)]
    [InlineData(300, 150)]
    [InlineData(10_000, 150)]
    public void ManyTracesBecomeTranslucent(int count, int alpha) =>
        Assert.Equal(alpha, MultiTracePlottable.AlphaFor(count));

    [Fact]
    public void AxisLimitsPadSpectraAndIgnoreBadTraceValues()
    {
        var sticks = new StickPlottable(new[] { 100.0, 300.0 }, new[] { 10.0, 50.0 });
        Assert.Equal(new AxisLimits(96, 304, 0, 54), sticks.GetAxisLimits());
        sticks.PeakLabels = new string?[] { "a", null };
        Assert.Equal(59.0, sticks.GetAxisLimits().Top, 9);           // head room for labels
        var narrow = new StickPlottable(new[] { 100.0, 100.5 }, new[] { 1.0, 50.0 }).GetAxisLimits();
        Assert.Equal(99.0, narrow.Left, 9);                            // minimum pad of 1
        Assert.Equal(101.5, narrow.Right, 9);

        var traces = new MultiTracePlottable(new[]
        {
            new TraceLine(Array.Empty<double>(), Array.Empty<double>(), Colors.Red, "empty"),
            new TraceLine(new[] { 2.0, 3.0 }, new[] { double.NaN, 4.0 }, Colors.Red, "a"),
            new TraceLine(new[] { 1.0, 5.0 }, new[] { 4.0, 1.0 }, Colors.Red, "b"),
        });
        Assert.Equal(new AxisLimits(1, 5, 0, 4), traces.GetAxisLimits());
        var flat = new MultiTracePlottable(new[] { new TraceLine(new[] { 1.0, 2.0 }, new[] { 0.0, -1.0 }, Colors.Red, "zero") });
        Assert.Equal(1, flat.GetAxisLimits().Top);                    // nothing positive: unit height
    }

    [Fact]
    public void PathPointEdges()
    {
        static float Px(double v) => (float)(v * 10);
        static float Py(double v) => (float)-v;
        var x = Enumerable.Range(0, 10).Select(i => (double)i).ToArray();
        var y = x.Select(v => v * v).ToArray();
        // Exactly two points per pixel column (10 points over 5 px... with span 45 px here, none decimated).
        Assert.Equal(10, MultiTracePlottable.PathPoints(x, y, 0, 9, Px, Py).Count);
        // 10 points over a 5-pixel span: exactly 2 per pixel - still drawn point by point.
        Assert.Equal(10, MultiTracePlottable.PathPoints(x, y, 0, 9, v => (float)(v * 5 / 9), Py).Count);
        // More than 2 per pixel: column centers at +0.5, lowest then highest value, the last column flushed.
        var dense = MultiTracePlottable.PathPoints(x, y, 0, 9, v => (float)(v * 2 / 9), Py);
        Assert.All(dense, p => Assert.Equal(0.5f, p.X - MathF.Floor(p.X)));
        Assert.Equal(2.5f, dense[^1].X);                               // pixel x = 2 for the last point (9 * 2 / 9)
        Assert.Equal(-81f, dense.Min(p => p.Y));
        Assert.Equal(0f, dense.Max(p => p.Y));
        // Visible range wholly before the data: just the first point, so the line can run in from the edge.
        Assert.Single(MultiTracePlottable.PathPoints(x, y, -10, -5, Px, Py));
        Assert.Single(MultiTracePlottable.PathPoints(x, y, 20, 30, Px, Py));
    }

    [Fact]
    public void NearestLookupsRespectRangesAndLimits()
    {
        var a = new TraceLine(new[] { 0.0, 1.0 }, new[] { 5.0, 5.0 }, Colors.Red, "a");
        var b = new TraceLine(new[] { 2.0, 3.0 }, new[] { 5.0, 5.0 }, Colors.Blue, "b");
        var traces = new MultiTracePlottable(new[] { a, b });
        var plot = new Plot();
        plot.Add.Plottable(traces);
        plot.Axes.SetLimits(0, 3, 0, 10);
        plot.GetImage(300, 200);
        Assert.Same(a, traces.Nearest(plot.GetPixel(new Coordinates(1.0, 5))));
        Assert.Same(b, traces.Nearest(plot.GetPixel(new Coordinates(2.0, 5))));
        Assert.Null(traces.Nearest(plot.GetPixel(new Coordinates(1.5, 5))));     // between the traces: neither covers 1.5
        Assert.Same(a, traces.Nearest(plot.GetPixel(new Coordinates(0.5, 5.2)), maxPixels: 50));

        var sticks = new StickPlottable(new[] { 100.0, 106.0, 120.0 }, new[] { 1.0, 1.0, 1.0 });
        Assert.Equal(1, sticks.NearestPeak(112.0, pixelsPerMz: 1));    // exactly 6 px from 106: inclusive
        Assert.Equal(-1, sticks.NearestPeak(112.9, pixelsPerMz: 1, maxPixels: 6.5));
        Assert.Equal(2, sticks.NearestPeak(119.0, pixelsPerMz: 1));
        Assert.Equal(0, sticks.NearestPeak(99.5, pixelsPerMz: 1));
        Assert.Equal(2, sticks.NearestPeak(125.0, pixelsPerMz: 1));
    }
}
