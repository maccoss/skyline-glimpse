using Parquet;
using Parquet.Schema;

namespace Glimpse.Tests;

/// <summary>Writes a report the way Skyline does for the extension asked for: Parquet for .parquet, else CSV.</summary>
internal static class ReportFiles
{
    public static void Write(string path, string[] header, IReadOnlyList<string[]> rows)
    {
        if (!path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, string.Join(",", header) + "\n" + string.Concat(rows.Select(r => string.Join(",", r) + "\n")));
            return;
        }
        WriteParquet(path, header.Select(h => (Field)new DataField<string>(h)).ToArray(),
            header.Select((_, c) => (Array)rows.Select(r => r[c]).ToArray()).ToArray());
    }

    /// <summary>A Parquet file of the given typed columns (string[], double[], double?[], int[] ...).</summary>
    public static void WriteParquet(string path, Field[] fields, Array[] columns) =>
        WriteParquetAsync(path, fields, columns).GetAwaiter().GetResult();

    private static async Task WriteParquetAsync(string path, Field[] fields, Array[] columns)
    {
        var schema = new ParquetSchema(fields);
        using var stream = File.Create(path);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream);
        using var group = writer.CreateRowGroup();
        var dataFields = schema.GetDataFields();
        for (var c = 0; c < columns.Length; c++)
        {
            var field = dataFields[c];
            switch (columns[c])
            {
                case string[] s: await group.WriteAsync(field, s); break;
                case double[] d: await group.WriteAsync(field, (ReadOnlyMemory<double>)d); break;
                case double?[] dn: await group.WriteAsync(field, (ReadOnlyMemory<double?>)dn); break;
                case float[] f: await group.WriteAsync(field, (ReadOnlyMemory<float>)f); break;
                case int[] i: await group.WriteAsync(field, (ReadOnlyMemory<int>)i); break;
                case int?[] inn: await group.WriteAsync(field, (ReadOnlyMemory<int?>)inn); break;
                case long[] l: await group.WriteAsync(field, (ReadOnlyMemory<long>)l); break;
                case bool[] b: await group.WriteAsync(field, (ReadOnlyMemory<bool>)b); break;
                case DateTime[] t: await group.WriteAsync(field, (ReadOnlyMemory<DateTime>)t); break;
                case decimal[] m: await group.WriteAsync(field, (ReadOnlyMemory<decimal>)m); break;
                default: throw new NotSupportedException(columns[c].GetType().Name);
            }
        }
    }
}
