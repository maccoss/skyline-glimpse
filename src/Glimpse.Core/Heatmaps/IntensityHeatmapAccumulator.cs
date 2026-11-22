using Glimpse.Core.Data;
using Glimpse.Core.Scans;

namespace Glimpse.Core.Heatmaps;

/// <summary>
/// Accumulates the m/z-by-retention-time intensity maps during the scan-index pass.
/// </summary>
/// <remarks>
/// <para><b>MS1:</b> each scan's peaks are summed into fixed-width m/z bins (default 1 Th), and each
/// cell is the mean of that over the scans falling in its RT column.</para>
/// <para><b>MS2:</b> each scan paints its total ion current across every m/z bin its isolation window
/// covers, so a DIA scheme draws as bands, DDA as short ticks at each precursor, and overlapping
/// windows average. Each cell is the mean over the scans that touched it.</para>
/// <para>RT columns are fixed before the pass from the first and last retention time, so memory is
/// bounded no matter how many scans the run has. m/z rows are allocated only when touched, so an
/// acquisition range of 350-1650 costs 1,300 rows, not the full axis.</para>
/// </remarks>
public sealed class IntensityHeatmapAccumulator : IScanObserver
{
    private readonly double _mzBin;
    private readonly int _maxColumns;
    private readonly double _mzMax;

    private double _rtLow;
    private double _rtBin;
    private int _columns;

    private Layer _ms1 = null!;
    private Layer _ms2 = null!;

    /// <param name="mzBinTh">Width of one m/z row in Th.</param>
    /// <param name="maxColumns">Number of retention-time columns.</param>
    /// <param name="mzMax">Peaks above this m/z are ignored (keeps a stray value from allocating rows).</param>
    public IntensityHeatmapAccumulator(double mzBinTh = 1.0, int maxColumns = 2000, double mzMax = 6000)
    {
        _mzBin = mzBinTh;
        _maxColumns = maxColumns;
        _mzMax = mzMax;
    }

    /// <summary>The finished maps, available after the pass.</summary>
    public IntensityHeatmaps? Result { get; private set; }

    public void Begin(int spectrumCount, double rtFirst, double rtLast)
    {
        if (!double.IsFinite(rtFirst) || !double.IsFinite(rtLast) || rtLast <= rtFirst)
        {
            rtFirst = 0;
            rtLast = 1;
        }
        _columns = Math.Max(1, Math.Min(_maxColumns, spectrumCount));
        _rtLow = rtFirst;
        // A hair wider than the range so the last scan lands inside the last column.
        _rtBin = (rtLast - rtFirst) * 1.000001 / _columns;
        _ms1 = new Layer(_columns, perCellCounts: false);
        _ms2 = new Layer(_columns, perCellCounts: true);
    }

    public void Observe(SpectrumData spectrum)
    {
        var h = spectrum.Header;
        if (double.IsNaN(h.RetentionTime))
            return;
        var col = (int)Math.Floor((h.RetentionTime - _rtLow) / _rtBin);
        if (col < 0 || col >= _columns)
            return;
        var ionScale = h.HasInjectionTime ? h.InjectionTimeMs / 1000.0 : double.NaN;

        if (h.MsLevel == 1)
        {
            _ms1.ColumnScans[col]++;
            if (!double.IsNaN(ionScale))
                _ms1.ColumnIonScans[col]++;
            var mz = spectrum.Mz;
            var intensity = spectrum.Intensity;
            var i = 0;
            while (i < mz.Length)
            {
                var row = (int)(mz[i] / _mzBin);
                if (mz[i] > _mzMax || row < 0)
                {
                    i++;
                    continue;
                }
                var sum = 0.0;
                while (i < mz.Length && (int)(mz[i] / _mzBin) == row)
                    sum += intensity[i++];
                _ms1.Add(row, col, sum, sum * ionScale);
            }
        }
        else if (h.MsLevel >= 2 && h.HasIsolationWindow)
        {
            var first = (int)(h.IsolationLower / _mzBin);
            var last = (int)(Math.Max(h.IsolationLower, h.IsolationUpper - 1e-9) / _mzBin);
            if (first < 0 || h.IsolationUpper > _mzMax)
                return;
            for (var row = first; row <= last; row++)
            {
                _ms2.Add(row, col, h.TotalIonCurrent, h.TotalIonCurrent * ionScale);
                _ms2.Touch(row, col, !double.IsNaN(ionScale));
            }
        }
    }

    public void End()
    {
        Result = new IntensityHeatmaps(
            _ms1.ToGrid(_mzBin, _rtLow, _rtBin),
            _ms2.ToGrid(_mzBin, _rtLow, _rtBin));
    }

