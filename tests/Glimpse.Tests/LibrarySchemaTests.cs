using Glimpse.Library;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glimpse.Tests;

/// <summary>
/// BiblioSpec has changed its schema over the years (columns added, order differing). The reader must
/// find columns by name and treat missing ones as absent.
/// </summary>
public sealed class LibrarySchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glimpse-blib2-" + Guid.NewGuid().ToString("N"));

    public LibrarySchemaTests() => Directory.CreateDirectory(_dir);

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

    private string Create(string name, string refSpectraColumns, Action<SqliteConnection> fill)
    {
        var path = Path.Combine(_dir, name);
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"CREATE TABLE RefSpectra ({refSpectraColumns}); CREATE TABLE RefSpectraPeaks (RefSpectraID INTEGER, peakMZ BLOB, peakIntensity BLOB);";
            cmd.ExecuteNonQuery();
        }
        fill(conn);
        return path;
    }

    private static void Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static byte[] Doubles(params double[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();

    private static byte[] Floats(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();

    [Fact]
    public void MinimalOldSchemaUsesThePlainSequence()
    {
        var path = Create("old.blib", "id INTEGER PRIMARY KEY, peptideSeq TEXT, peptideModSeq TEXT, precursorCharge INTEGER, precursorMZ REAL, numPeaks INTEGER", c =>
        {
            Exec(c, "INSERT INTO RefSpectra VALUES (1, 'PEPTIDE', NULL, 2, 400.69, 2)");
            Exec(c, "INSERT INTO RefSpectraPeaks VALUES (1, $m, $i)", ("$m", Doubles(200, 300)), ("$i", Floats(1, 4)));
        });
        var lib = BlibLibrary.Open(path);
        var e = lib.Find("PEPTIDE", 2)!;
        Assert.Equal(new[] { 200.0, 300.0 }, e.Mz);
        Assert.Equal("PEPTIDE", e.ModifiedSequence);
        Assert.Null(e.RetentionTime);
        Assert.Null(e.Copies);
        Assert.Null(e.ScoreType);
        Assert.Null(e.SourceFile);
        Assert.Null(e.SpectrumIdInFile);
        Assert.Null(e.Score);
    }

    [Theory]
    [InlineData("peptideModSeq TEXT, id INTEGER PRIMARY KEY, peptideSeq TEXT, precursorCharge INTEGER, precursorMZ REAL, numPeaks INTEGER")]
    [InlineData("precursorCharge INTEGER, id INTEGER PRIMARY KEY, peptideSeq TEXT, peptideModSeq TEXT, precursorMZ REAL, numPeaks INTEGER")]
    [InlineData("precursorMZ REAL, id INTEGER PRIMARY KEY, peptideSeq TEXT, peptideModSeq TEXT, precursorCharge INTEGER, numPeaks INTEGER")]
    public void ColumnsAreFoundByNameInAnyOrder(string columns)
    {
        var path = Create($"order-{Guid.NewGuid():N}.blib", columns, c =>
        {
            Exec(c, "INSERT INTO RefSpectra (id, peptideSeq, peptideModSeq, precursorCharge, precursorMZ, numPeaks) VALUES (7, 'AAK', 'AAK', 3, 123.4, 1)");
            Exec(c, "INSERT INTO RefSpectraPeaks VALUES (7, $m, $i)", ("$m", Doubles(150)), ("$i", Floats(2)));
        });
        var e = BlibLibrary.Open(path).Find("AAK", 3)!;
        Assert.Equal(123.4, e.PrecursorMz, 9);
        Assert.Equal(3, e.Charge);
        Assert.Equal(new[] { 150.0 }, e.Mz);
    }

    [Fact]
    public void NullsAndBrokenPeakRowsAreHandled()
    {
        var path = Create("nulls.blib", "id INTEGER PRIMARY KEY, peptideSeq TEXT, peptideModSeq TEXT, precursorCharge INTEGER, precursorMZ REAL, numPeaks INTEGER, scoreType INTEGER, SpecIDinFile TEXT, retentionTime REAL", c =>
        {
            Exec(c, "CREATE TABLE ScoreTypes (id INTEGER PRIMARY KEY, scoreType TEXT)");
            Exec(c, "INSERT INTO ScoreTypes VALUES (5, NULL)");
            Exec(c, "INSERT INTO RefSpectra VALUES (1, 'AAK', 'AAK', 2, 1, 2, 5, NULL, NULL)");    // no peaks row at all
            Exec(c, "INSERT INTO RefSpectra VALUES (2, 'CCK', 'CCK', 2, 1, 2, 5, 'x', 1.5)");
            Exec(c, "INSERT INTO RefSpectraPeaks VALUES (2, $m, NULL)", ("$m", Doubles(1, 2)));     // no intensities
            Exec(c, "INSERT INTO RefSpectra VALUES (3, 'DDK', 'DDK', 2, 1, 3, 5, 'y', 2.5)");
            Exec(c, "INSERT INTO RefSpectraPeaks VALUES (3, $m, $i)", ("$m", Doubles(1, 2, 3)), ("$i", Floats(0, 0)));  // lengths disagree; all zero
            Exec(c, "INSERT INTO RefSpectra VALUES (4, 'EEK', 'EEK', 2, 1, 2, 5, 'z', 3.5)");
            Exec(c, "INSERT INTO RefSpectraPeaks VALUES (4, NULL, $i)", ("$i", Floats(1, 2)));     // no m/z
        });
        var lib = BlibLibrary.Open(path);
        var a = lib.Find("AAK", 2)!;
        Assert.Empty(a.Mz);
        Assert.Equal("", a.ScoreType);                   // a score type row with no name
        Assert.Null(a.SpectrumIdInFile);
        Assert.Null(a.RetentionTime);
        Assert.Empty(lib.Find("CCK", 2)!.Mz);
        Assert.Empty(lib.Find("EEK", 2)!.Mz);
        var d = lib.Find("DDK", 2)!;
        Assert.Equal(new[] { 1.0, 2.0 }, d.Mz);           // as many peaks as both arrays hold
        Assert.Equal(new[] { 0.0, 0.0 }, d.Intensity);    // all-zero stays zero, not NaN
        Assert.Equal(2.5, d.RetentionTime);
    }
}
