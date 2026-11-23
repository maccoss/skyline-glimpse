using System.Globalization;
using System.Text.Json;
using Glimpse.Core.Data;
using Parquet;
using Parquet.Schema;

namespace Glimpse.Features;

/// <summary>
/// What a run's peptide-like search produced, kept next to the raw file as <c>run.hk</c> (Parquet) so reopening
/// the file shows the Peptide-like trace without searching again.
/// </summary>
/// <remarks>
/// <para>One row per isotope envelope: the spectrum index, MS level and retention time of its scan; the
/// series it was linked in (0 for MS1, then one per DIA isolation window); Hardklör's report (monoisotopic
/// mass, charge, intensity, base peak m/z, correlation, m/z range); the persistent feature it belongs to
/// (<c>feature</c>, -1 for none); and the peptide-like signal of its scan, the summed intensity of the scan's
/// peaks in persistent envelopes, each peak once (<c>scan_peptide_like</c>, and <c>scan_peptide_like_2plus</c>
/// counting only multiply charged MS1 envelopes; the same for MS2). The rows of one feature are the feature:
/// its seed is its most intense envelope. Envelopes' peaks are not kept: Hardklör finds the same envelopes in
/// a spectrum again in about a millisecond.</para>
/// <para>File metadata says what the search was made from (format, the raw file's size and time, the
/// spectrum count, the search settings); a file that does not match is ignored and replaced. It also lists
/// every searched scan of each series (<c>glimpse.series</c>), since a searched scan can have no envelope.</para>
/// </remarks>
public static class HkFile
{
    public const int FormatVersion = 2;

    /// <summary>What a .hk file must have been made from to be used.</summary>
    /// <param name="RawSize">The raw file's size in bytes (0 for a folder).</param>
    /// <param name="RawModifiedUtc">Its last write time.</param>
    /// <param name="SpectrumCount">Spectra in the run.</param>
    /// <param name="Settings">The search settings, as text (any change makes old files stale).</param>
    public sealed record Stamp(long RawSize, DateTime RawModifiedUtc, int SpectrumCount, string Settings)
    {
        /// <summary>The stamp of a raw file (or vendor folder) as it is on disk now.</summary>
        public static Stamp Of(string rawPath, int spectrumCount, string settings) => File.Exists(rawPath)
            ? new Stamp(new FileInfo(rawPath).Length, File.GetLastWriteTimeUtc(rawPath), spectrumCount, settings)
            : new Stamp(0, Directory.GetLastWriteTimeUtc(rawPath), spectrumCount, settings);
    }

    /// <summary>The .hk file beside a raw file: <c>run.raw</c> becomes <c>run.hk</c>.</summary>
    public static string PathBeside(string rawPath) => Path.ChangeExtension(rawPath.TrimEnd('\\', '/'), ".hk");

    private static readonly DataField<int> Scan = new("scan");
    private static readonly DataField<int> MsLevel = new("ms_level");
    private static readonly DataField<float> Rt = new("rt");
    private static readonly DataField<int> SeriesField = new("series");
    private static readonly DataField<double> MonoMass = new("mono_mass");
    private static readonly DataField<int> Charge = new("charge");
    private static readonly DataField<float> Intensity = new("intensity");
    private static readonly DataField<double> BasePeakMz = new("base_peak_mz");
    private static readonly DataField<float> Correlation = new("correlation");
    private static readonly DataField<double> LowMz = new("low_mz");
    private static readonly DataField<double> HighMz = new("high_mz");
    private static readonly DataField<int> Feature = new("feature");
    private static readonly DataField<double> ScanSignal = new("scan_peptide_like");
    private static readonly DataField<double> ScanSignal2Plus = new("scan_peptide_like_2plus");

    private static readonly ParquetSchema Schema = new(
        Scan, MsLevel, Rt, SeriesField, MonoMass, Charge, Intensity, BasePeakMz, Correlation, LowMz, HighMz, Feature, ScanSignal, ScanSignal2Plus);

    private const string FormatKey = "glimpse.hk.format";
    private const string RawSizeKey = "glimpse.raw.size";
    private const string RawModifiedKey = "glimpse.raw.modified";
    private const string SpectraKey = "glimpse.spectra";
    private const string SettingsKey = "glimpse.settings";
    private const string DdaKey = "glimpse.dda";
    private const string SeriesKey = "glimpse.series";

    /// <summary>Write the signal to <paramref name="path"/> (through a temporary file, so a reader never sees half of one).</summary>
    public static void Write(string path, PeptideLikeSignal signal, Stamp stamp) => WriteAsync(path, signal, stamp).GetAwaiter().GetResult();

