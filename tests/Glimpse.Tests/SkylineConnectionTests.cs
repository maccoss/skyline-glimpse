using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Glimpse.Core.Document;
using Glimpse.Skyline;
using SkylineTool;
using Xunit;

namespace Glimpse.Tests;

/// <summary>
/// A stand-in for Skyline's JSON tool service: one request per connection, message-mode pipe, JSON-RPC
/// 2.0 - the same contract the real server keeps, so the client and loader are tested over the wire.
/// </summary>
internal sealed class FakeSkylineServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly Func<string, JsonElement[], object?> _handler;

    public FakeSkylineServer(Func<string, JsonElement[], object?> handler)
    {
        _handler = handler;
        RawName = Guid.NewGuid().ToString("D");
        PipeName = JsonToolConstants.GetJsonPipeName(RawName);
        _thread = new Thread(Serve) { IsBackground = true };
        _thread.Start();
    }

    /// <summary>What Skyline passes a tool as $(SkylineConnection).</summary>
    public string RawName { get; }

    public string PipeName { get; }

    public List<string> Methods { get; } = new();

    public int Connections { get; private set; }

    private void Serve()
    {
        while (!_cts.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
            try
            {
                pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            using var ms = new MemoryStream();
            var buffer = new byte[65536];
            try
            {
                do
                {
                    var n = pipe.Read(buffer, 0, buffer.Length);
                    if (n == 0)
                        break;
                    ms.Write(buffer, 0, n);
                } while (!pipe.IsMessageComplete);
            }
            catch (IOException)
            {
                // The client went away mid-request.
            }
            // A client that gave up (a request Glimpse abandoned after its timeout) can connect late and close
            // without sending anything: there is nothing to answer, and throwing here would end the server
            // thread and crash the test host.
            if (ms.Length == 0)
                continue;
            Connections++;

            string response;
            using (var request = JsonDocument.Parse(ms.ToArray()))
            {
                var method = request.RootElement.GetProperty("method").GetString()!;
                var args = request.RootElement.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(e => e.Clone()).ToArray()
                    : Array.Empty<JsonElement>();
                lock (Methods)
                    Methods.Add(method);
                try
                {
                    var result = _handler(method, args);
                    response = JsonSerializer.Serialize(new { jsonrpc = "2.0", result, id = 1 });
                }
                catch (Exception ex)
                {
                    response = JsonSerializer.Serialize(new { jsonrpc = "2.0", error = new { code = -32000, message = ex.Message }, id = 1 });
                }
            }
            var bytes = Encoding.UTF8.GetBytes(response);
            try
            {
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                pipe.WaitForPipeDrain();
            }
            catch (IOException)
            {
                // The client gave up while the handler ran (a request Glimpse abandoned after its timeout):
                // nobody is left to answer, and throwing would end the server thread and crash the test host.
            }
            // Skyline closes the pipe after each request; so does this.
        }
    }

    public void Dispose() => _cts.Cancel();
}

