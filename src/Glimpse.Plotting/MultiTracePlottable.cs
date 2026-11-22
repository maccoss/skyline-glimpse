using ScottPlot;
using SkiaSharp;

namespace Glimpse.Plotting;

/// <summary>One line of a <see cref="MultiTracePlottable"/>.</summary>
public sealed record TraceLine(double[] X, double[] Y, Color Color, string Name, object? Tag = null);

/// <summary>
/// Any number of chromatogram traces in one plottable, drawn in a single SkiaSharp pass. Built for
/// "show me all 10,000 selected peptides": each trace is clipped to the visible RT range and, when it
/// has more points than pixels, reduced to the min and max of each pixel column, so drawing cost tracks
/// screen width rather than data size. Hover lookup finds the nearest trace in pixel space.
/// </summary>
public sealed class MultiTracePlottable : IPlottable
{
    private readonly IReadOnlyList<TraceLine> _traces;
    private readonly AxisLimits _limits;

    public MultiTracePlottable(IReadOnlyList<TraceLine> traces)
    {
        _traces = traces;
        double xMin = double.PositiveInfinity, xMax = double.NegativeInfinity, yMax = 0;
        foreach (var t in traces)
        {
            if (t.X.Length == 0)
                continue;
            xMin = Math.Min(xMin, t.X[0]);
            xMax = Math.Max(xMax, t.X[^1]);
            foreach (var y in t.Y)
            {
                if (double.IsFinite(y) && y > yMax)
                    yMax = y;
            }
        }
        _limits = double.IsFinite(xMin) ? new AxisLimits(xMin, xMax, 0, yMax <= 0 ? 1 : yMax) : AxisLimits.NoLimits;
    }

    public bool IsVisible { get; set; } = true;

    public IAxes Axes { get; set; } = new Axes();

    public IEnumerable<LegendItem> LegendItems => LegendItem.None;

    public IReadOnlyList<TraceLine> Traces => _traces;

    /// <summary>A trace drawn on top, wider and opaque (the hovered or focused one).</summary>
    public TraceLine? Highlighted { get; set; }

    public float LineWidth { get; set; } = 1f;

    public AxisLimits GetAxisLimits() => _limits;

    public void Render(RenderPack rp)
    {
        // Many overlapping traces read as a density when each is partly transparent.
        var alpha = AlphaFor(_traces.Count);
        using var paint = new SKPaint { IsAntialias = _traces.Count < 500, Style = SKPaintStyle.Stroke, StrokeWidth = LineWidth };
        using var path = new SKPath();
        foreach (var t in _traces)
        {
            if (ReferenceEquals(t, Highlighted))
                continue;
            paint.Color = new SKColor(t.Color.R, t.Color.G, t.Color.B, alpha);
            path.Reset();
            BuildPath(t, path);
            rp.Canvas.DrawPath(path, paint);
        }
        if (Highlighted is { } h)
        {
            paint.Color = new SKColor(h.Color.R, h.Color.G, h.Color.B, 255);
            paint.StrokeWidth = LineWidth + 1.5f;
            paint.IsAntialias = true;
            path.Reset();
            BuildPath(h, path);
            rp.Canvas.DrawPath(path, paint);
        }
    }

    /// <summary>
    /// Opacity of each trace: opaque up to 20 traces, then falling as 1/sqrt(count) so overlapping
    /// traces read as a density, but never below 40 of 255.
    /// </summary>
    internal static byte AlphaFor(int traceCount) =>
        (byte)Math.Clamp(255 * Math.Sqrt(20.0 / Math.Max(20, traceCount)), 40, 255);

    private void BuildPath(TraceLine t, SKPath path)
    {
        var points = PathPoints(t.X, t.Y, Axes.XAxis.Min, Axes.XAxis.Max, x => Axes.GetPixelX(x), y => Axes.GetPixelY(y));
        for (var i = 0; i < points.Count; i++)
        {
            if (i == 0)
                path.MoveTo(points[i]);
            else
                path.LineTo(points[i]);
        }
    }

