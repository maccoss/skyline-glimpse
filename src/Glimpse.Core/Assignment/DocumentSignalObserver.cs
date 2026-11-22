using Glimpse.Core.Chemistry;
using Glimpse.Core.Data;
using Glimpse.Core.Document;
using Glimpse.Core.Heatmaps;
using Glimpse.Core.Scans;

namespace Glimpse.Core.Assignment;

/// <summary>
/// Rides on the scan pass of one result file and measures everything that depends on the document:
/// per spectrum, the signal claimed by document peptides (each peak counted once, Skyline-PRISM's ion
/// accounting rule) and the number of peptides eluting in it; per precursor, its summed MS1 and MS2
/// XICs over its integrated peak plus a margin; and the assigned-fraction and peptides-per-spectrum
/// heatmaps.
/// </summary>
/// <remarks>
/// A precursor's claims are its document transitions (MS1: its precursor isotope transitions, or M to
/// M+2 when it has none; MS2: its fragment transitions, in the scans Skyline extracts it from by the
/// document's isolation scheme) inside the document's extraction tolerance.
/// It claims signal only inside Skyline's integrated peak in this file; the XIC extends
/// <see cref="XicMarginMinutes"/> beyond it so the peak can be seen in context.
/// </remarks>
public sealed class DocumentSignalObserver : IScanObserver
{
    private readonly string _fileName;
    private readonly FilePrecursor[] _byMz;
    private readonly double[] _mz;
    private readonly FilePrecursor[] _byStart;
    private readonly int _maxColumns;
    private readonly double _mzBin;
    private readonly IsolationScheme? _scheme;

    private float[] _assigned = Array.Empty<float>();
    private ushort[] _counts = Array.Empty<ushort>();
    private readonly List<FilePrecursor> _active = new();
    private int _nextStart;
    private int[] _stamp = Array.Empty<int>();
    private int _generation;

    private MeanGrid _ms1Total = null!;
    private MeanGrid _ms1Assigned = null!;
    private MeanGrid _ms1Count = null!;
    private MeanGrid _ms2Fraction = null!;
    private MeanGrid _ms2Count = null!;

    /// <param name="document">The document.</param>
    /// <param name="fileName">The document's name for this result file (its peak boundaries are looked up by it).</param>
    /// <param name="settings">Extraction tolerances.</param>
    /// <param name="xicMarginMinutes">How far beyond each integrated peak to extract the XIC.</param>
    /// <param name="mzBinTh">Heatmap row height, matching the intensity heatmap.</param>
    /// <param name="maxColumns">Heatmap RT columns, matching the intensity heatmap.</param>
    public DocumentSignalObserver(DocumentModel document, string fileName, DocumentSettings settings,
        double xicMarginMinutes = 1.0, double mzBinTh = 1.0, int maxColumns = 2000)
    {
        _fileName = fileName;
        XicMarginMinutes = xicMarginMinutes;
        _mzBin = mzBinTh;
        _maxColumns = maxColumns;
        _scheme = settings.IsolationScheme;
        var list = new List<FilePrecursor>();
        // A peptide listed under several proteins is one molecule: measured once, counted once in the
        // peptides-per-spectrum heatmap, and its XIC shared by every copy. Copies Skyline integrated
        // differently are kept apart.
        var byMolecule = new Dictionary<(string, int, string, double, double), FilePrecursor>();
        foreach (var p in document.Precursors)
        {
            if (!p.Peaks.TryGetValue(fileName, out var peak) || !peak.HasBoundaries || !double.IsFinite(p.Mz))
                continue;
            var key = (p.ModifiedSequence, p.Charge, p.LabelType, peak.Start, peak.End);
            if (byMolecule.TryGetValue(key, out var first))
            {
                first.Copies.Add(p);
                continue;
            }
            var ms1 = p.Transitions.Where(t => t.IsPrecursor).Select(t => t.ProductMz).ToList();
            if (ms1.Count == 0)
                ms1 = Fragments.PrecursorIsotopes(p.Mz, p.Charge).Select(i => i.Mz).ToList();
            var ms2 = p.Transitions.Where(t => !t.IsPrecursor).Select(t => t.ProductMz).ToList();
            var f = new FilePrecursor(p, peak.Start, peak.End,
                Windows(ms1, settings.PrecursorTolerance), Windows(ms2, settings.ProductTolerance));
            byMolecule.Add(key, f);
            list.Add(f);
        }
        _byMz = list.OrderBy(f => f.Precursor.Mz).ToArray();
        _mz = _byMz.Select(f => f.Precursor.Mz).ToArray();
        _byStart = list.OrderBy(f => f.Start).ToArray();
    }