    private static async Task WriteAsync(string path, PeptideLikeSignal signal, Stamp stamp)
    {
        var seriesOf = new Dictionary<int, int>();
        for (var s = 0; s < signal.Series.Count; s++)
            foreach (var searched in signal.Series[s])
                seriesOf[searched] = s;

        var scans = signal.Envelopes.Keys.Where(seriesOf.ContainsKey).Order().ToArray();
        var n = scans.Sum(i => signal.Envelopes[i].Keys.Length);
        var scan = new int[n];
        var level = new int[n];
        var rt = new float[n];
        var series = new int[n];
        var mass = new double[n];
        var charge = new int[n];
        var intensity = new float[n];
        var basePeak = new double[n];
        var correlation = new float[n];
        var low = new double[n];
        var high = new double[n];
        var feature = new int[n];
        var scanSignal = new double[n];
        var scanSignal2Plus = new double[n];
        var all = signal.IntensityByScan(1);
        var multiplyCharged = signal.IntensityByScan(2);
        var r = 0;
        foreach (var i in scans)
        {
            var env = signal.Envelopes[i];
            var ids = signal.FeatureOf[i];
            var h = signal.Headers[i];
            for (var e = 0; e < env.Keys.Length; e++, r++)
            {
                scan[r] = i;
                level[r] = h.MsLevel;
                rt[r] = (float)h.RetentionTime;
                series[r] = seriesOf[i];
                mass[r] = env.Keys[e].MonoMass;
                charge[r] = env.Keys[e].Charge;
                intensity[r] = env.Keys[e].Intensity;
                basePeak[r] = env.Details[e].BasePeakMz;
                correlation[r] = env.Details[e].Correlation;
                low[r] = env.Details[e].LowMz;
                high[r] = env.Details[e].HighMz;
                feature[r] = ids[e];
                scanSignal[r] = all[i];
                scanSignal2Plus[r] = multiplyCharged[i];
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // Zstd at its fastest level: about the compression of the default level, written some 50 times faster
            // (Parquet.Net's default Zstd level took 268 s for an Astral run's 17 million envelopes).
            await using var writer = await ParquetWriter.CreateAsync(Schema, stream, new ParquetOptions
            {
                CompressionMethod = CompressionMethod.Zstd,
                CompressionLevel = System.IO.Compression.CompressionLevel.Fastest,
            });
            writer.CustomMetadata = new Dictionary<string, string>
            {
                [FormatKey] = FormatVersion.ToString(CultureInfo.InvariantCulture),
                [RawSizeKey] = stamp.RawSize.ToString(CultureInfo.InvariantCulture),
                [RawModifiedKey] = stamp.RawModifiedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                [SpectraKey] = stamp.SpectrumCount.ToString(CultureInfo.InvariantCulture),
                [SettingsKey] = stamp.Settings,
                [DdaKey] = signal.IsDda ? "true" : "false",
                [SeriesKey] = JsonSerializer.Serialize(signal.Series),
            };
            using var group = writer.CreateRowGroup();
            await group.WriteAsync(Scan, (ReadOnlyMemory<int>)scan);
            await group.WriteAsync(MsLevel, (ReadOnlyMemory<int>)level);
            await group.WriteAsync(Rt, (ReadOnlyMemory<float>)rt);
            await group.WriteAsync(SeriesField, (ReadOnlyMemory<int>)series);
            await group.WriteAsync(MonoMass, (ReadOnlyMemory<double>)mass);
            await group.WriteAsync(Charge, (ReadOnlyMemory<int>)charge);
            await group.WriteAsync(Intensity, (ReadOnlyMemory<float>)intensity);
            await group.WriteAsync(BasePeakMz, (ReadOnlyMemory<double>)basePeak);
            await group.WriteAsync(Correlation, (ReadOnlyMemory<float>)correlation);
            await group.WriteAsync(LowMz, (ReadOnlyMemory<double>)low);
            await group.WriteAsync(HighMz, (ReadOnlyMemory<double>)high);
            await group.WriteAsync(Feature, (ReadOnlyMemory<int>)feature);
            await group.WriteAsync(ScanSignal, (ReadOnlyMemory<double>)scanSignal);
            await group.WriteAsync(ScanSignal2Plus, (ReadOnlyMemory<double>)scanSignal2Plus);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The signal saved at <paramref name="path"/>, or null when there is none, it cannot be read, or it was made
    /// from something other than <paramref name="expected"/> (another version of the raw file, other settings).
    /// </summary>
    public static PeptideLikeSignal? TryRead(string path, Stamp expected, ScanHeader[] headers)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return ReadAsync(path, expected, headers).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or FormatException or IndexOutOfRangeException or ArgumentException or InvalidOperationException
            or NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is a .hk file made from <paramref name="expected"/>. Reads only the file's
    /// footer, so it can decide before a pass whether the search is needed.
    /// </summary>
    public static bool Matches(string path, Stamp expected)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            return MatchesAsync(path, expected).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or FormatException or ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        {
            return false;
        }
    }

    private static async Task<bool> MatchesAsync(string path, Stamp expected)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var reader = await ParquetReader.CreateAsync(stream);
        return Matches(reader.CustomMetadata, expected);
    }

    private static bool Matches(IReadOnlyDictionary<string, string> meta, Stamp expected)
    {
        string Get(string key) => meta.TryGetValue(key, out var v) ? v : "";
        return Get(FormatKey) == FormatVersion.ToString(CultureInfo.InvariantCulture)
            && Get(RawSizeKey) == expected.RawSize.ToString(CultureInfo.InvariantCulture)
            && Get(RawModifiedKey) == expected.RawModifiedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            && Get(SpectraKey) == expected.SpectrumCount.ToString(CultureInfo.InvariantCulture)
            && Get(SettingsKey) == expected.Settings;
    }

    private static async Task<PeptideLikeSignal?> ReadAsync(string path, Stamp expected, ScanHeader[] headers)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var reader = await ParquetReader.CreateAsync(stream);
        var meta = reader.CustomMetadata;
        string Get(string key) => meta.TryGetValue(key, out var v) ? v : "";
        if (!Matches(meta, expected) || headers.Length != expected.SpectrumCount)
            return null;
        var seriesList = JsonSerializer.Deserialize<int[][]>(Get(SeriesKey)) ?? throw new InvalidDataException("No series.");
        var dda = Get(DdaKey) == "true";

        // Every searched scan, with no envelopes until rows say otherwise.
        var rows = new Dictionary<int, List<(EnvelopeKey Key, EnvelopeDetail Detail, int Feature)>>();
        var all = new double[headers.Length];
        var multiplyCharged = new double[headers.Length];
        Array.Fill(all, double.NaN);
        Array.Fill(multiplyCharged, double.NaN);
        foreach (var s in seriesList)
            foreach (var scan in s)
            {
                if (scan < 0 || scan >= headers.Length)
                    throw new InvalidDataException($"Scan {scan} beyond the run's {headers.Length} spectra.");
                rows[scan] = new();
                all[scan] = 0;                 // searched; rows below give the scans with signal
                multiplyCharged[scan] = 0;
            }

        for (var g = 0; g < reader.RowGroupCount; g++)
        {
            using var group = reader.OpenRowGroupReader(g);
            var count = checked((int)group.RowCount);
            async Task<T[]> Column<T>(DataField<T> field) where T : struct
            {
                var values = new T[count];
                await group.ReadAsync(field, values.AsMemory());
                return values;
            }
            var scan = await Column<int>(Scan);
            var mass = await Column<double>(MonoMass);
            var charge = await Column<int>(Charge);
            var intensity = await Column<float>(Intensity);
            var basePeak = await Column<double>(BasePeakMz);
            var correlation = await Column<float>(Correlation);
            var low = await Column<double>(LowMz);
            var high = await Column<double>(HighMz);
            var feature = await Column<int>(Feature);
            var scanSignal = await Column<double>(ScanSignal);
            var scanSignal2Plus = await Column<double>(ScanSignal2Plus);
            for (var r = 0; r < scan.Length; r++)
            {
                if (!rows.TryGetValue(scan[r], out var list))
                    throw new InvalidDataException($"Envelope in scan {scan[r]}, which no series searched.");
                list.Add((new EnvelopeKey(mass[r], charge[r], intensity[r]), new EnvelopeDetail(basePeak[r], correlation[r], low[r], high[r]), feature[r]));
                all[scan[r]] = scanSignal[r];
                multiplyCharged[scan[r]] = scanSignal2Plus[r];
            }
        }

        var envelopes = new Dictionary<int, ScanEnvelopes>();
        var featureOf = new Dictionary<int, int[]>();
        foreach (var (scan, list) in rows)
        {
            featureOf[scan] = list.Select(x => x.Feature).ToArray();
            envelopes[scan] = list.Count == 0
                ? ScanEnvelopes.Empty
                : ScanEnvelopes.Build(list.Select(x => x.Key).ToArray(), list.Select(x => x.Detail).ToArray(),
                    list.Select(_ => Array.Empty<int>()).ToArray(), Array.Empty<int>(), Array.Empty<float>());
        }

        // The features, from their envelopes: the seed is the most intense.
        var position = new Dictionary<int, (int Series, int Position)>();
        for (var s = 0; s < seriesList.Length; s++)
            for (var k = 0; k < seriesList[s].Length; k++)
                position[seriesList[s][k]] = (s, k);
        var members = new SortedDictionary<int, List<(int Position, EnvelopeKey Key, int Series)>>();
        foreach (var (scan, ids) in featureOf)
            for (var e = 0; e < ids.Length; e++)
            {
                if (ids[e] < 0)
                    continue;
                if (!members.TryGetValue(ids[e], out var m))
                    members[ids[e]] = m = new();
                m.Add((position[scan].Position, envelopes[scan].Keys[e], position[scan].Series));
            }
        var features = new List<(int Series, PersistentFeature Feature)>();
        foreach (var (id, m) in members)
        {
            if (id != features.Count)
                throw new InvalidDataException($"Feature {id} out of sequence.");
            var seed = m.OrderByDescending(x => x.Key.Intensity).ThenBy(x => x.Position).First();
            features.Add((seed.Series, new PersistentFeature(seed.Key.MonoMass, seed.Key.Charge, seed.Position, seed.Key.Intensity,
                m.Min(x => x.Position), m.Max(x => x.Position), m.Count, Kronik.SummedIntensity(m.Select(x => (x.Position, x.Key.Intensity))))));
        }
        return new PeptideLikeSignal(headers, envelopes, featureOf, features, seriesList, dda, all, multiplyCharged);
    }
}
