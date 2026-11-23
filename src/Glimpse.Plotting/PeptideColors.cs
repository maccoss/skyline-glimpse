using Glimpse.Core.Document;
using ScottPlot;

namespace Glimpse.Plotting;

/// <summary>
/// One color per peptide, the same in the spectrum, the legend and the XIC traces: derived from the
/// peptide's modified sequence, so it does not change as the selection or the spectrum changes.
/// </summary>
public static class PeptideColors
{
    // Tableau 20, minus the two greys. No peptide is ever drawn black or grey: grey means unassigned, and
    // black a peptide-like isotope envelope no document peptide explains.
    private static readonly Color[] Palette =
    {
        Color.FromHex("#1f77b4"), Color.FromHex("#ff7f0e"), Color.FromHex("#2ca02c"), Color.FromHex("#d62728"),
        Color.FromHex("#9467bd"), Color.FromHex("#8c564b"), Color.FromHex("#e377c2"), Color.FromHex("#bcbd22"),
        Color.FromHex("#17becf"), Color.FromHex("#aec7e8"), Color.FromHex("#ffbb78"), Color.FromHex("#98df8a"),
        Color.FromHex("#ff9896"), Color.FromHex("#c5b0d5"), Color.FromHex("#c49c94"), Color.FromHex("#f7b6d2"),
        Color.FromHex("#dbdb8d"), Color.FromHex("#9edae5"),
    };

    /// <summary>Color for a peak shared by more than one peptide: a vivid magenta no peptide has (never black or grey).</summary>
    public static Color Shared { get; } = Color.FromHex("#c800c8");

    /// <summary>
    /// A peptide's color dimmed (a peptide other than the focused one): its own hue, 70% of the way to white,
    /// so it stays a peptide's color rather than turning the grey of unassigned peaks.
    /// </summary>
    public static Color Dimmed(Color color) => new(
        (byte)Math.Round(color.R + (255 - color.R) * 0.7), (byte)Math.Round(color.G + (255 - color.G) * 0.7),
        (byte)Math.Round(color.B + (255 - color.B) * 0.7));

    public static Color For(DocPeptide peptide) => ForKey(peptide.Protein.Name + "/" + peptide.ModifiedSequence);

    public static Color For(DocProtein protein) => ForKey(protein.Name);

    public static Color ForKey(string key) => Palette[Slot(key)];

    /// <summary>
    /// Colors for a selection, in selection order: the nine strong hues first, then their light
    /// variants, so a handful of selected peptides never share a hue (hashing them could give two the
    /// same, or a dark and a light shade of one hue). Peptides beyond the palette take their usual color.
    /// </summary>
    public static Dictionary<DocPeptide, Color> ForSelection(IEnumerable<DocPeptide> peptides)
    {
        var colors = new Dictionary<DocPeptide, Color>();
        foreach (var peptide in peptides)
        {
            if (colors.ContainsKey(peptide))
                continue;
            colors[peptide] = colors.Count < Palette.Length ? Palette[colors.Count] : For(peptide);
        }
        return colors;
    }

    public static int PaletteSize => Palette.Length;

    /// <summary>The palette, for checking it.</summary>
    internal static IReadOnlyList<Color> All => Palette;

    private static uint Slot(string key)
    {
        // FNV-1a: stable across runs (string.GetHashCode is randomized per process).
        var hash = 2166136261u;
        foreach (var ch in key)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash % (uint)Palette.Length;
    }
}