    /// <summary>
    /// The pixel points to draw for one trace over the visible range [<paramref name="xMin"/>,
    /// <paramref name="xMax"/>]: one point past each edge so the line runs off the plot, non-finite values
    /// skipped, and, when there are more than two points per pixel column, each column reduced to its
    /// lowest and highest value (so the drawn envelope is exact while the point count tracks plot width).
    /// </summary>
    internal static List<SKPoint> PathPoints(double[] x, double[] y, double xMin, double xMax, Func<double, float> pixelX, Func<double, float> pixelY)
    {
        var points = new List<SKPoint>();
        if (x.Length == 0)
            return points;
        var start = Math.Max(0, LowerBound(x, xMin) - 1);
        var end = Math.Min(x.Length - 1, LowerBound(x, xMax));
        if (end < start)
            return points;

        var pixelSpan = Math.Abs(pixelX(xMax) - pixelX(xMin));
        if (end - start + 1 <= 2 * Math.Max(1, pixelSpan))
        {
            for (var i = start; i <= end; i++)
            {
                if (double.IsFinite(y[i]))
                    points.Add(new SKPoint(pixelX(x[i]), pixelY(y[i])));
            }
            return points;
        }

        var column = int.MinValue;
        float lo = 0, hi = 0;
        void Flush()
        {
            if (column == int.MinValue)
                return;
            points.Add(new SKPoint(column + 0.5f, lo));
            if (hi != lo)
                points.Add(new SKPoint(column + 0.5f, hi));
        }
        for (var i = start; i <= end; i++)
        {
            if (!double.IsFinite(y[i]))
                continue;
            var px = pixelX(x[i]);
            var py = pixelY(y[i]);
            var col = (int)MathF.Floor(px);
            if (col != column)
            {
                Flush();
                column = col;
                lo = hi = py;
            }
            else
            {
                // Pixel y grows downward: "lo" (the lowest value) is the largest pixel y.
                lo = Math.Max(lo, py);
                hi = Math.Min(hi, py);
            }
        }
        Flush();
        return points;
    }

    /// <summary>The trace whose line passes nearest the pixel position, within <paramref name="maxPixels"/>.</summary>
    public TraceLine? Nearest(Pixel pixel, double maxPixels = 8)
    {
        TraceLine? best = null;
        var bestDistance = maxPixels;
        var x = Axes.GetCoordinateX(pixel.X);
        foreach (var t in _traces)
        {
            // Range check in pixels, with the same tolerance as the distance: pointing at a trace's
            // first or last point must find it despite rounding in the pixel-to-data conversion.
            if (t.X.Length == 0 || pixel.X < Axes.GetPixelX(t.X[0]) - maxPixels || pixel.X > Axes.GetPixelX(t.X[^1]) + maxPixels)
                continue;
            // Distance to the drawn line, not just its points: a chromatographic peak is a steep line
            // between two points far apart in y, and pointing at its side must find it. Check the
            // segments either side of the pointer.
            var i = LowerBound(t.X, x);
            for (var j = Math.Max(1, i - 1); j <= Math.Min(t.X.Length - 1, i + 1); j++)
            {
                if (!double.IsFinite(t.Y[j - 1]) || !double.IsFinite(t.Y[j]))
                    continue;
                var d = SegmentDistance(pixel,
                    Axes.GetPixelX(t.X[j - 1]), Axes.GetPixelY(t.Y[j - 1]),
                    Axes.GetPixelX(t.X[j]), Axes.GetPixelY(t.Y[j]));
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = t;
                }
            }
            if (t.X.Length == 1 && double.IsFinite(t.Y[0]))
            {
                var d = SegmentDistance(pixel, Axes.GetPixelX(t.X[0]), Axes.GetPixelY(t.Y[0]), Axes.GetPixelX(t.X[0]), Axes.GetPixelY(t.Y[0]));
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = t;
                }
            }
        }
        return best;
    }

    /// <summary>Distance in pixels from <paramref name="p"/> to the segment (x1, y1)-(x2, y2).</summary>
    internal static double SegmentDistance(Pixel p, double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared > 0 ? Math.Clamp(((p.X - x1) * dx + (p.Y - y1) * dy) / lengthSquared, 0, 1) : 0;
        double cx = x1 + t * dx - p.X, cy = y1 + t * dy - p.Y;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    private static int LowerBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (sorted[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
