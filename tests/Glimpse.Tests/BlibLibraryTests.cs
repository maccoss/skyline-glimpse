using System.IO.Compression;
using Glimpse.Library;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glimpse.Tests;

/// <summary>A .blib written by the test, so the reader is covered without anyone's data files.</summary>
public sealed class BlibLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glimpse-blib-" + Guid.NewGuid().ToString("N"));

    public BlibLibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(bool withMetadataTables)
    {
        var path = Path.Combine(_dir, withMetadataTables ? "full.blib" : "bare.blib");
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        void Exec(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Exec("CREATE TABLE RefSpectra (id INTEGER PRIMARY KEY, peptideSeq TEXT, peptideModSeq TEXT, precursorCharge INTEGER, precursorMZ REAL, "
            + "retentionTime REAL, numPeaks INTEGER, copies INTEGER, fileID INTEGER, score REAL, scoreType INTEGER, SpecIDinFile TEXT)");
        Exec("CREATE TABLE RefSpectraPeaks (RefSpectraID INTEGER, peakMZ BLOB, peakIntensity BLOB)");
        if (withMetadataTables)
        {
            Exec("CREATE TABLE SpectrumSourceFiles (id INTEGER PRIMARY KEY, fileName TEXT, idFileName TEXT)");
            Exec("CREATE TABLE ScoreTypes (id INTEGER PRIMARY KEY, scoreType TEXT)");
            Exec("INSERT INTO SpectrumSourceFiles VALUES (1, 'run1.raw', 'search.pin')");
            Exec("INSERT INTO ScoreTypes VALUES (19, 'GENERIC Q-VALUE')");
        }

        void Add(int id, string seq, string modSeq, int z, double[] mz, float[] intensity, bool compress)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO RefSpectra VALUES ($id,$seq,$mod,$z,500.25,12.5,$n,3,1,0.001,19,'42')";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$mod", modSeq);
            cmd.Parameters.AddWithValue("$z", z);
            cmd.Parameters.AddWithValue("$n", mz.Length);
            cmd.ExecuteNonQuery();
            var mzBytes = mz.SelectMany(BitConverter.GetBytes).ToArray();
            var intBytes = intensity.SelectMany(BitConverter.GetBytes).ToArray();
            using var peaks = conn.CreateCommand();
            peaks.CommandText = "INSERT INTO RefSpectraPeaks VALUES ($id,$mz,$int)";
            peaks.Parameters.AddWithValue("$id", id);
            peaks.Parameters.AddWithValue("$mz", compress ? Zlib(mzBytes) : mzBytes);
            peaks.Parameters.AddWithValue("$int", compress ? Zlib(intBytes) : intBytes);
            peaks.ExecuteNonQuery();
        }

        // Unsorted peaks, compressed (many repeated bytes compress well).
        var many = Enumerable.Range(0, 50).Select(i => 300.0 + (49 - i)).ToArray();
        Add(1, "AACK", "AAC[+57.02146]K", 2, many, many.Select(m => (float)(m - 290)).ToArray(), compress: true);
        Add(2, "PEPTIDE", "PEPTIDE", 2, new[] { 200.0, 300.0 }, new[] { 2f, 8f }, compress: false);
        Add(3, "PEPTIDE", "PEPTIDE", 2, new[] { 999.0 }, new[] { 1f }, compress: false);   // redundant: first one wins
        Add(4, "XYZ", "X[Oxidation]YZ", 2, new[] { 1.0 }, new[] { 1f }, compress: false);  // unparseable: skipped
        return path;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal))
            z.Write(data, 0, data.Length);
        return output.ToArray();
    }

    [Fact]
    public void ReadsMetadataAndBothPeakEncodings()
    {
        var lib = BlibLibrary.Open(Write(withMetadataTables: true));
        Assert.Equal(2, lib.Count);
        Assert.Equal("full.blib", lib.Name);

        // Skyline's six-decimal mass finds BiblioSpec's five-decimal entry.
        var aack = lib.Find("AAC[+57.021464]K", 2)!;
        Assert.Equal(50, aack.Mz.Length);
        Assert.True(aack.Mz.Zip(aack.Mz.Skip(1)).All(p => p.First < p.Second), "peaks sorted by m/z");
        Assert.Equal(1.0, aack.Intensity.Max(), 9);
        Assert.Equal(10.0 / 59.0, aack.Intensity[0], 6);      // m/z 300 -> 10, normalized by 59
        Assert.Equal("run1.raw", aack.SourceFile);
        Assert.Equal("search.pin", aack.IdFile);
        Assert.Equal("GENERIC Q-VALUE", aack.ScoreType);
        Assert.Equal(0.001, aack.Score);
        Assert.Equal(3, aack.Copies);
        Assert.Equal(12.5, aack.RetentionTime);
        Assert.Equal("42", aack.SpectrumIdInFile);
        Assert.Equal(500.25, aack.PrecursorMz);

        var peptide = lib.Find("PEPTIDE", 2)!;
        Assert.Equal(new[] { 200.0, 300.0 }, peptide.Mz);
        Assert.Equal(new[] { 0.25, 1.0 }, peptide.Intensity);
        Assert.Same(lib.Find("PEPTIDE", 2)!.Mz, peptide.Mz);   // cached

        Assert.Null(lib.Find("PEPTIDE", 3));
        Assert.Null(lib.Find("NOTTHERE", 2));
        Assert.Null(lib.Find("bad[name]", 2));
    }

    [Fact]
    public void WorksWithoutOptionalTables()
    {
        var lib = BlibLibrary.Open(Write(withMetadataTables: false));
        var entry = lib.Find("PEPTIDE", 2)!;
        Assert.Null(entry.SourceFile);
        Assert.Null(entry.ScoreType);
    }

    [Fact]
    public void MissingFileThrows() =>
        Assert.Throws<FileNotFoundException>(() => BlibLibrary.Open(Path.Combine(_dir, "nope.blib")));

    [Fact]
    public void DoesNotLockTheFile()
    {
        var path = Write(withMetadataTables: true);
        var lib = BlibLibrary.Open(path);
        lib.Find("PEPTIDE", 2);
        // Pooling=False: no connection lingers, so the file can be replaced while Glimpse has it open.
        File.Move(path, path + ".moved");
        Assert.True(File.Exists(path + ".moved"));
    }
}
