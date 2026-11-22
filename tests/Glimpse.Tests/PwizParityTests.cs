using Glimpse.Core.Scans;
using Glimpse.Pwiz;
using Pwiz.Data.Common.Cv;
using Pwiz.Data.Common.Params;
using Xunit;

namespace Glimpse.Tests;

public class PwizParityTests
{
    private static string SmallMzml => Path.Combine(Path.GetDirectoryName(TestData.SmallRaw)!, "small.pwiz.1.1.mzML");

    [Fact]
    public void RawAndConvertedMzmlIndexTheSame()
    {
        using var raw = PwizMsDataSource.Open(TestData.SmallRaw);
        using var mzml = PwizMsDataSource.Open(SmallMzml);
        var a = ScanIndexBuilder.Build(raw, centroid: false, Array.Empty<IScanObserver>());
        var b = ScanIndexBuilder.Build(mzml, centroid: false, Array.Empty<IScanObserver>());
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].MsLevel, b[i].MsLevel);
            Assert.Equal(a[i].RetentionTime, b[i].RetentionTime, 4);
            if (a[i].Centroided)
            {
                // Centroided scans must agree exactly: same peaks, same signal.
                Assert.Equal(a[i].PeakCount, b[i].PeakCount);
                Assert.Equal(a[i].TotalIonCurrent, b[i].TotalIonCurrent, a[i].TotalIonCurrent * 1e-9);
            }
            else
            {
                // Profile FTMS scans: this mzML was written years ago by msconvert on Thermo's older
                // MSFileReader, whose profile intensities run 3-5% below today's RawFileReader with the
                // same points (and occasionally one zero point more). Same data, different vendor library.
                Assert.InRange(b[i].PeakCount, a[i].PeakCount - 1, a[i].PeakCount + 1);
                Assert.Equal(a[i].TotalIonCurrent, b[i].TotalIonCurrent, a[i].TotalIonCurrent * 0.06);
            }
            Assert.Equal(a[i].HasIsolationWindow, b[i].HasIsolationWindow);
        }
        Assert.Equal(mzml.ReadRetentionTime(3), b[3].RetentionTime, 9);
    }

    [Fact]
    public void ReaderDetailsAndRunInfo()
    {
        using var source = PwizMsDataSource.Open(TestData.ThermoTestFile("BSA-FT-HCD.raw"));
        Assert.True(source.CanVendorCentroid);
        Assert.NotNull(source.Run.InstrumentModel);
        Assert.Equal("BSA-FT-HCD.raw", source.Run.FileName);
        var s = source.ReadSpectrum(0, centroid: false);
        Assert.Equal(1, s.Details.ScanNumber);
        Assert.Contains("scan=1", s.Details.NativeId);
        Assert.Contains(".raw", PwizMsDataSource.SupportedExtensions);
    }

    [Fact]
    public void OpeningBadFilesFails()
    {
        Assert.Throws<FileNotFoundException>(() => PwizMsDataSource.Open(Path.Combine(Path.GetTempPath(), "no-such-file.raw")));
        var junk = Path.Combine(Path.GetTempPath(), $"glimpse-junk-{Guid.NewGuid():N}.mzML");
        File.WriteAllText(junk, "this is not mzML");
        try
        {
            Assert.ThrowsAny<Exception>(() => PwizMsDataSource.Open(junk));
        }
        finally
        {
            File.Delete(junk);
        }
    }

    [Theory]
    [InlineData(CVID.UO_minute, 2.5, 2.5)]
    [InlineData(CVID.UO_second, 150, 2.5)]
    [InlineData(CVID.UO_millisecond, 150_000, 2.5)]
    public void ScanTimeUnitsBecomeMinutes(CVID units, double value, double minutes) =>
        Assert.Equal(minutes, PwizMsDataSource.Minutes(new CVParam(CVID.MS_scan_start_time, value, units)), 9);

    [Theory]
    [InlineData(CVID.UO_millisecond, 7.5, 7.5)]
    [InlineData(CVID.UO_second, 0.0075, 7.5)]
    [InlineData(CVID.UO_microsecond, 7500, 7.5)]
    [InlineData(CVID.UO_nanosecond, 7_500_000, 7.5)]
    [InlineData(CVID.UO_minute, 0.000125, 7.5)]
    public void InjectionTimeUnitsBecomeMilliseconds(CVID units, double value, double ms) =>
        Assert.Equal(ms, PwizMsDataSource.Milliseconds(new CVParam(CVID.MS_ion_injection_time, value, units)), 9);

    [Fact]
    public void MissingParamsAreNaN()
    {
        Assert.True(double.IsNaN(PwizMsDataSource.Minutes(null)));
        Assert.True(double.IsNaN(PwizMsDataSource.Milliseconds(new CVParam())));
    }
}
