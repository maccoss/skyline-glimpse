using System.Xml.Linq;
using Glimpse.Core.Assignment;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Scans;
using Xunit;

namespace Glimpse.Tests;

/// <summary>Which precursors Skyline extracts from an MS2 scan, by the document's isolation scheme.</summary>
public class IsolationSchemeTests
{
    private static ScanHeader Scan(double lower, double upper, double target = double.NaN, double rt = 1) =>
        new(0, 2, rt, 10f, target, lower, upper, 0, double.NaN, 0, 0, Polarity.Positive, true);

    private static IsolationScheme Scheme(string xml) => IsolationScheme.From(XElement.Parse(xml))!;

    [Fact]
    public void WindowsWithMarginsExtractOnlyBetweenTheirStartAndEnd()
    {
        // Acquired 399-421 and 419-441: 2 Th of overlap, margins of 1 Th on each side.
        var scheme = Scheme("<isolation_scheme name=\"m\" special_handling=\"None\">"
            + "<isolation_window start=\"420\" end=\"440\" margin=\"1\" />"
            + "<isolation_window start=\"400\" end=\"420\" margin=\"1\" /></isolation_scheme>");
        Assert.Equal(new[] { new SchemeWindow(400, 420), new SchemeWindow(420, 440) }, scheme.Windows);   // sorted by start
        Assert.Equal((400.0, 420.0), scheme.ExtractionRange(Scan(399, 421)));
        Assert.Equal((420.0, 440.0), scheme.ExtractionRange(Scan(419, 441)));
        // The scan's isolation target, when the file gives one, is what picks the window.
        Assert.Equal((420.0, 440.0), scheme.ExtractionRange(Scan(399, 421, target: 425)));
        // A scan whose target is in no window: Skyline extracts nothing from it.
        Assert.Null(scheme.ExtractionRange(Scan(500, 520)));
        Assert.Null(scheme.ExtractionRange(Scan(double.NaN, double.NaN)));
    }

    [Fact]
    public void OfTwoWindowsContainingTheTargetTheNearestCentreWins()
    {
        // Overlapping windows: the target 404 is in all three; its own window's centre is nearest.
        var scheme = Scheme("<isolation_scheme special_handling=\"None\">"
            + "<isolation_window start=\"396\" end=\"404.5\" /><isolation_window start=\"400\" end=\"408\" />"
            + "<isolation_window start=\"404\" end=\"412\" /></isolation_scheme>");
        Assert.Equal(new SchemeWindow(400, 408), scheme.WindowFor(404));
        Assert.Equal(new SchemeWindow(396, 404.5), scheme.WindowFor(399));
        Assert.Equal(new SchemeWindow(404, 412), scheme.WindowFor(409));
        // Start is inside a window, end is not.
        Assert.Equal(new SchemeWindow(396, 404.5), scheme.WindowFor(396));
        Assert.Null(scheme.WindowFor(412));
        Assert.Null(scheme.WindowFor(395.99));
        Assert.Equal((400.0, 408.0), scheme.ExtractionRange(Scan(400, 408)));
    }

    [Theory]
    [InlineData("Overlap")]
    [InlineData("Overlap Multiplexed")]
    [InlineData("Fast Overlap")]
    public void AnOverlapSchemeUsesEachScansOwnWindow(string handling)
    {
        // The scheme of a saved document (staggered 8 Th windows) against data acquired in 3 Th windows:
        // Skyline ignores the scheme's windows for Overlap handling, so the scan's 3 Th are extracted, not
        // the 8 Th window whose centre is nearest.
        var scheme = Scheme($"<isolation_scheme special_handling=\"{handling}\">"
            + "<isolation_window start=\"400.4319\" end=\"408.4355\" /><isolation_window start=\"396.4301\" end=\"404.4337\" /></isolation_scheme>");
        Assert.True(scheme.IgnoresWindows);
        Assert.Equal((400.432, 403.433), scheme.ExtractionRange(Scan(400.432, 403.433, target: 401.933)));
        Assert.Equal((500.0, 503.0), scheme.ExtractionRange(Scan(500, 503)));   // outside every window too
        Assert.False(Scheme("<isolation_scheme special_handling=\"Multiplexed\" />").IgnoresWindows);
    }

