using Glimpse.Core.Data;

namespace Glimpse.Core.Scans;

/// <summary>
/// Which spectra a chromatogram or scan lookup considers: all of them, one MS level, or the MS2 scans of
/// one isolation window.
/// </summary>
public sealed record ScanFilter
{
    public static ScanFilter All { get; } = new();

    public static ScanFilter Ms1 { get; } = new() { MsLevel = 1 };

    public static ScanFilter Ms2 { get; } = new() { MsLevel = 2 };

    /// <summary>Only MS2 scans whose isolation window contains <paramref name="mz"/>.</summary>
    public static ScanFilter Ms2Containing(double mz) => new() { MsLevel = 2, IsolationMz = mz };

    /// <summary>Only MS2 scans with exactly this isolation window (to 0.001 m/z).</summary>
    public static ScanFilter Ms2Window(double lower, double upper) =>
        new() { MsLevel = 2, WindowLower = lower, WindowUpper = upper };

    /// <summary>MS level to accept, or null for all levels.</summary>
    public int? MsLevel { get; init; }

    /// <summary>When set, accept only MSn scans whose isolation window contains this m/z.</summary>
    public double? IsolationMz { get; init; }

    public double? WindowLower { get; init; }

    public double? WindowUpper { get; init; }

    public bool Accepts(in ScanHeader h)
    {
        if (MsLevel is { } level && h.MsLevel != level)
            return false;
        if (IsolationMz is { } mz && !h.IsolationContains(mz))
            return false;
        if (WindowLower is { } lo && WindowUpper is { } hi
            && (!h.HasIsolationWindow || Math.Abs(h.IsolationLower - lo) > 0.001 || Math.Abs(h.IsolationUpper - hi) > 0.001))
            return false;
        return true;
    }

    public string Describe()
    {
        if (WindowLower is { } lo && WindowUpper is { } hi)
            return $"MS2 {lo:0.##}-{hi:0.##}";
        if (IsolationMz is { } mz)
            return $"MS2 containing {mz:0.####}";
        return MsLevel switch
        {
            null => "All scans",
            var l => $"MS{l}",
        };
    }
}