    public double XicMarginMinutes { get; }

    /// <summary>The result, available after the pass.</summary>
    public DocumentSignals? Result { get; private set; }

    public void Begin(int spectrumCount, double rtFirst, double rtLast)
    {
        _assigned = new float[spectrumCount];
        _counts = new ushort[spectrumCount];
        if (!double.IsFinite(rtFirst) || !double.IsFinite(rtLast) || rtLast <= rtFirst)
        {
            rtFirst = 0;
            rtLast = 1;
        }
        var columns = Math.Max(1, Math.Min(_maxColumns, spectrumCount));
        var rtBin = (rtLast - rtFirst) * 1.000001 / columns;
        _ms1Total = new MeanGrid(columns, _mzBin, rtFirst, rtBin);
        _ms1Assigned = new MeanGrid(columns, _mzBin, rtFirst, rtBin);
        _ms1Count = new MeanGrid(columns, _mzBin, rtFirst, rtBin);
        _ms2Fraction = new MeanGrid(columns, _mzBin, rtFirst, rtBin);
        _ms2Count = new MeanGrid(columns, _mzBin, rtFirst, rtBin);
    }

    public void Observe(SpectrumData spectrum)
    {
        var h = spectrum.Header;
        var rt = h.RetentionTime;
        if (double.IsNaN(rt) || h.Index >= _assigned.Length)
            return;
        if (_stamp.Length < spectrum.Mz.Length)
            _stamp = new int[Math.Max(spectrum.Mz.Length, _stamp.Length * 2)];
        _generation++;

        var inPeak = 0;
        if (h.MsLevel == 1)
        {
            AdvanceActive(rt);
            foreach (var f in _active)
                inPeak += Extract(spectrum, f, f.Ms1, f.Ms1Xic, f.Ms1Claims, rt, countMs1Bins: true);
        }
        else if (h.MsLevel >= 2 && IsolationScheme.RangeFor(_scheme, h) is { } range)
        {
            // The precursors Skyline extracts from this scan: by the document's isolation scheme, so a
            // precursor in the overlap of two windows is extracted from one of them only.
            var from = SpectrumData.LowerBound(_mz, range.Lo);
            for (var i = from; i < _byMz.Length && _mz[i] < range.Hi; i++)
            {
                var f = _byMz[i];
                if (rt >= f.Start - XicMarginMinutes && rt <= f.End + XicMarginMinutes)
                    inPeak += Extract(spectrum, f, f.Ms2, f.Ms2Xic, f.Ms2Claims, rt, countMs1Bins: false);
            }
        }

        // Each claimed peak once, however many precursors claim it.
        double assigned = 0;
        var mzs = spectrum.Mz;
        var intensities = spectrum.Intensity;
        for (var i = 0; i < mzs.Length; i++)
        {
            if (_stamp[i] == _generation)
                assigned += intensities[i];
        }
        _assigned[h.Index] = (float)assigned;
        _counts[h.Index] = (ushort)Math.Min(inPeak, ushort.MaxValue);

        // Heatmaps.
        var col = _ms1Total.ColumnAt(rt);
        if (col < 0)
            return;
        if (h.MsLevel == 1)
        {
            // Per m/z bin: total and claimed signal (summed per scan; divided by the scan count at the end).
            var i = 0;
            while (i < mzs.Length)
            {
                var row = _ms1Total.RowAt(mzs[i]);
                double total = 0, claimed = 0;
                while (i < mzs.Length && _ms1Total.RowAt(mzs[i]) == row)
                {
                    total += intensities[i];
                    if (_stamp[i] == _generation)
                        claimed += intensities[i];
                    i++;
                }
                _ms1Total.Add(row, col, total);
                _ms1Assigned.Add(row, col, claimed);
            }
        }
        else if (h.MsLevel >= 2 && h.HasIsolationWindow)
        {
            var fraction = h.TotalIonCurrent > 0 ? assigned / h.TotalIonCurrent : 0;
            _ms2Fraction.AddRange(h.IsolationLower, h.IsolationUpper, col, fraction);
            _ms2Count.AddRange(h.IsolationLower, h.IsolationUpper, col, inPeak);
        }
    }