    [Fact]
    public void ATieBetweenTwoCentresKeepsTheFirstWindow()
    {
        var scheme = Scheme("<isolation_scheme><isolation_window start=\"400\" end=\"410\" /><isolation_window start=\"404\" end=\"408\" /></isolation_scheme>");
        Assert.Equal(new SchemeWindow(400, 410), scheme.WindowFor(405.5));   // centres 405 and 406, both 0.5 away
        Assert.Equal(new SchemeWindow(404, 408), scheme.WindowFor(405.6));
    }

    [Fact]
    public void WindowsWithTargetsAreMatchedByTarget()
    {
        var scheme = Scheme("<isolation_scheme><isolation_window start=\"400\" end=\"420\" target=\"410\" />"
            + "<isolation_window start=\"415\" end=\"440\" target=\"430\" /></isolation_scheme>");
        Assert.Equal((415.0, 440.0), scheme.ExtractionRange(Scan(420, 440, target: 430.05)));
        Assert.Equal((400.0, 420.0), scheme.ExtractionRange(Scan(400, 420, target: 409.95)));
        // Containing the target is not enough: it must match one.
        Assert.Null(scheme.ExtractionRange(Scan(410, 430, target: 420)));
        Assert.Null(scheme.ExtractionRange(Scan(420, 440, target: 430.06)));
    }

    [Fact]
    public void APrecursorFilterSetsTheWidthButNeverWidensTheScan()
    {
        var narrow = Scheme("<isolation_scheme precursor_filter=\"10\" />");
        Assert.Equal((410.0, 420.0), narrow.ExtractionRange(Scan(400, 430)));
        Assert.Equal((402.0, 406.0), narrow.ExtractionRange(Scan(402, 406)));
        Assert.False(narrow.UseMargin);

        // Asymmetric: 2 Th left of the target and 8 right.
        var asymmetric = Scheme("<isolation_scheme precursor_left_filter=\"2\" precursor_right_filter=\"8\" />");
        Assert.Equal(2, asymmetric.PrecursorFilter);
        Assert.Equal(8, asymmetric.PrecursorRightFilter);
        Assert.Equal((413.0, 423.0), asymmetric.ExtractionRange(Scan(400, 430)));
    }

    [Fact]
    public void ResultsOnlyTakesTheScansWindowLessAnyMargin()
    {
        var margin = Scheme("<isolation_scheme precursor_filter_margin=\"1.5\" />");
        Assert.True(margin.UseMargin);
        Assert.Equal(1.5, margin.PrecursorFilter);
        Assert.Equal((401.5, 418.5), margin.ExtractionRange(Scan(400, 420)));

        var plain = Scheme("<isolation_scheme name=\"Results\" />");
        Assert.Null(plain.PrecursorFilter);
        Assert.Equal((400.0, 420.0), plain.ExtractionRange(Scan(400, 420)));
    }

    [Theory]
    [InlineData("All Ions")]
    [InlineData("MSe")]
    public void AllIonsExtractsEveryPrecursor(string handling)
    {
        var scheme = Scheme($"<isolation_scheme special_handling=\"{handling}\" precursor_filter=\"5\" />");
        Assert.True(scheme.IsAllIons);
        Assert.Equal((double.NegativeInfinity, double.PositiveInfinity), scheme.ExtractionRange(Scan(100, 1500)));
    }

    [Fact]
    public void WithoutASchemeTheScansOwnWindowIsUsed()
    {
        Assert.Equal((400.0, 420.0), IsolationScheme.RangeFor(null, Scan(400, 420)));
        Assert.Null(IsolationScheme.RangeFor(null, Scan(double.NaN, double.NaN)));
        Assert.Equal((401.0, 419.0), IsolationScheme.RangeFor(Scheme("<isolation_scheme precursor_filter_margin=\"1\" />"), Scan(400, 420)));
    }

