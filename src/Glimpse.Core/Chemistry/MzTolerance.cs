using System.Globalization;

namespace Glimpse.Core.Chemistry;

/// <summary>
/// An m/z extraction window rule. Skyline's full-scan rules (centroided ppm, QIT fixed width, TOF,
/// Orbitrap and FT-ICR resolving power) are transcribed exactly from
/// <c>TransitionFullScan.GetFilterWindow</c> by way of Skyline-PRISM's <c>ProductMassTolerance</c>.
/// </summary>
/// <param name="Analyzer">"centroided" (ppm), "qit" (fixed m/z width), "tof", "orbitrap", "ft_icr".</param>
/// <param name="Resolution">ppm for centroided, m/z for qit, resolving power otherwise.</param>
/// <param name="ResolutionMz">m/z at which the resolving power is specified (orbitrap, ft_icr).</param>
/// <param name="SelectiveExtraction">Skyline's "use selective extraction" (narrows resolving-power windows).</param>
public sealed record MzTolerance(string Analyzer, double Resolution, double? ResolutionMz = null, bool SelectiveExtraction = false)
{
    public static MzTolerance Ppm(double ppm) => new("centroided", ppm);

    /// <summary>A fixed ±<paramref name="halfWidth"/> m/z window (Skyline's QIT rule: width = 2 x resolution).</summary>
    public static MzTolerance Th(double halfWidth) => new("qit", halfWidth);

    private double ResPerFilter => SelectiveExtraction ? 1.0 : 2.0;

    private double Denominator => Analyzer switch
    {
        "tof" => Resolution / ResPerFilter,
        "orbitrap" => Math.Sqrt(ResolutionMz ?? 0) * Resolution / ResPerFilter,
        "ft_icr" => (ResolutionMz ?? 0) * Resolution / ResPerFilter,
        "centroided" => Resolution * 2 / 1e6,
        _ => Resolution * ResPerFilter,
    };

    /// <summary>Full width of the window at <paramref name="mz"/>.</summary>
    public double WidthAt(double mz)
    {
        var d = Denominator;
        if (d <= 0 || !double.IsFinite(d))
            return 0;
        return Analyzer switch
        {
            "orbitrap" => mz * Math.Sqrt(mz) / d,
            "tof" => mz / d,
            "ft_icr" => mz * mz / d,
            "centroided" => mz * d,
            _ => d,
        };
    }

    public (double Low, double High) WindowAt(double mz)
    {
        var half = WidthAt(mz) / 2;
        return (mz - half, mz + half);
    }

    public bool IsUsable => Resolution > 0 && (Analyzer is not ("orbitrap" or "ft_icr") || ResolutionMz is > 0);

    /// <summary>Build from the attributes of Skyline's &lt;transition_full_scan&gt; element; null when unusable.</summary>
    public static MzTolerance? FromSkyline(string? analyzer, string? resolution, string? resolutionMz, string? selective)
    {
        if (string.IsNullOrWhiteSpace(analyzer) || !TryNum(resolution, out var res))
            return null;
        var t = new MzTolerance(analyzer.Trim().ToLowerInvariant(), res, TryNum(resolutionMz, out var at) ? at : null,
            string.Equals(selective, "true", StringComparison.OrdinalIgnoreCase));
        return t.IsUsable ? t : null;
    }

    /// <summary>Parse "10 ppm", "0.02 m/z", "0.5 Th", "0.5 Da".</summary>
    public static MzTolerance? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var s = text.Trim().ToLowerInvariant();
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] is '.' or 'e' or '+' or '-'))
            end++;
        if (!TryNum(s[..end], out var v) || v <= 0)
            return null;
        var unit = s[end..].Trim();
        return unit switch
        {
            "ppm" => Ppm(v),
            "m/z" or "mz" or "th" or "da" or "" => Th(v),
            _ => null,
        };
    }

    public string Describe() => Analyzer switch
    {
        "centroided" => $"±{Resolution:0.###} ppm",
        "qit" => $"±{Resolution:0.####} m/z",
        "tof" => $"TOF resolving power {Resolution:0}",
        "orbitrap" => $"Orbitrap {Resolution:0} at m/z {ResolutionMz:0}",
        "ft_icr" => $"FT-ICR {Resolution:0} at m/z {ResolutionMz:0}",
        _ => $"{Resolution} ({Analyzer})",
    };

    private static bool TryNum(string? s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
