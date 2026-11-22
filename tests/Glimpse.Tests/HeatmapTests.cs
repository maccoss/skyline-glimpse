using Glimpse.Core.Data;
using Glimpse.Core.Heatmaps;
using Xunit;

namespace Glimpse.Tests;

/// <summary>Exact cell values of the intensity heatmaps on a hand-built run.</summary>
public class HeatmapTests
{
    [Fact]
    public void TheSaturationQuantileCountsOnlyCellsWithSignal()
    {
        // Ten cells with signal, 10 to 100, among empty (0), missing (NaN) and negative cells.
        var grid = new float[,]
        {
            { 0, 10, 20, 30, float.NaN },
            { 40, 50, 60, -5, 0 },
            { 70, 80, 90, 100, float.PositiveInfinity },
        };
        Assert.Equal(90, HeatmapScale.Quantile(grid, 0.90));    // nearest rank: the 9th of 10
        Assert.Equal(100, HeatmapScale.Quantile(grid, 0.95));   // rounds up to the 10th
        Assert.Equal(50, HeatmapScale.Quantile(grid, 0.50));
        Assert.Equal(10, HeatmapScale.Quantile(grid, 0.0));     // the smallest, not below it
        Assert.Equal(100, HeatmapScale.Quantile(grid, 1.0));
        Assert.Equal(10, HeatmapScale.Quantile(grid, -1));      // clamped
        Assert.Equal(100, HeatmapScale.Quantile(grid, 2));
        Assert.Equal(double.NaN, HeatmapScale.Quantile(new float[,] { { 0, float.NaN }, { -1, 0 } }, 0.9));
        Assert.Equal(double.NaN, HeatmapScale.Quantile(new float[0, 0], 0.9));
        Assert.Equal(7, HeatmapScale.Quantile(new float[,] { { 7 } }, 0.9));
    }

    [Fact]
    public void OnlyIntensityIsDrawnOnALogScale()
    {
        Assert.True(HeatmapScale.IsLog(HeatmapValue.Intensity, logRequested: true));
        Assert.False(HeatmapScale.IsLog(HeatmapValue.Intensity, logRequested: false));
        // Peptides per spectrum (a few to a few tens) and the assigned fraction (0-1) are always linear.
        Assert.False(HeatmapScale.IsLog(HeatmapValue.PeptidesPerSpectrum, logRequested: true));
        Assert.False(HeatmapScale.IsLog(HeatmapValue.AssignedFraction, logRequested: true));

        Assert.True(HeatmapScale.OffersLog(HeatmapValue.Intensity));
        Assert.False(HeatmapScale.OffersLog(HeatmapValue.AssignedFraction));
        Assert.False(HeatmapScale.OffersLog(HeatmapValue.PeptidesPerSpectrum));

        // A linear intensity map tops its color scale at the 90th percentile of the cells with signal.
        Assert.Equal(0.90, HeatmapScale.IntensitySaturationQuantile);

        // The enum follows the window's Value list: Intensity, Assigned fraction, Peptides per spectrum.
        Assert.Equal(new[] { 0, 1, 2 }, new[] { (int)HeatmapValue.Intensity, (int)HeatmapValue.AssignedFraction, (int)HeatmapValue.PeptidesPerSpectrum });
    }

    private static SpectrumData Ms1(double rt, double[] mz, double[] intensity, float it) =>
        FakeSource.Spectrum(0, 1, rt, mz, intensity, it, double.NaN, double.NaN);

    private static SpectrumData Ms2(double rt, double lower, double upper, double tic, float it) =>
        FakeSource.Spectrum(0, 2, rt, new[] { 300.0 }, new[] { tic }, it, lower, upper);

    private static IntensityHeatmaps Build()
    {
        // Four RT columns of ~1 min over 0-4.
        var acc = new IntensityHeatmapAccumulator(mzBinTh: 1, maxColumns: 4, mzMax: 1000);
        acc.Begin(spectrumCount: 8, rtFirst: 0, rtLast: 4);
        acc.Observe(Ms1(0.5, new[] { 500.2, 500.7, 600.1, 1500 }, new[] { 10.0, 30.0, 5.0, 99.0 }, 10f)); // 1500 > mzMax: ignored
        acc.Observe(Ms1(0.9, new[] { 500.5 }, new[] { 20.0 }, float.NaN));                                // no injection time
        acc.Observe(Ms1(2.5, new[] { 600.5 }, new[] { 8.0 }, 20f));
        acc.Observe(Ms2(1.5, 400.5, 402.5, 12, 10f));       // rows 400, 401, 402
        acc.Observe(Ms2(1.6, 401.0, 403.0, 6, float.NaN));   // rows 401, 402 (403.0 is the open end)
        acc.Observe(Ms1(double.NaN, new[] { 500.0 }, new[] { 1000.0 }, 10f));   // no RT: ignored
        acc.Observe(Ms1(10, new[] { 500.0 }, new[] { 1000.0 }, 10f));           // past the last column: ignored
        acc.Observe(FakeSource.Spectrum(0, 2, 1.5, new[] { 300.0 }, new[] { 1000.0 }, 10f, double.NaN, double.NaN)); // MS2 without a window
        acc.End();
        return acc.Result!;
    }

