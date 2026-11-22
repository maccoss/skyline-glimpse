using System.Globalization;
using System.Xml.Linq;
using Glimpse.Core.Data;

namespace Glimpse.Core.Document;

/// <summary>One window of a document's isolation scheme: the precursor range Skyline extracts from it.</summary>
/// <param name="Start">Lowest precursor m/z extracted (the acquired window starts a margin lower).</param>
/// <param name="End">Highest precursor m/z extracted (exclusive; the acquired window ends a margin higher).</param>
/// <param name="Target">The scan's isolation target, when the scheme names one.</param>
public readonly record struct SchemeWindow(double Start, double End, double? Target = null)
{
    public double Center => (Start + End) / 2;

    public bool Contains(double mz) => mz >= Start && mz < End;
}

/// <summary>
/// The document's DIA isolation scheme, and which precursors Skyline extracts from each MS2 scan.
/// </summary>
/// <remarks>
/// Ported from Skyline: <c>SpectrumFilter.CalcDiaIsolationValues</c> and
/// <c>IsolationScheme.GetIsolationWindow</c> (pwiz_tools/Skyline/Model/Results/SpectrumFilter.cs,
/// Model/DocSettings/IsolationScheme.cs), with the XML read as <c>IsolationScheme.ReadXml</c> and
/// <c>IsolationWindow.ReadXml</c> read it. A scheme's windows carry margins outside their start and end;
/// a precursor in a margin is not extracted from that window, which is how overlapping windows avoid
/// counting a precursor twice. For the Overlap special handlings Skyline ignores the scheme
/// (<c>SpectrumFilter.Extract</c>'s <c>ignoreIso</c>) and extracts each demultiplexed spectrum by its own
/// isolation window; Glimpse does not demultiplex, so it uses each scan's own window.
/// </remarks>
public sealed record IsolationScheme
{
    /// <summary>"All Ions", "Overlap", ... or null for none.</summary>
    public string? SpecialHandling { get; init; }

    /// <summary>precursor_filter (a width), or with <see cref="UseMargin"/> precursor_filter_margin (a margin).</summary>
    public double? PrecursorFilter { get; init; }

    public double? PrecursorRightFilter { get; init; }

    /// <summary>True when <see cref="PrecursorFilter"/> is a margin taken off each side of the scan's window.</summary>
    public bool UseMargin { get; init; }

    /// <summary>Prespecified windows, by start. Empty means the windows come from the data ("Results only").</summary>
    public IReadOnlyList<SchemeWindow> Windows { get; init; } = Array.Empty<SchemeWindow>();

    public bool IsAllIons =>
        string.Equals(SpecialHandling, "All Ions", StringComparison.OrdinalIgnoreCase)
        || string.Equals(SpecialHandling, "MSe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Overlap, Overlap Multiplexed or Fast Overlap: Skyline extracts by each spectrum's own window, not the scheme's.</summary>
    public bool IgnoresWindows =>
        string.Equals(SpecialHandling, "Overlap", StringComparison.OrdinalIgnoreCase)
        || string.Equals(SpecialHandling, "Overlap Multiplexed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(SpecialHandling, "Fast Overlap", StringComparison.OrdinalIgnoreCase);

    /// <summary>Skyline's default instrument m/z match tolerance, for schemes that name targets.</summary>
    private const double MzMatchTolerance = 0.055;

    /// <summary>
    /// The precursor m/z range Skyline extracts from an MS2 scan with this isolation window, or null when
    /// the scan matches no window of the scheme (Skyline extracts nothing from it).
    /// </summary>
    public (double Lo, double Hi)? ExtractionRange(in ScanHeader scan)
    {
        if (!scan.HasIsolationWindow)
            return null;
        // Skyline works from the scan's isolation target and its total width.
        var width = scan.IsolationUpper - scan.IsolationLower;
        var target = double.IsFinite(scan.IsolationTarget) ? scan.IsolationTarget : scan.IsolationLower + width / 2;

        if (IsAllIons)
            return (double.NegativeInfinity, double.PositiveInfinity);

        if (IgnoresWindows)
            return (scan.IsolationLower, scan.IsolationUpper);

        if (PrecursorFilter is { } filter && !UseMargin)
        {
            // A set width, but never wider than the acquisition's (that would take in points from
            // outside the window, giving spiky chromatograms).
            var w = Math.Min(filter + (PrecursorRightFilter ?? 0), width);
            if (PrecursorRightFilter is { } right)
                target += right - w / 2;
            return (target - w / 2, target + w / 2);
        }

        if (Windows.Count > 0)
            return WindowFor(target) is { } window ? (window.Start, window.End) : null;

        // Results only: the scan's own window, less the margin on each side.
        var extracted = width - (PrecursorFilter ?? 0) * 2;
        return (target - extracted / 2, target + extracted / 2);
    }

    /// <summary>
    /// The precursor m/z range extracted from an MS2 scan: by <paramref name="scheme"/> when the document
    /// has one, else the scan's own isolation window. Null when nothing is extracted from the scan.
    /// </summary>
    public static (double Lo, double Hi)? RangeFor(IsolationScheme? scheme, in ScanHeader scan)
    {
        if (scheme is not null)
            return scheme.ExtractionRange(scan);
        return scan.HasIsolationWindow ? (scan.IsolationLower, scan.IsolationUpper) : null;
    }

    /// <summary>
    /// The scheme window for a scan's isolation target: the one naming that target, or else the containing
    /// window whose centre is nearest it.
    /// </summary>
    public SchemeWindow? WindowFor(double target)
    {
        if (Windows.Count == 0)
            return null;
        if (Windows[0].Target.HasValue)
        {
            foreach (var w in Windows)
            {
                if (w.Target is { } t && Math.Abs(t - target) <= MzMatchTolerance)
                    return w;
            }
            return null;
        }
        SchemeWindow? best = null;
        foreach (var w in Windows)
        {
            if (w.Contains(target) && (best is null || Math.Abs(w.Center - target) < Math.Abs(best.Value.Center - target)))
                best = w;
        }
        return best;
    }

    /// <summary>The isolation_scheme element of Skyline settings XML, or null when there is none.</summary>
    public static IsolationScheme? From(XElement? element)
    {
        if (element is null)
            return null;
        var filter = Num(element, "precursor_filter");
        var useMargin = false;
        double? right = null;
        if (filter is null)
        {
            filter = Num(element, "precursor_filter_margin");
            useMargin = filter is not null;
        }
        if (filter is null)
        {
            filter = Num(element, "precursor_left_filter");
            right = Num(element, "precursor_right_filter");
        }
        var windows = element.Elements("isolation_window")
            .Select(w => (Start: Num(w, "start"), End: Num(w, "end"), Target: Num(w, "target")))
            .Where(w => w.Start is not null && w.End is not null && w.End > w.Start)
            .Select(w => new SchemeWindow(w.Start!.Value, w.End!.Value, w.Target))
            .OrderBy(w => w.Start)
            .ToArray();
        return new IsolationScheme
        {
            SpecialHandling = (string?)element.Attribute("special_handling"),
            PrecursorFilter = filter,
            PrecursorRightFilter = right,
            UseMargin = useMargin,
            Windows = windows,
        };
    }

    private static double? Num(XElement e, string name) =>
        double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}
