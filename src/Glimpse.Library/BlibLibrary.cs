using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using Glimpse.Core.Chemistry;
using Glimpse.Core.Library;
using Microsoft.Data.Sqlite;

namespace Glimpse.Library;

/// <summary>
/// A BiblioSpec .blib library. Opening reads only the RefSpectra metadata (tens of thousands of rows,
/// well under a second); peaks are read per spectrum when first asked for.
/// </summary>
/// <remarks>
/// <para>Every connection is opened read-only with <c>Pooling=False</c>, so no handle lingers in the
/// connection pool and locks the user's library against Skyline.</para>
/// <para>Peptides are matched on <see cref="ModifiedSequence.MatchKey"/>: Skyline reports
/// <c>C[+57.021464]</c> and BiblioSpec stores <c>C[+57.02146]</c>, so masses are compared to 0.01 Da.</para>
/// </remarks>
public sealed class BlibLibrary : ISpectralLibrary
{
    private readonly string _path;
    private readonly Dictionary<string, Entry> _byKey;
    private readonly ConcurrentDictionary<long, (double[] Mz, double[] Intensity)> _peaks = new();

    private BlibLibrary(string path, Dictionary<string, Entry> byKey)
    {
        _path = path;
        _byKey = byKey;
    }

    public string Name => Path.GetFileName(_path);

    public int Count => _byKey.Count;

    public static BlibLibrary Open(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Spectral library not found.", path);

        using var conn = OpenConnection(path);
        var files = new Dictionary<long, (string? File, string? IdFile)>();
        if (HasTable(conn, "SpectrumSourceFiles"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM SpectrumSourceFiles";
            using var r = cmd.ExecuteReader();
            var cId = Ordinal(r, "id");
            var cFile = Ordinal(r, "fileName");
            var cIdFile = Ordinal(r, "idFileName");
            while (r.Read())
                files[r.GetInt64(cId)] = (Str(r, cFile), Str(r, cIdFile));
        }

        var scoreTypes = new Dictionary<long, string>();
        if (HasTable(conn, "ScoreTypes"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, scoreType FROM ScoreTypes";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                scoreTypes[r.GetInt64(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
        }

        var byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM RefSpectra";
            using var r = cmd.ExecuteReader();
            int cId = Ordinal(r, "id"), cSeq = Ordinal(r, "peptideSeq"), cMod = Ordinal(r, "peptideModSeq"),
                cZ = Ordinal(r, "precursorCharge"), cMz = Ordinal(r, "precursorMZ"), cRt = Ordinal(r, "retentionTime"),
                cCopies = Ordinal(r, "copies"), cFile = Ordinal(r, "fileID"), cScore = Ordinal(r, "score"),
                cScoreType = Ordinal(r, "scoreType"), cSpecId = Ordinal(r, "SpecIDinFile"), cNum = Ordinal(r, "numPeaks");
            while (r.Read())
            {
                var mod = Str(r, cMod) ?? Str(r, cSeq);
                var parsed = ModifiedSequence.TryParse(mod);
                if (parsed is null)
                    continue;
                var charge = Int(r, cZ) ?? 0;
                var key = Key(parsed.MatchKey, charge);
                if (byKey.ContainsKey(key))
                    continue; // a redundant library: keep the first (best) entry
                var fileId = Int(r, cFile);
                var (srcFile, idFile) = fileId is { } f && files.TryGetValue(f, out var info) ? info : (null, null);
                var scoreTypeId = Int(r, cScoreType);
                byKey[key] = new Entry(r.GetInt64(cId), Int(r, cNum) ?? 0, new LibrarySpectrum
                {
                    LibraryName = Path.GetFileName(path),
                    ModifiedSequence = mod ?? "",
                    Charge = charge,
                    PrecursorMz = Dbl(r, cMz) ?? double.NaN,
                    RetentionTime = Dbl(r, cRt),
                    SourceFile = srcFile,
                    IdFile = idFile,
                    Score = Dbl(r, cScore),
                    ScoreType = scoreTypeId is { } st && scoreTypes.TryGetValue(st, out var name) ? name : null,
                    Copies = Int(r, cCopies),
                    SpectrumIdInFile = Str(r, cSpecId),
                });
            }
        }
        return new BlibLibrary(path, byKey);
    }

    public LibrarySpectrum? Find(string modifiedSequence, int charge)
    {
        var parsed = ModifiedSequence.TryParse(modifiedSequence);
        if (parsed is null || !_byKey.TryGetValue(Key(parsed.MatchKey, charge), out var entry))
            return null;
        var peaks = _peaks.GetOrAdd(entry.Id, id => ReadPeaks(id, entry.PeakCount));
        return entry.Metadata with { Mz = peaks.Mz, Intensity = peaks.Intensity };
    }

    private (double[] Mz, double[] Intensity) ReadPeaks(long id, int numPeaks)
    {
        using var conn = OpenConnection(_path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT peakMZ, peakIntensity FROM RefSpectraPeaks WHERE RefSpectraID = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0) || r.IsDBNull(1))
            return (Array.Empty<double>(), Array.Empty<double>());
        var mzBytes = Inflate((byte[])r.GetValue(0), numPeaks * 8);
        var intBytes = Inflate((byte[])r.GetValue(1), numPeaks * 4);
        var n = Math.Min(mzBytes.Length / 8, intBytes.Length / 4);
        var mz = new double[n];
        var intensity = new double[n];
        double max = 0;
        for (var i = 0; i < n; i++)
        {
            mz[i] = BitConverter.ToDouble(mzBytes, i * 8);
            intensity[i] = BitConverter.ToSingle(intBytes, i * 4);
            max = Math.Max(max, intensity[i]);
        }
        if (max > 0)
        {
            for (var i = 0; i < n; i++)
                intensity[i] /= max;
        }
        Array.Sort(mz, intensity);
        return (mz, intensity);
    }

    /// <summary>
    /// BiblioSpec compresses a peak array only when that makes it smaller, and says so by length alone:
    /// a blob shorter than the uncompressed size is zlib.
    /// </summary>
    private static byte[] Inflate(byte[] blob, int expectedLength)
    {
        if (expectedLength <= 0 || blob.Length >= expectedLength)
            return blob;
        try
        {
            using var input = new MemoryStream(blob);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(expectedLength);
            z.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            // Shorter than numPeaks says but not zlib: the count is wrong, not the data. Read it as
            // stored rather than fail the whole spectrum.
            return blob;
        }
    }

    private static SqliteConnection OpenConnection(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static bool HasTable(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$t";
        cmd.Parameters.AddWithValue("$t", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static string Key(string matchKey, int charge) => matchKey + "/" + charge.ToString(CultureInfo.InvariantCulture);

    private static int Ordinal(SqliteDataReader r, string name)
    {
        for (var i = 0; i < r.FieldCount; i++)
        {
            if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static string? Str(SqliteDataReader r, int c) => c < 0 || r.IsDBNull(c) ? null : Convert.ToString(r.GetValue(c), CultureInfo.InvariantCulture);

    private static int? Int(SqliteDataReader r, int c) => c < 0 || r.IsDBNull(c) ? null : Convert.ToInt32(r.GetValue(c), CultureInfo.InvariantCulture);

    private static double? Dbl(SqliteDataReader r, int c) => c < 0 || r.IsDBNull(c) ? null : Convert.ToDouble(r.GetValue(c), CultureInfo.InvariantCulture);

    private sealed record Entry(long Id, int PeakCount, LibrarySpectrum Metadata);
}
