using System.Windows;
using System.Windows.Input;
using ScottPlot;
using ScottPlot.WPF;

namespace Glimpse.App.Plotting;

/// <summary>
/// Turns a left-button press and release that did not move into a "click at these coordinates", so
/// ScottPlot keeps left-drag panning while a plain click selects a point.
/// </summary>
public static class PlotClicks
{
    private const double MaxMovePixels = 4;

    public static void OnClick(WpfPlot plot, Action<Coordinates> handler)
    {
        Point? down = null;
        plot.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(plot);
        plot.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (down is not { } start)
                return;
            down = null;
            var up = e.GetPosition(plot);
            if (Math.Abs(up.X - start.X) > MaxMovePixels || Math.Abs(up.Y - start.Y) > MaxMovePixels)
                return;
            handler(ToCoordinates(plot, e));
        };
    }

    /// <summary>
    /// Draw at the screen's scale: without this a 200% display shows every font and line at half the size
    /// FigureStyle asks for. Call once the plot is in a window (the scale is the window's).
    /// </summary>
    public static void MatchDisplayScale(WpfPlot plot)
    {
        plot.DisplayScale = plot.DetectDisplayScale();
        plot.Plot.ScaleFactor = plot.DisplayScale;
        plot.Refresh();
    }

    /// <summary>The mouse position in the units plottables draw in (see FigureStyle.ToRenderPixel).</summary>
    public static Pixel ToRenderPixel(WpfPlot plot, MouseEventArgs e)
    {
        var p = e.GetPosition(plot);
        return Glimpse.Plotting.FigureStyle.ToRenderPixel(p.X * plot.DisplayScale, p.Y * plot.DisplayScale, plot.Plot.ScaleFactor);
    }

    public static Coordinates ToCoordinates(WpfPlot plot, MouseEventArgs e)
    {
        var p = e.GetPosition(plot);
        var scale = plot.DisplayScale;
        return plot.Plot.GetCoordinates(new Pixel(p.X * scale, p.Y * scale));
    }
}
