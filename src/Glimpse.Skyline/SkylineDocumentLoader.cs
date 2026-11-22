#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Glimpse.Core.Document;
using SkylineTool;

namespace Glimpse.Skyline;

/// <summary>
/// Reads the connected Skyline document into a <see cref="DocumentModel"/> in steps that can each be
/// shown as they arrive: the result files (a tenth of a second), then the targets and transitions, and
/// each file's peak boundaries only when that file is opened.
/// </summary>
/// <remarks>
/// <para>Measured on a 69,000 precursor, 620,000 transition, 17 replicate document: result files 0.1 s,
/// transitions 3.5 s, one file's peaks 3.6 s. Two things made the difference, both found by timing
/// report shapes against a live Skyline: no locator columns (they cost Skyline 36 s a report; see
/// <see cref="SkylineLocators"/>), and Parquet rather than CSV (writing text was most of the rest).</para>
/// <para>Peaks are per file: all 17 replicates in one report took 28 s, so opening one file would wait
/// eight times longer than it needs to.</para>
/// </remarks>
public static class SkylineDocumentLoader
{
    /// <summary>The whole document: result files, then targets and transitions.</summary>
    public static DocumentModel Load(SkylineConnection skyline, Action<string>? log = null)
    {
        var documentPath = NullIfEmpty(skyline.GetDocumentPath());
        var files = LoadResultFiles(skyline, documentPath, log);
        return LoadTargets(skyline, documentPath, files, log);
    }

    /// <summary>
    /// The document's result files with usable paths (see <see cref="ResolvePaths"/>). Empty for a
    /// document without results.
    /// </summary>
    public static IReadOnlyList<ResultFileInfo> LoadResultFiles(SkylineConnection skyline, string? documentPath, Action<string>? log = null)
    {
        var path = TempReport("files");
        try
        {
            var clock = Stopwatch.StartNew();
            Export(skyline, new ReportDefinition { Name = "Glimpse Files", Select = DocumentModelBuilder.ResultFileColumns, DataSource = "document_grid", PivotReplicate = false }, path);
            var files = DocumentModelBuilder.ReadResultFiles(ReadReport(path));
            log?.Invoke($"Result file report exported in {clock.Elapsed.TotalSeconds:0.0} s: {files.Count:N0} files");
            return ResolvePaths(files, documentPath);
        }
        catch (Exception ex)
        {
            // A document without results has no files to list; that is not a failure.
            log?.Invoke("Result file report unavailable: " + ex.Message);
            return Array.Empty<ResultFileInfo>();
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>Proteins, peptides, precursors and transitions, with the given result files.</summary>
    public static DocumentModel LoadTargets(SkylineConnection skyline, string? documentPath, IReadOnlyList<ResultFileInfo> files, Action<string>? log = null)
    {
        var path = TempReport("transitions");
        try
        {
            var clock = Stopwatch.StartNew();
            Export(skyline, new ReportDefinition { Name = "Glimpse Transitions", Select = DocumentModelBuilder.TransitionColumns, DataSource = "document_grid", PivotReplicate = false }, path);
            var exported = clock.Elapsed.TotalSeconds;
            var model = DocumentModelBuilder.Build(documentPath, ReadReport(path), null).WithResultFiles(files);
            log?.Invoke($"Transition report exported in {exported:0.0} s, read in {clock.Elapsed.TotalSeconds - exported:0.0} s");
            return model;
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// Fetch Skyline's integrated peaks for one result file and add them to <paramref name="model"/>.
    /// The report is filtered to the file's replicate (see <see cref="DocumentModelBuilder.PeakColumns"/>
    /// for why not to the file itself). Returns the number of precursor peaks added.
    /// </summary>
    public static int LoadPeaks(SkylineConnection skyline, DocumentModel model, ResultFileInfo file, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(file.ReplicateName))
            throw new InvalidOperationException($"No replicate is known for {file.FileName}.");
        var path = TempReport("peaks");
        try
        {
            var clock = Stopwatch.StartNew();
            Export(skyline, new ReportDefinition
            {
                Name = "Glimpse Peaks",
                Select = DocumentModelBuilder.PeakColumns,
                DataSource = "document_grid", PivotReplicate = false,
                Filter = new[] { new ReportFilter { Column = "ReplicateName", Op = "equals", Value = file.ReplicateName } },
            }, path);
            var added = DocumentModelBuilder.ApplyPeaks(model, ReadReport(path), file.FileName, out var ambiguous);
            log?.Invoke($"Peaks for {file.FileName} exported in {clock.Elapsed.TotalSeconds:0.0} s: {added:N0} precursors");
            if (ambiguous > 0)
                log?.Invoke($"Replicate {file.ReplicateName} gave {ambiguous:N0} precursors more than one peak (it holds several files); "
                    + $"those precursors have no peak in {file.FileName}.");
            return added;
        }
        finally
        {
            TryDelete(path);
        }
    }

    // PivotReplicate = false on every report: left unset, Skyline pivots results by replicate, putting every
    // replicate's values on every row. Combined with the per-file columns that multiplied a 69,000
    // precursor, 17 replicate peak report into tens of gigabytes.

    /// <summary>A unique temporary report path: two loads in the same millisecond must not share a file.</summary>
    private static string TempReport(string kind)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SkylineGlimpse");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{kind}-{Environment.ProcessId}-{Guid.NewGuid():N}.parquet");
    }

    /// <summary>
    /// A report Skyline wrote: Parquet, or CSV from a Skyline too old to write Parquet (it picks the format
    /// from the extension and falls back to CSV for one it does not know).
    /// </summary>
    internal static CsvTable ReadReport(string path) =>
        IsParquet(path) ? ParquetReport.Read(path) : CsvTable.Read(path);

    /// <summary>Parquet files start with the magic bytes "PAR1".</summary>
    internal static bool IsParquet(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var magic = new byte[4];
        return stream.Read(magic, 0, 4) == 4 && magic[0] == (byte)'P' && magic[1] == (byte)'A' && magic[2] == (byte)'R' && magic[3] == (byte)'1';
    }

    /// <summary>
    /// The live document's settings (tolerances, libraries) from the tool service; the saved .sky file
    /// when that fails; Skyline's defaults when neither is available.
    /// </summary>
    public static DocumentSettings LoadSettings(SkylineConnection skyline, string? documentPath, Action<string>? log = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "SkylineGlimpse", $"settings-{Environment.ProcessId}.xml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            skyline.Call(c => c.GetDocumentSettings(path));
            if (File.Exists(path))
                return DocumentSettings.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            log?.Invoke("Live document settings unavailable: " + ex.Message);
        }
        finally
        {
            TryDelete(path);
        }
        try
        {
            if (documentPath is not null && File.Exists(documentPath))
                return DocumentSettings.Parse(File.ReadAllText(documentPath));
        }
        catch (Exception ex)
        {
            log?.Invoke("Could not read settings from the .sky file: " + ex.Message);
        }
        return DocumentSettings.Default;
    }

