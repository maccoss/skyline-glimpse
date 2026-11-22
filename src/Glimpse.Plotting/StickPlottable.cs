using ScottPlot;
using SkiaSharp;

namespace Glimpse.Plotting;

/// <summary>
/// A centroided spectrum drawn as vertical sticks, with an optional color and label per peak. Drawn
/// directly with SkiaSharp: peaks outside the visible m/z range are skipped, and labels are placed most
/// intense first, skipping any that would overlap a label already drawn, so zooming in reveals more.
/// </summary>
public sealed class StickPlottable : IPlottable
{
    private readonly double[] _mz;
    private readonly double[] _intensity;
    private readonly double _maxIntensity;

    /// <param name="mz">Peak m/z, ascending.</param>
    /// <param name="intensity">Peak heights, parallel to <paramref name="mz"/>.</param>
    public StickPlottable(double[] mz, double[] intensity)
    {
        _mz = mz;
        _intensity = intensity;
        _maxIntensity = intensity.Length == 0 ? 1 : Math.Max(1e-12, intensity.Max());
    }

    public bool IsVisible { get; set; } = true;

    public IAxes Axes { get; set; } = new Axes();

    public IEnumerable<LegendItem> LegendItems => LegendItem.None;

    /// <summary>Color for peaks without an entry in <see cref="PeakColors"/>.</summary>
    public Color DefaultColor { get; set; } = Color.FromHex("#9a9a9a");

    /// <summary>Optional per-peak colors (same length as the peak arrays); null entries use the default.</summary>
    public Color?[]? PeakColors { get; set; }

    /// <summary>Optional per-peak labels; null entries are not labeled.</summary>
    public string?[]? PeakLabels { get; set; }

    /// <summary>Line width in pixels for colored (assigned) peaks.</summary>
    public float HighlightWidth { get; set; } = FigureStyle.AssignedStickWidth;

    /// <summary>Line width in pixels for peaks without a color.</summary>
    public float StickWidth { get; set; } = FigureStyle.StickWidth;

    public float LabelFontSize { get; set; } = FigureStyle.PeakLabelFontSize;

    public AxisLimits GetAxisLimits()
    {
        if (_mz.Length == 0)
            return AxisLimits.NoLimits;
        var pad = Math.Max(1, (_mz[^1] - _mz[0]) * 0.02);
        // Head room for labels above the tallest peak.
        return new AxisLimits(_mz[0] - pad, _mz[^1] + pad, 0, _maxIntensity * (PeakLabels is null ? 1.08 : 1.18));
    }

    public void Render(RenderPack rp)
    {
        if (_mz.Length == 0)
            return;
        var xMin = Axes.XAxis.Min;
        var xMax = Axes.XAxis.Max;
        var y0 = Axes.GetPixelY(0);
        var first = LowerBound(xMin);

        using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke };
        // Default (unassigned) peaks first so colored ones draw on top.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = first; i < _mz.Length && _mz[i] <= xMax; i++)
            {
                var color = PeakColors?[i];
                if ((pass == 0) == color.HasValue)
                    continue;
                var c = color ?? DefaultColor;
                paint.Color = new SKColor(c.R, c.G, c.B, c.A);
                paint.StrokeWidth = color.HasValue ? HighlightWidth : StickWidth;
                var x = MathF.Round(Axes.GetPixelX(_mz[i])) + 0.5f;
                rp.Canvas.DrawLine(x, y0, x, Axes.GetPixelY(_intensity[i]), paint);
            }
        }

        if (PeakLabels is not null)
            RenderLabels(rp, first, xMax);
    }

    private void RenderLabels(RenderPack rp, int first, double xMax)
    {
        var labeled = new List<int>();
        for (var i = first; i < _mz.Length && _mz[i] <= xMax; i++)
        {
            if (PeakLabels![i] is not null)
                labeled.Add(i);
        }
        labeled.Sort((a, b) => _intensity[b].CompareTo(_intensity[a]));

        using var font = new SKFont(SKTypeface.Default, LabelFontSize);
        using var text = new SKPaint { IsAntialias = true };
        var data = rp.DataRect;
        var boxes = labeled.Select(i =>
        {
            var width = font.MeasureText(PeakLabels![i]!);
            var x = Axes.GetPixelX(_mz[i]);
            var top = Axes.GetPixelY(_intensity[i]) - 3;
            return new SKRect(x - width / 2 - 1, top - (LabelFontSize + 1), x + width / 2 + 1, top);
        }).ToList();
        var placed = PlaceLabels(boxes, new SKRect(data.Left, data.Top, data.Right, data.Bottom));
        LastPlacedLabels = placed.Select(k => labeled[k]).ToArray();
        foreach (var k in placed)
        {
            var i = labeled[k];
            var c = PeakColors?[i] ?? DefaultColor;
            text.Color = new SKColor(c.R, c.G, c.B, 255);
            rp.Canvas.DrawText(PeakLabels![i]!, boxes[k].Left + 1, boxes[k].Bottom - 2, font, text);
        }
    }

    /// <summary>Indices of the peaks whose labels were drawn in the last render (for tests and hover).</summary>
    public IReadOnlyList<int> LastPlacedLabels { get; private set; } = Array.Empty<int>();

    /// <summary>
    /// Greedy label placement: boxes are offered in priority order (most intense peak first); a box is
    /// kept when it lies inside <paramref name="bounds"/> and overlaps no box already kept. Returns the
    /// positions (into <paramref name="boxes"/>) of the kept boxes.
    /// </summary>
    internal static List<int> PlaceLabels(IReadOnlyList<SKRect> boxes, SKRect bounds)
    {
        var kept = new List<int>();
        for (var k = 0; k < boxes.Count; k++)
        {
            var box = boxes[k];
            if (box.Top < bounds.Top || box.Left < bounds.Left || box.Right > bounds.Right)
                continue;
            if (kept.Any(j => boxes[j].IntersectsWith(box)))
                continue;
            kept.Add(k);
        }
        return kept;
    }

    /// <summary>Index of the peak nearest <paramref name="mz"/> in pixel terms, or -1 when none is within <paramref name="maxPixels"/>.</summary>
    public int NearestPeak(double mz, double pixelsPerMz, double maxPixels = 6)
    {
        if (_mz.Length == 0)
            return -1;
        var i = LowerBound(mz);
        var best = -1;
        var bestDistance = double.PositiveInfinity;
        for (var j = Math.Max(0, i - 2); j < Math.Min(_mz.Length, i + 2); j++)
        {
            var d = Math.Abs(_mz[j] - mz) * pixelsPerMz;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = j;
            }
        }
        return bestDistance <= maxPixels ? best : -1;
    }

    private int LowerBound(double value)
    {
        int lo = 0, hi = _mz.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_mz[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
