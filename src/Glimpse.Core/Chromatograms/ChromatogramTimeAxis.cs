namespace Glimpse.Core.Chromatograms;

/// <summary>The retention-time range the chromatogram shows after it is rescaled.</summary>
public static class ChromatogramTimeAxis
{
    /// <summary>Selections with more XIC traces than this keep the whole run: their span is the run anyway.</summary>
    public const int MaxZoomTraces = 50;

    /// <summary>
    /// The whole run, first scan to last, while a whole-run trace (TIC, base peak, assigned) is drawn, when
    /// <paramref name="fullRun"/> asks for it, or with no or many XICs. XICs drawn on their own: their span,
    /// padded by 10% (at least 0.2 min) on each side, within the run.
    /// </summary>
    public static (double Low, double High) Range(double rtFirst, double rtLast, IReadOnlyList<XicTrace> xics,
        bool runTracesShown, bool fullRun)
    {
        var drawn = xics.Where(x => x.Times.Length > 0).ToList();
        if (runTracesShown || fullRun || drawn.Count == 0 || xics.Count > MaxZoomTraces)
            return (rtFirst, rtLast);
        var t0 = drawn.Min(x => x.Times[0]);
        var t1 = drawn.Max(x => x.Times[^1]);
        var pad = Math.Max(0.2, (t1 - t0) * 0.1);
        return (Math.Max(rtFirst, t0 - pad), Math.Min(rtLast, t1 + pad));
    }
}