    [Fact]
    public void BrokenWindowsAreSkippedAndAMissingSchemeIsNull()
    {
        var scheme = Scheme("<isolation_scheme><isolation_window start=\"400\" /><isolation_window start=\"420\" end=\"410\" /><isolation_window start=\"450\" end=\"450\" />"
            + "<isolation_window start=\"x\" end=\"430\" /><isolation_window start=\"430\" end=\"440\" /></isolation_scheme>");
        Assert.Equal(new[] { new SchemeWindow(430, 440) }, scheme.Windows);
        Assert.Null(IsolationScheme.From(null));
        Assert.False(scheme.IsAllIons);
        Assert.False(scheme.IgnoresWindows);
        Assert.Null(scheme.SpecialHandling);
    }

    [Fact]
    public void DocumentSettingsReadTheSchemeFromTheFullScanSettings()
    {
        var settings = DocumentSettings.Parse("<settings_summary><transition_settings>"
            + "<transition_full_scan acquisition_method=\"DIA\" product_mass_analyzer=\"centroided\" product_res=\"10\">"
            + "<isolation_scheme name=\"temp\" special_handling=\"Overlap\">"
            + "<isolation_window start=\"400.43190000000004\" end=\"408.43553800000007\" /></isolation_scheme>"
            + "</transition_full_scan></transition_settings></settings_summary>");
        Assert.Equal("Overlap", settings.IsolationScheme!.SpecialHandling);
        Assert.Equal(new[] { new SchemeWindow(400.43190000000004, 408.43553800000007) }, settings.IsolationScheme.Windows);
        Assert.Null(DocumentSettings.Parse("<s><transition_full_scan acquisition_method=\"DDA\" /></s>").IsolationScheme);
        Assert.Null(DocumentSettings.Default.IsolationScheme);
    }

    [Fact]
    public void APrecursorInTheOverlapOfTwoWindowsIsExtractedFromOneOfThem()
    {
        // AAK at 419.5 is acquired in both windows (399-421 and 419-441) but extracted only from the first,
        // whose extraction range (400-420) contains it.
        var protein = new DocProtein("P1", null, null, null);
        var aak = Docs.Precursor(protein, "AAK", 2, 419.5, "run1.raw", 0, 5, ("precursor", 2, 419.5), ("y2", 1, 300.0));
        var source = new FakeSource()
            .Ms2(1, 399, 421, new[] { 300.0 }, new[] { 40.0 })
            .Ms2(1.01, 419, 441, new[] { 300.0 }, new[] { 70.0 });
        var settings = DocumentSettings.Default with
        {
            IsolationScheme = Scheme("<isolation_scheme><isolation_window start=\"400\" end=\"420\" margin=\"1\" />"
                + "<isolation_window start=\"420\" end=\"440\" margin=\"1\" /></isolation_scheme>"),
        };
        var observer = new DocumentSignalObserver(Docs.Model(protein), "run1.raw", settings);
        var index = ScanIndexBuilder.Build(source, false, new IScanObserver[] { observer });
        var xic = observer.Result!.Xics[aak];
        Assert.Equal(new[] { 0 }, xic.Ms2Scans);
        Assert.Equal(new[] { 40f }, xic.Ms2Values);
        Assert.Equal(new[] { 40f, 0f }, observer.Result.AssignedIntensity);
        Assert.Equal(new ushort[] { 1, 0 }, observer.Result.PeptideCount);

        // Without the scheme, each scan's own window decides, and both have it.
        var plain = new DocumentSignalObserver(Docs.Model(protein), "run1.raw", DocumentSettings.Default);
        ScanIndexBuilder.Build(source, false, new IScanObserver[] { plain });
        Assert.Equal(new[] { 0, 1 }, plain.Result!.Xics[aak].Ms2Scans);

        // The spectrum's candidates follow the same rule.
        var candidates = new SpectrumCandidates(new[] { aak }, settings.IsolationScheme);
        Assert.Single(candidates.Find(index.Headers[0], "run1.raw", CandidateOptions.Default));
        Assert.Empty(candidates.Find(index.Headers[1], "run1.raw", CandidateOptions.Default));
        Assert.Single(new SpectrumCandidates(new[] { aak }).Find(index.Headers[1], "run1.raw", CandidateOptions.Default));
    }
}
