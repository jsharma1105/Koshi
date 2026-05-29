using System.Diagnostics;
using System.Text.Json;
using Koshi.Mcp.Cli.Setup;

namespace Koshi.Agents.Internal;

/// <summary>
/// Live-ping a single MCP server registration: spawn the configured
/// <c>koshi-mcp</c> command with the configured args + env + cwd, run a real
/// JSON-RPC handshake over stdio (<c>initialize</c> → <c>notifications/initialized</c>
/// → <c>tools/list</c> → <c>tools/call koshi_health</c>), close stdin, and
/// wait briefly for a clean exit before force-killing.
///
/// <para>
/// This is what makes <c>koshi-agents doctor</c> a real "is everything wired"
/// check instead of a "do files exist" check (#71). The static config / persona /
/// PATH checks remain in <see cref="Commands.DoctorCommand"/>; this class is the
/// behavioural probe layered on top of them.
/// </para>
///
/// <para>
/// All process management lives in <see cref="StdioMcpHandshake"/>; the
/// orchestration in <see cref="RunAsync"/> is pure async logic. Tests inject a
/// fake <see cref="IMcpHandshake"/> so the probe's contract (timeout handling,
/// tools-list parsing, isError surfacing) can be verified without spawning real
/// binaries.
/// </para>
/// </summary>
internal static class McpLivePing
{
    /// <summary>Tool names every supported koshi-mcp build must advertise.</summary>
    public static readonly IReadOnlyList<string> RequiredTools = new[] { "koshi_health", "koshi_version" };

    /// <summary>
    /// Orchestrate one full handshake. Never throws; failures are captured into
    /// <see cref="McpLivePingResult.Errors"/>.
    /// </summary>
    public static async Task<McpLivePingResult> RunAsync(
        McpLivePingOptions options,
        IMcpHandshake handshake,
        CancellationToken cancellationToken = default)
    {
        var result = new McpLivePingResult
        {
            BinaryPath = options.Command,
            Args = options.Args.ToArray(),
        };

        // Pre-flight: capture binary version BEFORE the long-running handshake.
        // Failing to capture version is not fatal; older binaries may not
        // implement --version (very unlikely but worth tolerating).
        try
        {
            result.BinaryVersion = await handshake.CaptureVersionAsync(
                options.Command, options.Env, options.Cwd, options.VersionTimeoutMs, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Errors.Add($"version capture failed: {ex.Message}");
        }

        IMcpProcess? proc = null;
        var spawnStart = Stopwatch.GetTimestamp();
        try
        {
            try
            {
                proc = await handshake.SpawnAsync(options.Command, options.Args, options.Env, options.Cwd, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors.Add($"spawn failed: {ex.Message}");
                return result;
            }

            result.SpawnSucceeded = true;
            result.SpawnLatencyMs = (int)Stopwatch.GetElapsedTime(spawnStart).TotalMilliseconds;
            result.Pid = proc.Pid;

            // Phase 1: initialize
            var initParams = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "koshi-agents-doctor", version = "0.8.1" }
            };
            var initResp = await proc.RpcAsync("initialize", initParams, options.PerCallTimeoutMs, cancellationToken);
            if (initResp is null)
            {
                result.Errors.Add($"initialize: TIMEOUT after {options.PerCallTimeoutMs}ms");
                return result;
            }
            if (TryGetJsonRpcError(initResp.Value, out var initErr))
            {
                result.Errors.Add($"initialize: {initErr}");
                return result;
            }
            result.InitializeSucceeded = true;
            if (initResp.Value.TryGetProperty("result", out var initResult) &&
                initResult.TryGetProperty("protocolVersion", out var protoElem) &&
                protoElem.ValueKind == JsonValueKind.String)
            {
                result.InitializeProtocol = protoElem.GetString();
            }

            await proc.NotifyAsync("notifications/initialized", cancellationToken);

            // Phase 2: tools/list
            var listResp = await proc.RpcAsync("tools/list", null, options.PerCallTimeoutMs, cancellationToken);
            if (listResp is null)
            {
                result.Errors.Add($"tools/list: TIMEOUT after {options.PerCallTimeoutMs}ms");
                return result;
            }
            if (TryGetJsonRpcError(listResp.Value, out var listErr))
            {
                result.Errors.Add($"tools/list: {listErr}");
                return result;
            }
            result.AdvertisedTools = ExtractToolNames(listResp.Value);
            result.MissingRequiredTools = RequiredTools.Where(t => !result.AdvertisedTools.Contains(t)).ToList();

            // Phase 3: tools/call koshi_health — only when required tool is advertised.
            // (Calling a missing tool returns an MCP error; better to skip and tell the
            // user the binary is missing the tool, not that the call failed.)
            if (result.AdvertisedTools.Contains("koshi_health"))
            {
                var callParams = new { name = "koshi_health", arguments = new { } };
                var healthResp = await proc.RpcAsync("tools/call", callParams, options.PerCallTimeoutMs, cancellationToken);
                if (healthResp is null)
                {
                    result.Errors.Add($"koshi_health: TIMEOUT after {options.PerCallTimeoutMs}ms");
                }
                else if (TryGetJsonRpcError(healthResp.Value, out var healthErr))
                {
                    result.Errors.Add($"koshi_health: {healthErr}");
                }
                else
                {
                    result.HealthCallSucceeded = true;
                    if (healthResp.Value.TryGetProperty("result", out var hr))
                    {
                        if (hr.TryGetProperty("isError", out var isErr) && isErr.ValueKind == JsonValueKind.True)
                        {
                            result.HealthCallSucceeded = false;
                            result.Errors.Add("koshi_health: tool reported isError=true");
                        }
                        result.HealthFirstLine = ExtractFirstNonEmptyLine(hr);
                    }
                }
            }
        }
        finally
        {
            if (proc is not null)
            {
                try
                {
                    var exited = await proc.ShutdownAsync(options.ShutdownGraceMs, cancellationToken);
                    result.StoppedCleanly = exited;
                    if (!exited)
                    {
                        result.Errors.Add($"process did not exit within {options.ShutdownGraceMs}ms; force-killed");
                    }
                    // Always drain stderr after the process has exited so we
                    // surface startup errors (bad env, port conflict, native
                    // load failure) in the doctor output.
                    result.StderrTail = proc.StderrTail;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Errors.Add($"shutdown: {ex.Message}");
                }
            }
        }

        return result;
    }