    /// <summary>
    /// The first BiblioSpec library of the document that can be found on disk: the settings' file name
    /// as given, then next to the document; finally a .blib named after the document.
    /// </summary>
    public static string? FindLibrary(DocumentSettings settings, string? documentPath)
    {
        var docDir = documentPath is null ? null : Path.GetDirectoryName(documentPath);
        foreach (var hint in settings.LibraryFiles)
        {
            if (!hint.EndsWith(".blib", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Path.IsPathRooted(hint) && File.Exists(hint))
                return hint;
            if (docDir is not null && File.Exists(Path.Combine(docDir, Path.GetFileName(hint))))
                return Path.Combine(docDir, Path.GetFileName(hint));
        }
        if (documentPath is not null)
        {
            var sameName = Path.ChangeExtension(documentPath, ".blib");
            if (File.Exists(sameName))
                return sameName;
        }
        return null;
    }

    /// <summary>
    /// How long a report may take. The largest measured, every replicate's peaks of a 69,000 precursor
    /// document, took 28 s; this leaves room for slower machines and bigger documents.
    /// </summary>
    internal static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(15);

    private static void Export(SkylineConnection skyline, ReportDefinition definition, string path)
    {
        skyline.Call(c => c.ExportReportFromDefinition(definition, path, "invariant"), ReportTimeout);
        if (!File.Exists(path))
            throw new IOException($"Skyline did not write the {definition.Name} report.");
    }

    /// <summary>
    /// Fill in a usable path for each result file: Skyline's own when it exists, otherwise a file of the
    /// same name next to the document (the usual layout when a document folder is moved or shared).
    /// </summary>
    internal static DocumentModel WithResolvedPaths(DocumentModel model) =>
        new(model.DocumentPath, model.Proteins, ResolvePaths(model.ResultFiles, model.DocumentPath));

    /// <summary>
    /// Usable paths for result files: Skyline's own when it exists, otherwise a file of the same name next
    /// to the document.
    /// </summary>
    public static IReadOnlyList<ResultFileInfo> ResolvePaths(IEnumerable<ResultFileInfo> resultFiles, string? documentPath)
    {
        var docDir = documentPath is null ? null : Path.GetDirectoryName(documentPath);
        return resultFiles.Select(f =>
        {
            var path = StripSampleSuffix(f.FilePath);
            if (path is not null && (File.Exists(path) || Directory.Exists(path)))
                return f with { FilePath = path };
            if (docDir is not null)
            {
                var candidate = Path.Combine(docDir, f.FileName);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return f with { FilePath = candidate };
            }
            return f with { FilePath = path };
        }).ToList();
    }

    /// <summary>
    /// The file to open first: the active replicate's, else the first file on disk. Null when no result
    /// file is on disk.
    /// </summary>
    public static ResultFileInfo? FirstFileToOpen(IReadOnlyList<ResultFileInfo> files, string? replicate)
    {
        static bool OnDisk(ResultFileInfo f) => f.FilePath is { } p && (File.Exists(p) || Directory.Exists(p));
        return files.FirstOrDefault(f => OnDisk(f) && string.Equals(f.ReplicateName, replicate, StringComparison.Ordinal))
            ?? files.FirstOrDefault(OnDisk);
    }

    /// <summary>Skyline appends "|sample" or "?options" to multi-sample paths; strip them.</summary>
    internal static string? StripSampleSuffix(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var cut = path.IndexOfAny(new[] { '|', '?' });
        return cut >= 0 ? path[..cut] : path;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
