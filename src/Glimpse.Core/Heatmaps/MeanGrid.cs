namespace Glimpse.Core.Heatmaps;

/// <summary>
/// A sparse m/z-by-RT grid of per-cell means, for the document heatmaps (assigned fraction,
/// precursors per spectrum). Rows are allocated when first touched.
/// </summary>
public sealed class MeanGrid
{
    private readonly int _columns;
    private readonly double _mzBin;
    private readonly double _rtLow;
    private readonly double _rtBin;
    private readonly Dictionary<int, (float[] Sum, ushort[] Count)> _rows = new();

    public MeanGrid(int columns, double mzBin, double rtLow, double rtBin)
    {
        _columns = columns;
        _mzBin = mzBin;
        _rtLow = rtLow;
        _rtBin = rtBin;
    }

    public int ColumnAt(double rt)
    {
        // Floor, not a cast: a cast truncates toward zero and would put a time just before the grid in column 0.
        var c = (int)Math.Floor((rt - _rtLow) / _rtBin);
        return c >= 0 && c < _columns ? c : -1;
    }

    public int RowAt(double mz) => (int)Math.Floor(mz / _mzBin);

    /// <summary>Add one observation to a cell.</summary>
    public void Add(int row, int col, double value)
    {
        if (row < 0 || col < 0)
            return;
        if (!_rows.TryGetValue(row, out var r))
        {
            r = (new float[_columns], new ushort[_columns]);
            _rows.Add(row, r);
        }
        r.Sum[col] += (float)value;
        if (r.Count[col] < ushort.MaxValue)
            r.Count[col]++;
    }

    /// <summary>Add one observation across every row an m/z range covers.</summary>
    public void AddRange(double mzLow, double mzHigh, int col, double value)
    {
        var first = RowAt(mzLow);
        var last = RowAt(Math.Max(mzLow, mzHigh - 1e-9));
        for (var row = first; row <= last; row++)
            Add(row, col, value);
    }

    /// <summary>
    /// The grid of means. Cells with no observation are NaN, except short runs between observed cells
    /// (narrower than a few acquisition cycles), which take the value to their left.
    /// </summary>
    public HeatmapGrid ToGrid()
    {
        if (_rows.Count == 0)
            return HeatmapGrid.Empty;
        var minRow = _rows.Keys.Min();
        var rows = _rows.Keys.Max() - minRow + 1;
        var values = new float[rows, _columns];
        for (var r = 0; r < rows; r++)
        {
            var has = _rows.TryGetValue(minRow + r, out var row);
            for (var c = 0; c < _columns; c++)
                values[r, c] = has && row.Count[c] > 0 ? row.Sum[c] / row.Count[c] : float.NaN;
        }
        GridGaps.FillShortGaps(values, GridGaps.TypicalGap(values) * 3);
        return new HeatmapGrid(values, values, minRow * _mzBin, _mzBin, _rtLow, _rtBin);
    }
}

/// <summary>Shared gap handling for heatmap grids whose RT columns are narrower than the acquisition cycle.</summary>
internal static class GridGaps
{
    public static int TypicalGap(float[,] values)
    {
        var rows = values.GetLength(0);
        var cols = values.GetLength(1);
        var gaps = new List<int>();
        for (var r = 0; r < rows && gaps.Count < 200_000; r += Math.Max(1, rows / 64))
        {
            var last = -1;
            for (var c = 0; c < cols; c++)
            {
                if (float.IsNaN(values[r, c]))
                    continue;
                if (last >= 0)
                    gaps.Add(c - last);
                last = c;
            }
        }
        if (gaps.Count == 0)
            return 1;
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    public static void FillShortGaps(float[,] values, int maxGap)
    {
        if (maxGap <= 1)
            return;
        var rows = values.GetLength(0);
        var cols = values.GetLength(1);
        for (var r = 0; r < rows; r++)
        {
            var last = -1;
            for (var c = 0; c < cols; c++)
            {
                if (float.IsNaN(values[r, c]))
                    continue;
                if (last >= 0 && c - last > 1 && c - last <= maxGap)
                {
                    for (var k = last + 1; k < c; k++)
                        values[r, k] = values[r, last];
                }
                last = c;
            }
        }
    }
}