    private static bool TryGetJsonRpcError(JsonElement response, out string message)
    {
        if (response.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            var code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32().ToString()
                : "?";
            var msg = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : "(no message)";
            message = $"JSON-RPC error {code}: {msg}";
            return true;
        }
        message = string.Empty;
        return false;
    }

    private static IReadOnlyList<string> ExtractToolNames(JsonElement response)
    {
        var names = new List<string>();
        if (response.TryGetProperty("result", out var result) &&
            result.TryGetProperty("tools", out var tools) &&
            tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    var n = name.GetString();
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }
        }
        return names;
    }

    private static string? ExtractFirstNonEmptyLine(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array ||
            content.GetArrayLength() == 0)
        {
            return null;
        }

        var first = content[0];
        string? text = null;
        if (first.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
        {
            text = t.GetString();
        }
        if (text is null) return null;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed.Length > 200 ? trimmed[..200] : trimmed;
        }
        return null;
    }
}

/// <summary>Inputs for a single live-ping run.</summary>
internal sealed class McpLivePingOptions
{
    /// <summary>Absolute or PATH-resolvable executable to spawn.</summary>
    public required string Command { get; init; }

    /// <summary>Args to pass to the MCP server. Usually empty.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Env vars to set in the child process. Merged with the parent env.</summary>
    public IReadOnlyDictionary<string, string?> Env { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Working directory for the child process. Null = inherit.</summary>
    public string? Cwd { get; init; }

    /// <summary>Per JSON-RPC call timeout. Default 5 s.</summary>
    public int PerCallTimeoutMs { get; init; } = 5_000;

    /// <summary>Timeout for the <c>--version</c> sidecar invocation. Default 2 s.</summary>
    public int VersionTimeoutMs { get; init; } = 2_000;

    /// <summary>Grace period to wait for clean exit after closing stdin. Default 2 s.</summary>
    public int ShutdownGraceMs { get; init; } = 2_000;
}

/// <summary>Captured evidence from a single live ping. Mutable for builder convenience.</summary>
internal sealed class McpLivePingResult
{
    public required string BinaryPath { get; init; }
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    public string? BinaryVersion { get; set; }
    public bool SpawnSucceeded { get; set; }
    public int SpawnLatencyMs { get; set; }
    public int Pid { get; set; }

    public bool InitializeSucceeded { get; set; }
    public string? InitializeProtocol { get; set; }

    /// <summary>Tools the server advertised in tools/list. Empty when list failed.</summary>
    public IReadOnlyList<string> AdvertisedTools { get; set; } = Array.Empty<string>();

    /// <summary>Subset of <see cref="McpLivePing.RequiredTools"/> the server did NOT advertise.</summary>
    public IReadOnlyList<string> MissingRequiredTools { get; set; } = Array.Empty<string>();

    /// <summary>True only when koshi_health returned a non-error result.</summary>
    public bool HealthCallSucceeded { get; set; }

    public string? HealthFirstLine { get; set; }

    public bool StoppedCleanly { get; set; }

    public List<string> Errors { get; } = new();

    /// <summary>Tail of the child's stderr (when captured), one entry per line.</summary>
    public IReadOnlyList<string> StderrTail { get; set; } = Array.Empty<string>();

    /// <summary>Quick predicate: everything green and no errors.</summary>
    public bool IsAllGreen =>
        SpawnSucceeded && InitializeSucceeded && HealthCallSucceeded &&
        MissingRequiredTools.Count == 0 && Errors.Count == 0;
}

/// <summary>
/// Indirection over "spawn a koshi-mcp child process and capture its version
/// via --version". Real impl is <see cref="StdioMcpHandshake"/>; tests inject
/// a fake.
/// </summary>
internal interface IMcpHandshake
{
    Task<string?> CaptureVersionAsync(
        string command,
        IReadOnlyDictionary<string, string?> env,
        string? cwd,
        int timeoutMs,
        CancellationToken cancellationToken);

    Task<IMcpProcess> SpawnAsync(
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env,
        string? cwd,
        CancellationToken cancellationToken);
}

/// <summary>
/// Abstracted child process the live ping talks to. Implementations own their
/// own concurrency (a single producer reading stdout in a background task).
/// </summary>
internal interface IMcpProcess : IAsyncDisposable
{
    int Pid { get; }

    Task<JsonElement?> RpcAsync(string method, object? @params, int timeoutMs, CancellationToken cancellationToken);

    Task NotifyAsync(string method, CancellationToken cancellationToken);

    /// <summary>Close stdin and wait for graceful exit; force-kill the tree if not.</summary>
    Task<bool> ShutdownAsync(int graceMs, CancellationToken cancellationToken);

    /// <summary>Latest lines from the child's stderr (cap'd; see implementation).</summary>
    IReadOnlyList<string> StderrTail { get; }
}