    private sealed class Layer
    {
        private readonly int _columns;
        private readonly bool _perCell;
        private readonly Dictionary<int, Row> _rows = new();

        public Layer(int columns, bool perCellCounts)
        {
            _columns = columns;
            _perCell = perCellCounts;
            ColumnScans = new int[columns];
            ColumnIonScans = new int[columns];
        }

        public int[] ColumnScans { get; }

        public int[] ColumnIonScans { get; }

        public void Add(int row, int col, double value, double ions)
        {
            var r = GetRow(row);
            r.Sum[col] += (float)value;
            if (!double.IsNaN(ions))
                r.IonSum[col] += (float)ions;
        }

        public void Touch(int row, int col, bool hasIons)
        {
            var r = GetRow(row);
            r.Count![col]++;
            if (hasIons)
                r.IonCount![col]++;
        }

        private Row GetRow(int row)
        {
            if (!_rows.TryGetValue(row, out var r))
            {
                r = new Row(_columns, _perCell);
                _rows.Add(row, r);
            }
            return r;
        }

        public HeatmapGrid ToGrid(double mzBin, double rtLow, double rtBin)
        {
            if (_rows.Count == 0)
                return HeatmapGrid.Empty;
            var minRow = _rows.Keys.Min();
            var maxRow = _rows.Keys.Max();
            var rows = maxRow - minRow + 1;
            var values = new float[rows, _columns];
            var ions = new float[rows, _columns];
            for (var r = 0; r < rows; r++)
            {
                var has = _rows.TryGetValue(minRow + r, out var row);
                for (var c = 0; c < _columns; c++)
                {
                    float v, n;
                    if (_perCell)
                    {
                        var count = has ? row!.Count![c] : 0;
                        var ionCount = has ? row!.IonCount![c] : 0;
                        v = count > 0 ? row!.Sum[c] / count : float.NaN;
                        n = ionCount > 0 ? row!.IonSum[c] / ionCount : float.NaN;
                    }
                    else
                    {
                        // MS1: a column with scans but no peaks in this bin is a real zero; a column
                        // with no MS1 scan at all is missing data.
                        v = ColumnScans[c] > 0 ? (has ? row!.Sum[c] : 0) / ColumnScans[c] : float.NaN;
                        n = ColumnIonScans[c] > 0 ? (has ? row!.IonSum[c] : 0) / ColumnIonScans[c] : float.NaN;
                    }
                    values[r, c] = v;
                    ions[r, c] = n;
                }
            }
            var maxGap = GridGaps.TypicalGap(values) * 3;
            GridGaps.FillShortGaps(values, maxGap);
            GridGaps.FillShortGaps(ions, maxGap);
            return new HeatmapGrid(values, ions, minRow * mzBin, mzBin, rtLow, rtBin);
        }

        private sealed class Row
        {
            public Row(int columns, bool perCell)
            {
                Sum = new float[columns];
                IonSum = new float[columns];
                if (perCell)
                {
                    Count = new ushort[columns];
                    IonCount = new ushort[columns];
                }
            }

            public float[] Sum { get; }

            public float[] IonSum { get; }

            public ushort[]? Count { get; }

            public ushort[]? IonCount { get; }
        }
    }
}

/// <summary>The MS1 and MS2 intensity maps of one run.</summary>
public sealed record IntensityHeatmaps(HeatmapGrid Ms1, HeatmapGrid Ms2);

/// <summary>
/// A regular m/z (rows) by retention time (columns) grid. Row r covers
/// [MzLow + r*MzBin, MzLow + (r+1)*MzBin); column c covers [RtLow + c*RtBin, RtLow + (c+1)*RtBin).
/// NaN marks a cell with no data (no scan of that level in the column, or no window covering it).
/// </summary>
public sealed record HeatmapGrid(float[,] Values, float[,] Ions, double MzLow, double MzBin, double RtLow, double RtBin)
{
    public static HeatmapGrid Empty { get; } = new(new float[0, 0], new float[0, 0], 0, 1, 0, 1);

    public int Rows => Values.GetLength(0);

    public int Columns => Values.GetLength(1);

    public bool IsEmpty => Rows == 0 || Columns == 0;

    public double MzHigh => MzLow + Rows * MzBin;

    public double RtHigh => RtLow + Columns * RtBin;

    public float[,] For(IntensityUnits units) => units == IntensityUnits.Ions ? Ions : Values;

    public int RowAt(double mz) => (int)Math.Floor((mz - MzLow) / MzBin);

    public int ColumnAt(double rt) => (int)Math.Floor((rt - RtLow) / RtBin);
}
