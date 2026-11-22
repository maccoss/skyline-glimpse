#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Glimpse.Core.Document;
using Parquet;
using Parquet.Schema;

namespace Glimpse.Skyline;

/// <summary>
/// Reads a Skyline report exported as Parquet into a <see cref="CsvTable"/>, each cell formatted the way
/// the invariant CSV export would give it, so the document builder reads either.
/// </summary>
/// <remarks>
/// Parquet because Skyline writes it far faster than text: the 620,000 row transition report takes it
/// 3.5 s as Parquet (5 MB) against 11.9 s as CSV (76 MB). The column reading follows Skyline-PRISM's
/// ParquetColumnIo (dotnet/src/SkylinePrism.Core/IO), including its two Parquet.Net 6 findings: a string
/// column reports its CLR type as ReadOnlyMemory&lt;char&gt;, and a nullable column must be read into a
/// nullable buffer.
/// </remarks>
public static class ParquetReport
{
    public static CsvTable Read(string path) => ReadAsync(path).GetAwaiter().GetResult();

    private static async Task<CsvTable> ReadAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using var reader = await ParquetReader.CreateAsync(stream);
        var fields = reader.Schema.GetDataFields();
        var header = new string[fields.Length];
        for (var c = 0; c < fields.Length; c++)
            header[c] = fields[c].Name;

        var rows = new List<string[]>();
        for (var g = 0; g < reader.RowGroupCount; g++)
        {
            using var group = reader.OpenRowGroupReader(g);
            var count = checked((int)group.RowCount);
            var start = rows.Count;
            for (var r = 0; r < count; r++)
                rows.Add(new string[fields.Length]);
            for (var c = 0; c < fields.Length; c++)
            {
                var values = await ReadColumnAsync(group, fields[c], count);
                for (var r = 0; r < count; r++)
                    rows[start + r][c] = Format(values.GetValue(r));
            }
        }
        return CsvTable.FromRows(header, rows);
    }

    /// <summary>A cell as the invariant CSV export writes it; null is an empty cell.</summary>
    internal static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        // .NET's default double and float formatting is already the shortest string that round-trips.
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        DateTime t => t.ToString("O", CultureInfo.InvariantCulture),
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static async Task<Array> ReadColumnAsync(ParquetRowGroupReader group, DataField field, int count)
    {
        var type = field.ClrType;
        if (type == typeof(string) || type == typeof(ReadOnlyMemory<char>))
        {
            var strings = new string?[count];
            await group.ReadAsync(field, strings.AsMemory());
            return strings;
        }
        if (type == typeof(double)) return await ReadValuesAsync<double>(group, field, count);
        if (type == typeof(float)) return await ReadValuesAsync<float>(group, field, count);
        if (type == typeof(int)) return await ReadValuesAsync<int>(group, field, count);
        if (type == typeof(long)) return await ReadValuesAsync<long>(group, field, count);
        if (type == typeof(bool)) return await ReadValuesAsync<bool>(group, field, count);
        if (type == typeof(DateTime)) return await ReadValuesAsync<DateTime>(group, field, count);
        throw new NotSupportedException($"Report column '{field.Name}' has type {type.Name}, which Glimpse does not read.");
    }

    private static async Task<Array> ReadValuesAsync<T>(ParquetRowGroupReader group, DataField field, int count)
        where T : struct
    {
        if (field.IsNullable)
        {
            var nullable = new T?[count];
            await group.ReadAsync(field, nullable.AsMemory());
            return nullable;
        }
        var plain = new T[count];
        await group.ReadAsync(field, plain.AsMemory());
        return plain;
    }
}