    /// <summary>
    /// XIC point for one precursor in one scan, and its claims when the scan is inside its peak (each
    /// claimed peak recorded, so a summed XIC can count a peak once however many precursors claim it).
    /// Returns 1 when the precursor counts as present in the spectrum.
    /// </summary>
    private int Extract(SpectrumData spectrum, FilePrecursor f, (double Lo, double Hi)[] windows, XicBuilder xic, ClaimBuilder claims, double rt, bool countMs1Bins)
    {
        var mzs = spectrum.Mz;
        var intensities = spectrum.Intensity;
        var inside = rt >= f.Start && rt <= f.End;
        double sum = 0;
        foreach (var (lo, hi) in windows)
        {
            for (var i = SpectrumData.LowerBound(mzs, lo); i < mzs.Length && mzs[i] <= hi; i++)
            {
                sum += intensities[i];
                if (inside)
                {
                    _stamp[i] = _generation;
                    claims.Add(spectrum.Header.Index, i, (float)intensities[i]);
                }
            }
        }
        if (windows.Length > 0)
            xic.Add(spectrum.Header.Index, (float)sum);
        if (!inside)
            return 0;
        if (countMs1Bins)
        {
            var col = _ms1Count.ColumnAt(rt);
            if (col >= 0)
                _ms1Count.Add(_ms1Count.RowAt(f.Precursor.Mz), col, 1);
        }
        return 1;
    }

    /// <summary>Keep the MS1 active set to the precursors whose extended window contains <paramref name="rt"/>.</summary>
    private void AdvanceActive(double rt)
    {
        while (_nextStart < _byStart.Length && _byStart[_nextStart].Start - XicMarginMinutes <= rt)
            _active.Add(_byStart[_nextStart++]);
        _active.RemoveAll(f => f.End + XicMarginMinutes < rt);
    }

    public void End()
    {
        var xics = new Dictionary<DocPrecursor, PrecursorXic>();
        foreach (var f in _byMz)
        {
            var xic = new PrecursorXic(f.Precursor, f.Ms1Xic.Scans(), f.Ms1Xic.Values(), f.Ms2Xic.Scans(), f.Ms2Xic.Values())
            {
                Ms1Claims = f.Ms1Claims.Build(),
                Ms2Claims = f.Ms2Claims.Build(),
            };
            xics[f.Precursor] = xic;
            foreach (var copy in f.Copies)
                xics[copy] = xic with { Precursor = copy };
        }

        Result = new DocumentSignals(
            _fileName, _assigned, _counts, xics,
            Ms1Fraction: Ratio(_ms1Assigned.ToGrid(), _ms1Total.ToGrid()),
            Ms2Fraction: _ms2Fraction.ToGrid(),
            Ms1Count: _ms1Count.ToGrid(),
            Ms2Count: _ms2Count.ToGrid(),
            PrecursorsInFile: _byMz.Length);
    }

    /// <summary>
    /// Cell-wise assigned / total. Both grids receive an observation for exactly the same cells (every
    /// m/z bin of every MS1 scan, claimed or not), so they share their layout cell for cell.
    /// </summary>
    private static HeatmapGrid Ratio(HeatmapGrid assigned, HeatmapGrid total)
    {
        if (total.IsEmpty)
            return HeatmapGrid.Empty;
        var values = new float[total.Rows, total.Columns];
        for (var r = 0; r < total.Rows; r++)
        {
            for (var c = 0; c < total.Columns; c++)
            {
                var t = total.Values[r, c];
                values[r, c] = float.IsNaN(t) ? float.NaN : t > 0 ? assigned.Values[r, c] / t : 0;
            }
        }
        return total with { Values = values, Ions = values };
    }

    private static (double, double)[] Windows(List<double> mzs, MzTolerance tolerance) =>
        mzs.Where(double.IsFinite).Select(tolerance.WindowAt).ToArray();

