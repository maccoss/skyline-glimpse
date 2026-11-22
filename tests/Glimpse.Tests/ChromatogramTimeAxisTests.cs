using Glimpse.Core.Chromatograms;
using Xunit;

namespace Glimpse.Tests;

/// <summary>Selecting peptides keeps the whole run in view while the TIC is drawn; XICs alone are zoomed to.</summary>
public class ChromatogramTimeAxisTests
{
    private static XicTrace Xic(params double[] times) => new("x", times, times.Select(_ => 1.0).ToArray(), null);

    private static readonly XicTrace[] TwoXics = { Xic(10, 11, 12), Xic(13, 14) };

    [Fact]
    public void WithTheTicTheWholeRunIsShown()
    {
        Assert.Equal((0.5, 60.0), ChromatogramTimeAxis.Range(0.5, 60, TwoXics, runTracesShown: true, fullRun: false));
    }

    [Fact]
    public void XicsAloneAreZoomedTo()
    {
        // 10-14 min, padded by 10% of 4 min (0.4) each side.
        var (low, high) = ChromatogramTimeAxis.Range(0.5, 60, TwoXics, runTracesShown: false, fullRun: false);
        Assert.Equal(9.6, low, 9);
        Assert.Equal(14.4, high, 9);
    }

    [Fact]
    public void ANarrowSpanGetsAtLeastAFifthOfAMinute()
    {
        var (low, high) = ChromatogramTimeAxis.Range(0.5, 60, new[] { Xic(20, 20.5) }, runTracesShown: false, fullRun: false);
        Assert.Equal(19.8, low, 9);
        Assert.Equal(20.7, high, 9);
    }

    [Fact]
    public void ThePaddingStopsAtTheRunsEnds()
    {
        Assert.Equal((1.0, 15.0), ChromatogramTimeAxis.Range(1, 15, new[] { Xic(1.1, 14.9) }, runTracesShown: false, fullRun: false));
    }

    [Fact]
    public void ResetZoomShowsTheWholeRunEvenWithXicsAlone()
    {
        Assert.Equal((0.5, 60.0), ChromatogramTimeAxis.Range(0.5, 60, TwoXics, runTracesShown: false, fullRun: true));
    }

    [Fact]
    public void NoXicsOrOnlyEmptyOnesShowTheWholeRun()
    {
        Assert.Equal((0.5, 60.0), ChromatogramTimeAxis.Range(0.5, 60, Array.Empty<XicTrace>(), runTracesShown: false, fullRun: false));
        Assert.Equal((0.5, 60.0), ChromatogramTimeAxis.Range(0.5, 60, new[] { Xic() }, runTracesShown: false, fullRun: false));
    }

    [Fact]
    public void MoreThanFiftyXicsShowTheWholeRun()
    {
        var fifty = Enumerable.Range(0, 50).Select(_ => Xic(10, 11)).ToArray();
        Assert.Equal(9.8, ChromatogramTimeAxis.Range(0.5, 60, fifty, runTracesShown: false, fullRun: false).Low, 9);
        var fiftyOne = fifty.Append(Xic(10, 11)).ToArray();
        Assert.Equal((0.5, 60.0), ChromatogramTimeAxis.Range(0.5, 60, fiftyOne, runTracesShown: false, fullRun: false));
    }

    [Fact]
    public void AnEmptyTraceDoesNotStretchTheZoom()
    {
        var (low, high) = ChromatogramTimeAxis.Range(0.5, 60, new[] { Xic(), Xic(30, 31) }, runTracesShown: false, fullRun: false);
        Assert.Equal(29.8, low, 9);
        Assert.Equal(31.2, high, 9);
    }
}
