using System.Windows;
using System.Windows.Input;
using ScottPlot;
using ScottPlot.WPF;

namespace Glimpse.App.Plotting;

/// <summary>
/// Turns a left-button press and release that did not move into a "click at these coordinates", so
/// a left-drag still zooms to a rectangle while a plain click selects a point.
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
    /// Glimpse's mouse: left-drag zooms to the rectangle drawn, a left click (no drag) picks a point (see
    /// <see cref="OnClick"/>), middle-drag pans, right-drag stretches an axis, the wheel zooms, and a
    /// double-click or middle-click runs <paramref name="reset"/> (Glimpse's own view: intensity from zero,
    /// the whole run or spectrum). ScottPlot's own double-click (a render-timing overlay) and middle-click
    /// (an autoscale with margins below zero) are removed.
    /// </summary>
    public static void OnReset(WpfPlot plot, Action reset)
    {
        var input = plot.UserInputProcessor;
        input.DoubleLeftClickBenchmark(false);
        input.RemoveAll<ScottPlot.Interactivity.UserActionResponses.SingleClickAutoscale>();
        input.RemoveAll<ScottPlot.Interactivity.UserActionResponses.KeyboardAutoscale>();
        input.RemoveAll<ScottPlot.Interactivity.UserActionResponses.MouseDragPan>();
        input.RemoveAll<ScottPlot.Interactivity.UserActionResponses.MouseDragZoomRectangle>();
        input.UserActionResponses.Add(new ScottPlot.Interactivity.UserActionResponses.MouseDragZoomRectangle(ScottPlot.Interactivity.StandardMouseButtons.Left));
        input.UserActionResponses.Add(new ScottPlot.Interactivity.UserActionResponses.MouseDragPan(ScottPlot.Interactivity.StandardMouseButtons.Middle));
        plot.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                reset();
                e.Handled = true;
            }
        };
        Point? middleDown = null;
        plot.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
                middleDown = e.GetPosition(plot);
        };
        plot.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle || middleDown is not { } start)
                return;
            middleDown = null;
            var up = e.GetPosition(plot);
            if (Math.Abs(up.X - start.X) <= MaxMovePixels && Math.Abs(up.Y - start.Y) <= MaxMovePixels)
                reset();
        };
    }

    /// <summary>
    /// Pin intensity axes at zero: whatever a zoom rectangle, a pan or the wheel does, the bottom of the
    /// axis stays at 0 and only its top moves. The wheel zooms about the mouse, so zooming in would
    /// otherwise lift the bottom off zero, and a rectangle dragged past the plot would take it negative.
    /// Applied just before each render, so it costs no extra redraw.
    /// </summary>
    public static void KeepFromZero(WpfPlot plot, params ScottPlot.IYAxis[] axes)
    {
        plot.Plot.RenderManager.RenderStarting += (_, _) =>
        {
            foreach (var axis in axes)
            {
                if (axis.Min != 0 && axis.Max > 0)
                    plot.Plot.Axes.SetLimitsY(0, axis.Max, axis);
            }
        };
    }

    /// <summary>
    /// Draw at the screen's scale: without this a 200% display shows every font and line at half the size
    /// FigureStyle asks for. Call once the plot is in a window (the scale is the window's).
    /// </summary>
    public static void MatchDisplayScale(WpfPlot plot)
    {
        plot.DisplayScale = plot.DetectDisplayScale();
        ApplyScale(plot);
        plot.Refresh();
    }

    /// <summary>
    /// Grow text and lines with the plot (FigureStyle.SizeScale): a maximized window on a large screen then
    /// shows the same figure scaled up. Call once; it follows every resize.
    /// </summary>
    /// <remarks>
    /// SizeChanged comes in the layout pass, before the render the resize causes, so setting the scale there
    /// is enough: no Refresh of its own, which would draw every resize twice. (RenderStarting is too late:
    /// ScottPlot has laid the plot out by then, and the new scale would only show on the next render.)
    /// </remarks>
    public static void ScaleWithSize(WpfPlot plot) => plot.SizeChanged += (_, _) => ApplyScale(plot);

    /// <summary>
    /// The size scale applied on top of the display scale (1 for a plot at or below the reference size):
    /// divide a radius in render units by it to keep the radius the same on screen.
    /// </summary>
    public static double SizeScaleOf(WpfPlot plot) => plot.DisplayScale > 0 ? plot.Plot.ScaleFactor / plot.DisplayScale : 1;

    /// <summary>The display scale times the size scale; returns true when it changed.</summary>
    private static bool ApplyScale(WpfPlot plot)
    {
        var scale = plot.DisplayScale * Glimpse.Plotting.FigureStyle.SizeScale(plot.ActualWidth, plot.ActualHeight);
        if (Math.Abs(scale - plot.Plot.ScaleFactor) < 0.005)
            return false;
        plot.Plot.ScaleFactor = scale;
        return true;
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