    [Fact]
    public void Ms1CellsAreBinnedSumsAveragedOverTheColumnsScans()
    {
        var g = Build().Ms1;
        Assert.Equal(500, g.MzLow);
        Assert.Equal(101, g.Rows);            // 500..600
        Assert.Equal(601, g.MzHigh);
        Assert.Equal(4, g.Columns);
        Assert.Equal(4.000004, g.RtHigh, 6);
        var r500 = g.RowAt(500.9);
        var r600 = g.RowAt(600.0);
        Assert.Equal(0, r500);
        Assert.Equal(100, r600);
        // Column 0 holds two MS1 scans: (10 + 30) and 20 in bin 500 -> mean 30; bin 600 -> 5 / 2.
        Assert.Equal(30f, g.Values[r500, 0]);
        Assert.Equal(2.5f, g.Values[r600, 0]);
        // Ions: only the first scan had an injection time (10 ms): 40 x 0.01 over one scan.
        Assert.Equal(0.4f, g.Ions[r500, 0], 5);
        Assert.Equal(0.05f, g.Ions[r600, 0], 5);
        // Column 2: one scan, 8 at m/z 600 (20 ms) and a real zero at 500.
        Assert.Equal(8f, g.Values[r600, 2]);
        Assert.Equal(0.16f, g.Ions[r600, 2], 5);
        Assert.Equal(0f, g.Values[r500, 2]);
        // Column 1 has no MS1 scan but sits between two that do: filled from the left.
        Assert.Equal(30f, g.Values[r500, 1]);
        // Column 3 has no MS1 scan and nothing after it: stays empty.
        Assert.True(float.IsNaN(g.Values[r500, 3]));
        Assert.Same(g.Ions, g.For(IntensityUnits.Ions));
        Assert.Same(g.Values, g.For(IntensityUnits.AsRecorded));
    }

    [Fact]
    public void Ms2ScansPaintTheirIsolationWindow()
    {
        var g = Build().Ms2;
        Assert.Equal(400, g.MzLow);
        Assert.Equal(3, g.Rows);              // 400, 401, 402 - not 403
        Assert.Equal(12f, g.Values[0, 1]);
        Assert.Equal(9f, g.Values[1, 1]);     // mean of the two overlapping windows
        Assert.Equal(9f, g.Values[2, 1]);
        // Ions: the second scan has no injection time, so only the first contributes.
        Assert.Equal(0.12f, g.Ions[0, 1], 5);
        Assert.Equal(0.12f, g.Ions[1, 1], 5);
        Assert.True(float.IsNaN(g.Values[0, 0]));
        Assert.True(float.IsNaN(g.Values[0, 2]));
    }

    [Fact]
    public void DegenerateTimeRangeStillRecords()
    {
        var acc = new IntensityHeatmapAccumulator(mzBinTh: 1, maxColumns: 10);
        acc.Begin(spectrumCount: 3, rtFirst: 5, rtLast: 5);   // no range: falls back to 0-1
        acc.Observe(Ms1(0.5, new[] { 500.0 }, new[] { 7.0 }, 10f));
        acc.End();
        var g = acc.Result!.Ms1;
        Assert.Equal(3, g.Columns);           // never more columns than spectra
        Assert.Equal(7f, g.Values[0, g.ColumnAt(0.5)]);
        Assert.True(acc.Result.Ms2.IsEmpty);
    }

    [Fact]
    public void GridLookupsFloorNotTruncate()
    {
        var g = new HeatmapGrid(new float[2, 2], new float[2, 2], 400, 0.5, 10, 2);
        Assert.Equal(1, g.RowAt(400.6));
        Assert.Equal(-1, g.RowAt(399.9));
        Assert.Equal(1, g.ColumnAt(12.5));
        Assert.Equal(-1, g.ColumnAt(9.9));
        Assert.Equal(401, g.MzHigh);
        Assert.Equal(14, g.RtHigh);
        Assert.True(new HeatmapGrid(new float[0, 3], new float[0, 3], 0, 1, 0, 1).IsEmpty);
        Assert.True(new HeatmapGrid(new float[3, 0], new float[3, 0], 0, 1, 0, 1).IsEmpty);
        Assert.False(g.IsEmpty);
    }

    [Fact]
    public void MeanGridUsesItsBinWidths()
    {
        var grid = new MeanGrid(columns: 4, mzBin: 2, rtLow: 10, rtBin: 0.5);
        Assert.Equal(1, grid.ColumnAt(10.7));
        Assert.Equal(-1, grid.ColumnAt(12.0));     // 4 columns cover 10-12
        Assert.Equal(201, grid.RowAt(403.9));
        grid.AddRange(400, 404, 0, 1);              // rows 200 and 201; 404 is the open end
        grid.Add(201, 3, 5);
        var g = grid.ToGrid();
        Assert.Equal(400, g.MzLow);
        Assert.Equal(2, g.Rows);
        Assert.Equal(1f, g.Values[0, 0]);
        Assert.Equal(1f, g.Values[1, 0]);
        Assert.Equal(5f, g.Values[1, 3]);
    }
}