public sealed class SkylineConnectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glimpse-sky-" + Guid.NewGuid().ToString("N"));

    public SkylineConnectionTests() => Directory.CreateDirectory(_dir);

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
    public void PipeNameIsDerivedFromTheToolArgument()
    {
        var raw = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";
        Assert.Equal("SkylineMcpJson-6f9619ff8b86d011b42d00cf4fc964ff", SkylineConnection.FromArguments(new[] { raw })!.PipeName);
        Assert.Equal("SkylineMcpJson-abc", SkylineConnection.FromArguments(new[] { "SkylineMcpJson-abc" })!.PipeName);
    }

    [Fact]
    public void EachCallUsesANewPipe()
    {
        using var server = new FakeSkylineServer((method, _) => method switch
        {
            "GetVersion" => "Skyline-daily 26.1",
            "GetDocumentPath" => @"C:\docs\test.sky",
            "GetProcessId" => "4242",
            _ => throw new InvalidOperationException("unexpected " + method),
        });
        var skyline = SkylineConnection.FromArguments(new[] { server.RawName })!;
        Assert.Equal("Skyline-daily 26.1", skyline.GetVersion());
        Assert.Equal(@"C:\docs\test.sky", skyline.GetDocumentPath());
        Assert.Equal(4242, skyline.GetProcessId());
        Assert.Equal(3, server.Connections);
        Assert.Throws<JsonRpcException>(() => skyline.Call(c => c.GetSelectionText()));
    }

    [Fact]
    public void TheFakeServerOutlivesAClientThatConnectsAndSendsNothing()
    {
        using var server = new FakeSkylineServer((_, _) => "Skyline-daily 26.1");
        using (var quitter = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut))
            quitter.Connect(5000);
        var skyline = SkylineConnection.FromArguments(new[] { server.RawName })!;
        Assert.Equal("Skyline-daily 26.1", skyline.GetVersion());
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public void TheFakeServerOutlivesAClientThatLeavesBeforeItsAnswer()
    {
        using var handlerStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var server = new FakeSkylineServer((method, _) =>
        {
            if (method == "GetDocumentPath")
            {
                handlerStarted.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
            return "Skyline-daily 26.1";
        });
        using (var leaver = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut))
        {
            leaver.Connect(5000);
            leaver.ReadMode = PipeTransmissionMode.Message;
            var request = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"GetDocumentPath\",\"id\":1}");
            leaver.Write(request, 0, request.Length);
            Assert.True(handlerStarted.Wait(TimeSpan.FromSeconds(10)));
        }
        release.Set();   // the answer now goes to a closed pipe

        var skyline = SkylineConnection.FromArguments(new[] { server.RawName })!;
        Assert.Equal("Skyline-daily 26.1", skyline.GetVersion());
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public void ARequestSkylineNeverAnswersTimesOutAndFreesTheConnection()
    {
        using var answer = new ManualResetEventSlim();
        using var server = new FakeSkylineServer((method, _) =>
        {
            if (method == "GetDocumentPath")
                answer.Wait(TimeSpan.FromSeconds(30));   // Skyline stuck behind a modal dialog
            return method == "GetVersion" ? "Skyline-daily 26.1" : "test.sky";
        });
        var skyline = SkylineConnection.FromArguments(new[] { server.RawName }, TimeSpan.FromSeconds(1))!;

        // A call's own limit, shorter than the connection's two minutes.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = Assert.Throws<TimeoutException>(() => skyline.Call(c => c.GetDocumentPath(), TimeSpan.FromMilliseconds(500)));
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.4, 4.0);
        Assert.Equal("Skyline did not answer within 0.5 s.", error.Message);
        Assert.False(skyline.IsBusy);

        // The connection's limit, for calls that give none (Skyline is still stuck on the first request).
        skyline.RequestTimeout = TimeSpan.FromMilliseconds(300);
        error = Assert.Throws<TimeoutException>(() => skyline.GetVersion());
        Assert.Equal("Skyline did not answer within 0.3 s.", error.Message);
        Assert.False(skyline.IsBusy);

        // Once Skyline is free again, the next request goes through on the same connection. (First let the
        // abandoned request's one-second connection attempt run out, so it cannot take the server's pipe.)
        Thread.Sleep(1500);
        answer.Set();
        skyline.RequestTimeout = TimeSpan.FromSeconds(10);
        Assert.Equal("Skyline-daily 26.1", skyline.GetVersion());
        Assert.Equal(TimeSpan.FromMinutes(2), SkylineConnection.FromArguments(new[] { "x" }, null, _dir)!.RequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(15), SkylineDocumentLoader.ReportTimeout);
    }

    [Fact]
    public void LoaderExportsBothReportsAndResolvesFilePaths()
    {
        var docPath = Path.Combine(_dir, "doc.sky");
        File.WriteAllText(docPath, "<srm_settings />");
        var rawNextToDoc = Path.Combine(_dir, "run1.raw");
        File.WriteAllText(rawNextToDoc, "");
        var requested = new List<(string Name, string[] Select, string Culture, string? DataSource, string? Filter, string Path)>();
        using var server = new FakeSkylineServer((method, args) =>
        {
            switch (method)
            {
                case "GetDocumentPath":
                    return docPath;
                case "ExportReportFromDefinition":
                    var def = args[0];
                    var select = def.GetProperty("select").EnumerateArray().Select(e => e.GetString()!).ToArray();
                    var name = def.GetProperty("name").GetString()!;
                    var filter = def.TryGetProperty("filter", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.Array && f.GetArrayLength() > 0
                        ? $"{f[0].GetProperty("column").GetString()} {f[0].GetProperty("op").GetString()} {f[0].GetProperty("value").GetString()}"
                        : null;
                    // Every report must ask for one row per replicate: Skyline pivots by replicate unless told not to.
                    Assert.True(def.TryGetProperty("pivot_replicate", out var pivot) && pivot.ValueKind == System.Text.Json.JsonValueKind.False,
                        $"{name} must set PivotReplicate = false");
                    var path = args[1].GetString()!;
                    requested.Add((name, select, args[2].GetString()!, def.TryGetProperty("data_source", out var ds) ? ds.GetString() : null, filter, path));
                    // Answer only the columns asked for, by name, the way Skyline does: a misspelled
                    // column comes back empty and the document cannot be built from it.
                    var values = new Dictionary<string, string>
                    {
                        ["ProteinName"] = "P1", ["PeptideSequence"] = "AAK", ["PrecursorCharge"] = "2", ["PrecursorMz"] = "200.1",
                        ["FragmentIon"] = "y1", ["ProductCharge"] = "1", ["ProductMz"] = "147.1",
                        ["PeptideModifiedSequenceMonoisotopicMasses"] = "AAK", ["IsotopeLabelType"] = "light",
                        ["FileName"] = "run1.raw", ["FilePath"] = @"Z:\moved\run1.raw|0", ["ReplicateName"] = "rep1",
                        ["BestRetentionTime"] = "5", ["MinStartTime"] = "4.5", ["MaxEndTime"] = "5.5",
                    };
                    // Like Skyline, the format follows the extension Glimpse asked for.
                    ReportFiles.Write(path, select, new[] { select.Select(c => values.TryGetValue(c, out var v) ? v : "").ToArray() });
                    return new { file_path = path };
                default:
                    throw new InvalidOperationException(method);
            }
        });
        var skyline = SkylineConnection.FromArguments(new[] { server.RawName })!;
        var model = SkylineDocumentLoader.Load(skyline);

        Assert.Equal(docPath, model.DocumentPath);
        Assert.Single(model.Precursors);
        Assert.Empty(model.Precursors[0].Peaks);                 // peaks come per file, on demand
        // Skyline's path does not exist (and has a "|sample" suffix): the file next to the document is used.
        Assert.Equal(rawNextToDoc, model.ResultFiles.Single().FilePath);
        Assert.Equal("rep1", model.ResultFiles.Single().ReplicateName);

        Assert.Equal(1, SkylineDocumentLoader.LoadPeaks(skyline, model, model.ResultFiles.Single()));
        Assert.Equal(5.0, model.Precursors[0].Peaks["run1.raw"].Apex);
        Assert.True(model.HasPeaksFor("run1.raw"));
        Assert.False(model.HasPeaksFor("run2.raw"));

        // The file list first (it is instant, and lets a file be opened while the targets load).
        Assert.Equal(new[] { "Glimpse Files", "Glimpse Transitions", "Glimpse Peaks" }, requested.Select(r => r.Name));
        Assert.All(requested, r => Assert.EndsWith(".parquet", r.Path));
        // Literal names, not the builder's own arrays: comparing those with themselves proves nothing.
        Assert.Equal(new[] { "ReplicateName", "FileName", "FilePath" }, requested[0].Select);
        // No locator columns: they cost Skyline 36 s a report on a 69,000 precursor document.
        Assert.Equal(new[]
        {
            "ProteinName", "ProteinAccession", "ProteinGene",
            "PeptideSequence", "PeptideModifiedSequenceMonoisotopicMasses",
            "ModifiedSequenceMonoisotopicMasses", "PrecursorCharge", "PrecursorMz", "IsotopeLabelType",
            "FragmentIon", "ProductCharge", "ProductMz",
        }, requested[1].Select);
        // No FileName/FilePath: either makes Skyline build the report per transition, pivoted.
        Assert.Equal(new[]
        {
            "ProteinName", "PeptideModifiedSequenceMonoisotopicMasses", "PrecursorCharge", "IsotopeLabelType",
            "ReplicateName",
            "BestRetentionTime", "MinStartTime", "MaxEndTime",
            "DetectionQValue", "LibraryDotProduct", "IsotopeDotProduct",
        }, requested[2].Select);
        // Locators are built the way Skyline builds them.
        Assert.Equal("Precursor:/P1/AAK/light++", model.Precursors[0].Locator);
        Assert.Equal("Molecule:/P1/AAK", model.Peptides[0].Locator);
        // Only the peak report is filtered, to the file's replicate.
        Assert.Equal(new string?[] { null, null, "ReplicateName equals rep1" }, requested.Select(r => r.Filter));
        Assert.All(requested, r => Assert.Equal("invariant", r.Culture));
        Assert.All(requested, r => Assert.Equal("document_grid", r.DataSource));
        // Temporary report files are cleaned up.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Path.GetTempPath(), "SkylineGlimpse"), $"*-{Environment.ProcessId}-*.parquet"));
    }

    [Fact]
    public void LoaderToleratesADocumentWithoutResults()
    {
        using var server = new FakeSkylineServer((method, args) =>
        {
            if (method == "GetDocumentPath")
                return "";
            var def = args[0];
            if (def.GetProperty("name").GetString() == "Glimpse Files")
                throw new InvalidOperationException("no results");
            File.WriteAllText(args[1].GetString()!, "ProteinName,PeptideSequence,PrecursorCharge,PrecursorMz,ProductMz\nP1,AAK,2,200.1,147.1\n");
            return null;
        });
        var log = new List<string>();
        var model = SkylineDocumentLoader.Load(SkylineConnection.FromArguments(new[] { server.RawName })!, log.Add);
        Assert.Null(model.DocumentPath);
        Assert.Single(model.Precursors);
        Assert.Empty(model.ResultFiles);
        Assert.Contains(log, l => l.Contains("Result file report unavailable"));
    }

    [Fact]
    public void SettingsComeFromTheLiveDocumentThenTheSkyFile()
    {
        const string live = "<settings_summary><transition_full_scan product_mass_analyzer=\"centroided\" product_res=\"7\" /></settings_summary>";
        using (var server = new FakeSkylineServer((method, args) =>
        {
            File.WriteAllText(args[0].GetString()!, live);
            return args[0].GetString();
        }))
        {
            var settings = SkylineDocumentLoader.LoadSettings(SkylineConnection.FromArguments(new[] { server.RawName })!, null);
            Assert.Equal(7, settings.ProductTolerance.Resolution);
        }

        var sky = Path.Combine(_dir, "saved.sky");
        File.WriteAllText(sky, "<srm_settings><transition_full_scan product_mass_analyzer=\"centroided\" product_res=\"3\" /></srm_settings>");
        using (var failing = new FakeSkylineServer((_, _) => throw new InvalidOperationException("busy")))
        {
            var connection = SkylineConnection.FromArguments(new[] { failing.RawName })!;
            var log = new List<string>();
            Assert.Equal(3, SkylineDocumentLoader.LoadSettings(connection, sky, log.Add).ProductTolerance.Resolution);
            Assert.Contains(log, l => l.Contains("busy"));
            Assert.Same(DocumentSettings.Default, SkylineDocumentLoader.LoadSettings(connection, Path.Combine(_dir, "missing.sky")));
        }
    }

    [Fact]
    public void FindsTheLibraryNextToTheDocument()
    {
        var doc = Path.Combine(_dir, "doc.sky");
        var settings = DocumentSettings.Default with { LibraryFiles = new[] { "notes.txt", @"Q:\elsewhere\lib.blib" } };
        Assert.Null(SkylineDocumentLoader.FindLibrary(settings, doc));
        Assert.Null(SkylineDocumentLoader.FindLibrary(settings, null));

        File.WriteAllText(Path.Combine(_dir, "doc.blib"), "");
        Assert.Equal(Path.Combine(_dir, "doc.blib"), SkylineDocumentLoader.FindLibrary(settings, doc));   // same-name fallback

        File.WriteAllText(Path.Combine(_dir, "lib.blib"), "");
        Assert.Equal(Path.Combine(_dir, "lib.blib"), SkylineDocumentLoader.FindLibrary(settings, doc));   // hint, by file name

        var rooted = Path.Combine(_dir, "rooted.blib");
        File.WriteAllText(rooted, "");
        Assert.Equal(rooted, SkylineDocumentLoader.FindLibrary(settings with { LibraryFiles = new[] { rooted } }, null));
    }
}

