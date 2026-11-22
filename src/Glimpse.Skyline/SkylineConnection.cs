#nullable enable

using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using SkylineTool;

namespace Glimpse.Skyline;

/// <summary>
/// The live connection to the Skyline instance that launched Glimpse, over Skyline's JSON-RPC tool
/// service.
/// </summary>
/// <remarks>
/// Three rules, each of which otherwise fails with the same "0x00 is invalid start of value":
/// the JSON pipe name is derived from <c>$(SkylineConnection)</c> with
/// <see cref="JsonToolConstants.GetJsonPipeName"/>; every call opens a NEW pipe, because Skyline closes
/// it after each request; and the pipe must be in message read mode.
/// </remarks>
public sealed class SkylineConnection
{
    private SkylineConnection(string pipeName, TimeSpan timeout)
    {
        PipeName = pipeName;
        Timeout = timeout;
    }

    public string PipeName { get; }

    public TimeSpan Timeout { get; }

    /// <summary>
    /// The connection named by the tool's first argument (<c>$(SkylineConnection)</c>), or, during
    /// development, the most recent instance advertised in <c>~/.skyline-mcp/</c>. Null when neither
    /// exists, which means Glimpse runs standalone.
    /// </summary>
    public static SkylineConnection? FromArguments(string[] args, TimeSpan? timeout = null) =>
        FromArguments(args, timeout, AdvertisedDirectory);

    /// <summary>~/.skyline-mcp, where Skyline advertises running instances.</summary>
    internal static string AdvertisedDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".skyline-mcp");

    internal static SkylineConnection? FromArguments(string[] args, TimeSpan? timeout, string advertisedDirectory)
    {
        var to = timeout ?? TimeSpan.FromSeconds(5);
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) && !File.Exists(args[0]))
        {
            var raw = args[0];
            var name = raw.StartsWith(JsonToolConstants.JSON_PIPE_PREFIX, StringComparison.Ordinal)
                ? raw
                : JsonToolConstants.GetJsonPipeName(raw);
            return new SkylineConnection(name, to);
        }
        var advertised = MostRecentAdvertised(advertisedDirectory);
        return advertised is null ? null : new SkylineConnection(advertised, to);
    }

    /// <summary>Run one call against Skyline on a fresh pipe.</summary>
    // Skyline's tool service answers one request at a time. Glimpse queues its own requests behind this
    // gate so a peak report asked for while the transition report runs waits its turn, instead of timing
    // out against a busy pipe.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True while one of this connection's requests is in progress.</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Run a request, after any of this connection's requests already in progress.</summary>
    public T Call<T>(Func<SkylineJsonToolClient, T> action)
    {
        _gate.Wait();
        try
        {
            return Send(action);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Run a request only if none of this connection's is in progress (for polling, which should skip a
    /// beat rather than wait behind a long report). Returns false, without contacting Skyline, if busy.
    /// </summary>
    public bool TryCall<T>(Func<SkylineJsonToolClient, T> action, out T result)
    {
        if (!_gate.Wait(0))
        {
            result = default!;
            return false;
        }
        try
        {
            result = Send(action);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private T Send<T>(Func<SkylineJsonToolClient, T> action)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect((int)Timeout.TotalMilliseconds);
        pipe.ReadMode = PipeTransmissionMode.Message;
        return action(new SkylineJsonToolClient(pipe));
    }

    /// <summary>Run one call against Skyline on a fresh pipe.</summary>
    public void Call(Action<SkylineJsonToolClient> action) => Call(c =>
    {
        action(c);
        return 0;
    });

    public string GetVersion() => Call(c => c.GetVersion());

    public string GetDocumentPath() => Call(c => c.GetDocumentPath());

    public int? GetProcessId() =>
        int.TryParse(Call(c => c.GetProcessId()), out var pid) ? pid : null;

    /// <summary>Pipe name of the newest instance in ~/.skyline-mcp/connection-*.json, or null.</summary>
    internal static string? MostRecentAdvertised(string dir)
    {
        if (!Directory.Exists(dir))
            return null;
        return Directory.EnumerateFiles(dir, "connection-*.json")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f.FullName));
                    return doc.RootElement.TryGetProperty("pipe_name", out var p) ? p.GetString() : null;
                }
                catch (Exception)
                {
                    return null;
                }
            })
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
    }
}
