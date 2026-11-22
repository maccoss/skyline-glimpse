namespace Glimpse.App.Plotting;

/// <summary>ScottPlot colors as WPF brushes (for legend swatches).</summary>
public static class WpfBrushes
{
    public static System.Windows.Media.SolidColorBrush From(ScottPlot.Color c) =>
        new(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
}
