using System.Globalization;
using System.Text;

namespace Glimpse.Core.Document;

/// <summary>
/// A minimal RFC 4180 CSV reader for Skyline report exports (invariant culture): quoted fields,
/// doubled quotes, embedded commas and newlines.
/// </summary>
public sealed class CsvTable
{
    private readonly Dictionary<string, int> _columns;

    private CsvTable(string[] header, List<string[]> rows)
    {
        Header = header;
        Rows = rows;
        _columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
            _columns.TryAdd(header[i], i);
    }

    /// <summary>A table from rows already in hand (a report read from Parquet), with CSV's string cells.</summary>
    public static CsvTable FromRows(string[] header, List<string[]> rows) => new(header, rows);

    public string[] Header { get; }

    public IReadOnlyList<string[]> Rows { get; }

    public static CsvTable Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Read(reader);
    }

    public static CsvTable Read(TextReader reader)
    {
        var records = ParseRecords(reader).GetEnumerator();
        if (!records.MoveNext())
            return new CsvTable(Array.Empty<string>(), new List<string[]>());
        var header = records.Current;
        var rows = new List<string[]>();
        while (records.MoveNext())
            rows.Add(records.Current);
        return new CsvTable(header, rows);
    }

    /// <summary>Index of the first column whose header is one of <paramref name="names"/>, or -1.</summary>
    public int Column(params string[] names)
    {
        foreach (var n in names)
        {
            if (_columns.TryGetValue(n, out var i))
                return i;
        }
        return -1;
    }

    public static string Get(string[] row, int column) => column >= 0 && column < row.Length ? row[column] : "";

    public static double GetDouble(string[] row, int column) =>
        double.TryParse(Get(row, column), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    public static double? GetNullableDouble(string[] row, int column) =>
        double.TryParse(Get(row, column), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static int GetInt(string[] row, int column) =>
        int.TryParse(Get(row, column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static IEnumerable<string[]> ParseRecords(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var any = false;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            var ch = (char)c;
            any = true;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }
            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return fields.ToArray();
                    fields.Clear();
                    any = false;
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }
        if (any || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }
}