public sealed class AdvertisedConnectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glimpse-mcp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void WithoutArgumentsTheNewestAdvertisedInstanceIsUsed()
    {
        // No directory: standalone.
        Assert.Null(SkylineConnection.FromArguments(Array.Empty<string>(), null, _dir));
        Directory.CreateDirectory(_dir);
        Assert.Null(SkylineConnection.FromArguments(Array.Empty<string>(), null, _dir));

        var old = Path.Combine(_dir, "connection-1.json");
        File.WriteAllText(old, "{\"pipe_name\":\"SkylineMcpJson-old\"}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(_dir, "connection-2.json"), "{\"pipe_name\":\"SkylineMcpJson-new\"}");
        File.WriteAllText(Path.Combine(_dir, "connection-3.json"), "not json");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "connection-3.json"), DateTime.UtcNow.AddHours(1));
        File.WriteAllText(Path.Combine(_dir, "other.json"), "{\"pipe_name\":\"ignored\"}");

        // The unreadable newest file is skipped; the newest readable one wins.
        Assert.Equal("SkylineMcpJson-new", SkylineConnection.FromArguments(Array.Empty<string>(), null, _dir)!.PipeName);
        // A data-file argument is not a connection either.
        Assert.Equal("SkylineMcpJson-new", SkylineConnection.FromArguments(new[] { old }, null, _dir)!.PipeName);
        Assert.Equal("SkylineMcpJson-x", SkylineConnection.FromArguments(new[] { "x" }, null, _dir)!.PipeName);
    }
}
