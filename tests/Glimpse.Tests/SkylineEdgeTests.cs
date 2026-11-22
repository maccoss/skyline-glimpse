using Glimpse.Core.Document;
using Glimpse.Skyline;
using Xunit;

namespace Glimpse.Tests;

public sealed class SkylineEdgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glimpse-sky2-" + Guid.NewGuid().ToString("N"));

    public SkylineEdgeTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void ConnectTimeoutDefaultsToFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), SkylineConnection.FromArguments(new[] { "x" })!.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(1), SkylineConnection.FromArguments(new[] { "x" }, TimeSpan.FromSeconds(1))!.Timeout);
    }

    [Fact]
    public void ActionCallsReachSkylineAndLoaderLogsItsSteps()
    {
        using var server = new FakeSkylineServer((method, args) =>
        {
            switch (method)
            {
                case "GetVersion":
                    return "v";
                case "GetDocumentPath":
                    return null;
                default:
                    File.WriteAllText(args[1].GetString()!, "ProteinName,PeptideSequence,PrecursorCharge,PrecursorMz,ProductMz,PrecursorLocator,FileName,ReplicateName,BestRetentionTime\nP,AK,2,200,147,L,a.raw,rep,5\n");
                    return null;
            }
        });
        var skyline = SkylineConnection.FromArguments(new[] { server.RawName })!;
        string? version = null;
        skyline.Call(c => { version = c.GetVersion(); });
        Assert.Equal("v", version);

        var log = new List<string>();
        var model = SkylineDocumentLoader.Load(skyline, log.Add);
        SkylineDocumentLoader.LoadPeaks(skyline, model, model.ResultFiles.Single(), log.Add);
        Assert.Contains(log, l => l.StartsWith("Peaks for a.raw exported in", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("Transition report exported in", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("Result file report exported in", StringComparison.Ordinal) && l.EndsWith("1 files", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsTempFileIsRemovedAndAMissingSkyIsNotAnError()
    {
        using var server = new FakeSkylineServer((_, args) =>
        {
            File.WriteAllText(args[0].GetString()!, "<settings_summary />");
            return args[0].GetString();
        });
        var connection = SkylineConnection.FromArguments(new[] { server.RawName })!;
        SkylineDocumentLoader.LoadSettings(connection, null);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Path.GetTempPath(), "SkylineGlimpse"), $"settings-{Environment.ProcessId}.xml"));

        using var failing = new FakeSkylineServer((_, _) => throw new InvalidOperationException("no"));
        var log = new List<string>();
        SkylineDocumentLoader.LoadSettings(SkylineConnection.FromArguments(new[] { failing.RawName })!, Path.Combine(_dir, "missing.sky"), log.Add);
        Assert.DoesNotContain(log, l => l.Contains("Could not read settings"));
    }

    [Fact]
    public void OnlyBlibHintsAreConsidered()
    {
        var doc = Path.Combine(_dir, "doc.sky");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "");
        var settings = DocumentSettings.Default with { LibraryFiles = new[] { "notes.txt" } };
        Assert.Null(SkylineDocumentLoader.FindLibrary(settings, doc));
        Assert.Null(SkylineDocumentLoader.FindLibrary(settings with { LibraryFiles = new[] { "lib.blib" } }, null));
    }

    [Fact]
    public void ResultFilePathsResolveFilesFoldersAndFallbacks()
    {
        // An existing file elsewhere is used where it is, not looked for next to the document.
        Directory.CreateDirectory(Path.Combine(_dir, "elsewhere"));
        var raw = Path.Combine(_dir, "elsewhere", "a.raw");
        File.WriteAllText(raw, "");
        var dFolder = Path.Combine(_dir, "b.d");
        Directory.CreateDirectory(dFolder);
        var nextToDoc = Path.Combine(_dir, "c.raw");
        File.WriteAllText(nextToDoc, "");
        var files = new[]
        {
            new ResultFileInfo("a.raw", raw + "|0", "a"),                    // exists once the suffix is stripped
            new ResultFileInfo("b.d", dFolder, "b"),                          // a folder-shaped format
            new ResultFileInfo("c.raw", @"Q:\gone\c.raw", "c"),               // moved: found next to the document
            new ResultFileInfo("d.raw", @"Q:\gone\d.raw?centroid", "d"),      // nowhere: Skyline's path, cleaned
            new ResultFileInfo("e.raw", null, "e"),
        };
        var model = new DocumentModel(Path.Combine(_dir, "doc.sky"), Array.Empty<DocProtein>(), files);
        var resolved = SkylineDocumentLoader.WithResolvedPaths(model).ResultFiles.Select(f => f.FilePath).ToArray();
        Assert.Equal(new[] { raw, dFolder, nextToDoc, @"Q:\gone\d.raw", null }, resolved);

        var unsaved = new DocumentModel(null, Array.Empty<DocProtein>(), new[] { new ResultFileInfo("c.raw", @"Q:\gone\c.raw", "c") });
        Assert.Equal(@"Q:\gone\c.raw", SkylineDocumentLoader.WithResolvedPaths(unsaved).ResultFiles[0].FilePath);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(@"C:\a.raw", @"C:\a.raw")]
    [InlineData(@"C:\a.wiff|sample2", @"C:\a.wiff")]
    [InlineData(@"C:\a.raw?centroid_ms1=true", @"C:\a.raw")]
    [InlineData("|x", "")]
    public void SampleSuffixesAreStripped(string? input, string? expected) =>
        Assert.Equal(expected, SkylineDocumentLoader.StripSampleSuffix(input));
}