    private sealed class FilePrecursor
    {
        public FilePrecursor(DocPrecursor precursor, double start, double end, (double, double)[] ms1, (double, double)[] ms2)
        {
            Precursor = precursor;
            Start = start;
            End = end;
            Ms1 = ms1;
            Ms2 = ms2;
        }

        public DocPrecursor Precursor { get; }

        /// <summary>The same molecule under other proteins, with the same integrated peak.</summary>
        public List<DocPrecursor> Copies { get; } = new();

        public double Start { get; }

        public double End { get; }

        public (double Lo, double Hi)[] Ms1 { get; }

        public (double Lo, double Hi)[] Ms2 { get; }

        public XicBuilder Ms1Xic { get; } = new();

        public XicBuilder Ms2Xic { get; } = new();

        public ClaimBuilder Ms1Claims { get; } = new();

        public ClaimBuilder Ms2Claims { get; } = new();
    }

    private sealed class ClaimBuilder
    {
        private List<long>? _peaks;
        private List<float>? _intensities;

        public void Add(int scan, int peak, float intensity)
        {
            (_peaks ??= new List<long>()).Add(PeakClaims.Key(scan, peak));
            (_intensities ??= new List<float>()).Add(intensity);
        }

        public PeakClaims Build() => _peaks is null
            ? PeakClaims.Empty
            : new PeakClaims(_peaks.ToArray(), _intensities!.ToArray());
    }

    private sealed class XicBuilder
    {
        private List<int>? _scans;
        private List<float>? _values;

        public void Add(int scan, float value)
        {
            (_scans ??= new List<int>()).Add(scan);
            (_values ??= new List<float>()).Add(value);
        }

        public int[] Scans() => _scans?.ToArray() ?? Array.Empty<int>();

        public float[] Values() => _values?.ToArray() ?? Array.Empty<float>();
    }
}

/// <summary>Everything the document pass measured for one result file.</summary>
/// <param name="FileName">The document's name for the file.</param>
/// <param name="AssignedIntensity">Per spectrum index: signal claimed by document peptides (as recorded).</param>
/// <param name="PeptideCount">
/// Per spectrum index: precursors inside their integrated peak (and, for MS2, the isolation window), each
/// molecule once however many proteins list it.
/// </param>
/// <param name="Xics">Per precursor with a peak in this file (every copy of a shared peptide): its summed MS1 and MS2 XICs.</param>
/// <param name="PrecursorsInFile">Distinct precursor molecules with a peak in this file.</param>
public sealed record DocumentSignals(
    string FileName,
    float[] AssignedIntensity,
    ushort[] PeptideCount,
    IReadOnlyDictionary<DocPrecursor, PrecursorXic> Xics,
    HeatmapGrid Ms1Fraction,
    HeatmapGrid Ms2Fraction,
    HeatmapGrid Ms1Count,
    HeatmapGrid Ms2Count,
    int PrecursorsInFile);

/// <summary>
/// One precursor's summed XICs: the MS1 trace (its precursor isotopes, in MS1 scans) and the MS2 trace
/// (its fragment transitions, in the MS2 scans whose isolation window contains it). Values are as
/// recorded; convert to ions with each scan's injection time.
/// </summary>
public sealed record PrecursorXic(DocPrecursor Precursor, int[] Ms1Scans, float[] Ms1Values, int[] Ms2Scans, float[] Ms2Values)
{
    /// <summary>The MS1 peaks this precursor claimed inside its integrated peak; null when not recorded.</summary>
    public PeakClaims? Ms1Claims { get; init; }

    /// <summary>The MS2 peaks this precursor claimed inside its integrated peak; null when not recorded.</summary>
    public PeakClaims? Ms2Claims { get; init; }
}

/// <summary>
/// The peaks one precursor claimed in one file, each named by its scan and its position in that scan's
/// peak list, with its intensity (as recorded). A peak claimed by several precursors (a peptide listed under
/// two proteins, or two precursors whose windows catch the same peak) has the same key in each, so a sum
/// over a selection can count it once.
/// </summary>
public sealed record PeakClaims(long[] Peaks, float[] Intensities)
{
    public static PeakClaims Empty { get; } = new(Array.Empty<long>(), Array.Empty<float>());

    public static long Key(int scan, int peak) => ((long)scan << 32) | (uint)peak;

    public static int ScanOf(long key) => (int)(key >> 32);
}
