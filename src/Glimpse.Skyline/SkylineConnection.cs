#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    /// How long a request may wait for Skyline's answer before it fails, unless the call gives its own
    /// limit. A Skyline that never answers (a modal dialog holding it) must not hold every later request
    /// behind it.
    /// </summary>
    public TimeSpan RequestTimeout { get; internal set; } = TimeSpan.FromMinutes(2);

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
    /// <param name="action">The request.</param>
    /// <param name="timeout">How long to wait for the answer; <see cref="RequestTimeout"/> when null.</param>
    /// <exception cref="TimeoutException">Skyline did not answer in time.</exception>
    public T Call<T>(Func<SkylineJsonToolClient, T> action, TimeSpan? timeout = null)
    {
        _gate.Wait();
        try
        {
            return Send(action, timeout ?? RequestTimeout);
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
            result = Send(action, RequestTimeout);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One request on a fresh pipe, given up after <paramref name="timeout"/>. The pipe offers no read
    /// timeout, so the request runs on its own thread: a request Skyline never answers is abandoned (its
    /// pipe closed) and fails here, and this connection's next request can go ahead.
    /// </summary>
    private T Send<T>(Func<SkylineJsonToolClient, T> action, TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        var request = Task.Run(() =>
        {
            using (pipe)
            {
                pipe.Connect((int)Timeout.TotalMilliseconds);
                pipe.ReadMode = PipeTransmissionMode.Message;
                return action(new SkylineJsonToolClient(pipe));
            }
        });
        if (!((IAsyncResult)request).AsyncWaitHandle.WaitOne(timeout))
        {
            // Observe the abandoned request's eventual failure, so it is not reported as unhandled.
            request.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            pipe.Dispose();
            throw new TimeoutException($"Skyline did not answer within {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.");
        }
        return request.GetAwaiter().GetResult();
    }

    /// <summary>Run one call against Skyline on a fresh pipe.</summary>
    public void Call(Action<SkylineJsonToolClient> action, TimeSpan? timeout = null) => Call(c =>
    {
        action(c);
        return 0;
    }, timeout);

    public string GetVersion() => Call(c => c.GetVersion());

    public string GetDocumentPath() => Call(c => c.GetDocumentPath());

    public int? GetProcessId() =>
        int.TryParse(Call(c => c.GetProcessId()), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : null;

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
