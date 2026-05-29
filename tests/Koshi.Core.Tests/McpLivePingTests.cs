using System.Text.Json;
using Koshi.Agents.Internal;
using Koshi.Mcp.Cli.Setup;

namespace Koshi.Core.Tests;

/// <summary>
/// Behaviour tests for <see cref="McpLivePing"/>. We do NOT spawn the real
/// koshi-mcp binary here — the smoke test (tests/Koshi.Mcp.SmokeTest) already
/// proves end-to-end. These tests pin the contract: timeout handling, JSON-RPC
/// error surfacing, missing-required-tool detection, isError surfacing, and
/// stderr capture.
/// </summary>
public class McpLivePingTests
{
    private static readonly McpLivePingOptions FastOptions = new()
    {
        Command = "fake-koshi-mcp",
        // Tight timeouts so the suite stays under a few seconds even when
        // tests exercise timeout paths.
        PerCallTimeoutMs = 300,
        VersionTimeoutMs = 100,
        ShutdownGraceMs = 200,
    };

    [Fact]
    public async Task Happy_path_populates_all_evidence_fields()
    {
        var fake = new FakeHandshake
        {
            Version = "1.2.3",
            InitializeProtocol = "2024-11-05",
            ToolNames = new[] { "koshi_health", "koshi_version", "koshi_search" },
            HealthText = "═══ Koshi Health (v1.2.3) ═══\n\n  Retrieval:\n    Indexed: yes",
        };

        var r = await McpLivePing.RunAsync(FastOptions, fake);

        Assert.True(r.SpawnSucceeded);
        Assert.Equal("1.2.3", r.BinaryVersion);
        Assert.True(r.InitializeSucceeded);
        Assert.Equal("2024-11-05", r.InitializeProtocol);
        Assert.Equal(3, r.AdvertisedTools.Count);
        Assert.Empty(r.MissingRequiredTools);
        Assert.True(r.HealthCallSucceeded);
        Assert.Equal("═══ Koshi Health (v1.2.3) ═══", r.HealthFirstLine);
        Assert.True(r.StoppedCleanly);
        Assert.True(r.IsAllGreen);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public async Task Spawn_failure_is_reported_not_thrown()
    {
        var fake = new FakeHandshake { ThrowOnSpawn = "command not found" };

        var r = await McpLivePing.RunAsync(FastOptions, fake);

        Assert.False(r.SpawnSucceeded);
        Assert.False(r.IsAllGreen);
        Assert.Contains(r.Errors, e => e.Contains("spawn failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Errors, e => e.Contains("command not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Initialize_timeout_is_captured_with_ms()
    {
        var fake = new FakeHandshake { InitializeReturnsNull = true };

        var r = await McpLivePing.RunAsync(FastOptions, fake);

        Assert.True(r.SpawnSucceeded);
        Assert.False(r.InitializeSucceeded);
        Assert.Contains(r.Errors, e => e.Contains("initialize", StringComparison.OrdinalIgnoreCase) &&
                                       e.Contains("TIMEOUT", StringComparison.Ordinal));
        Assert.False(r.IsAllGreen);
    }

    [Fact]
    public async Task Initialize_jsonrpc_error_is_captured()
    {
        var fake = new FakeHandshake { InitializeError = (-32602, "invalid protocol version") };

        var r = await McpLivePing.RunAsync(FastOptions, fake);

        Assert.True(r.SpawnSucceeded);
        Assert.False(r.InitializeSucceeded);
        Assert.Contains(r.Errors, e => e.Contains("initialize", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Errors, e => e.Contains("-32602", StringComparison.Ordinal));
        Assert.Contains(r.Errors, e => e.Contains("invalid protocol version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_required_tools_are_listed_explicitly()
    {
        var fake = new FakeHandshake
        {
            InitializeProtocol = "2024-11-05",
            // Only one of the two required tools is advertised.
            ToolNames = new[] { "koshi_version", "koshi_search" },
        };

        var r = await McpLivePing.RunAsync(FastOptions, fake);

        Assert.True(r.InitializeSucceeded);
        Assert.Equal(new[] { "koshi_health" }, r.MissingRequiredTools);
        Assert.False(r.IsAllGreen);
        // koshi_health was NOT advertised, so the tool call should be SKIPPED,
        // not failed — the doctor reports "missing required" instead of "call failed".
        Assert.False(r.HealthCallSucceeded);
        Assert.DoesNotContain(r.Errors, e => e.Contains("koshi_health: TIMEOUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_isError_true_flips_HealthCallSucceeded_false()
    {
        var fake = new FakeHandshake
        {
            InitializeProtocol = "2024-11-05",
            ToolNames = new[] { "koshi_health", "koshi_version" },
            HealthIsError = true,
            HealthText = "snapshot corrupt",
        };

        var r = await McpLivePing.RunAsync(FastOptions, fake);

        Assert.False(r.HealthCallSucceeded);
        Assert.Contains(r.Errors, e => e.Contains("isError=true", StringComparison.Ordinal));
        Assert.False(r.IsAllGreen);
    }

    [Fact]
    public async Task Stderr_tail_is_surfaced_on_failure()
    {
        var fake = new FakeHandshake
        {
            InitializeReturnsNull = true,
            StderrLines = new[] { "[koshi] FATAL: bind failed", "exit code 2" },
        };

        var r = await McpLivePing.RunAsync(FastOptions, fake);

        Assert.Equal(2, r.StderrTail.Count);
        Assert.Contains("FATAL", r.StderrTail[0]);
    }

    [Fact]
    public async Task Version_capture_failure_does_not_fail_the_ping()
    {
        var fake = new FakeHandshake
        {
            ThrowOnVersion = "binary stat failed",
            InitializeProtocol = "2024-11-05",
            ToolNames = new[] { "koshi_health", "koshi_version" },
            HealthText = "ok",
        };

        var r = await McpLivePing.RunAsync(FastOptions, fake);

        Assert.Null(r.BinaryVersion);
        Assert.Contains(r.Errors, e => e.Contains("version capture failed", StringComparison.OrdinalIgnoreCase));
        // The rest of the probe still ran and succeeded.
        Assert.True(r.InitializeSucceeded);
        Assert.True(r.HealthCallSucceeded);
        // Not "all green" because we recorded an error in Errors.
        Assert.False(r.IsAllGreen);
    }

    // ─── Test double ────────────────────────────────────────────────────────

    private sealed class FakeHandshake : IMcpHandshake
    {
        public string? Version { get; set; }
        public string? ThrowOnSpawn { get; set; }
        public string? ThrowOnVersion { get; set; }
        public bool InitializeReturnsNull { get; set; }
        public (int code, string message)? InitializeError { get; set; }
        public string? InitializeProtocol { get; set; }
        public IReadOnlyList<string>? ToolNames { get; set; }
        public bool HealthIsError { get; set; }
        public string? HealthText { get; set; }
        public string[] StderrLines { get; set; } = Array.Empty<string>();

        public Task<string?> CaptureVersionAsync(string command, IReadOnlyDictionary<string, string?> env,
            string? cwd, int timeoutMs, CancellationToken cancellationToken)
        {
            if (ThrowOnVersion is not null) throw new InvalidOperationException(ThrowOnVersion);
            return Task.FromResult(Version);
        }

        public Task<IMcpProcess> SpawnAsync(string command, IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string?> env, string? cwd, CancellationToken cancellationToken)
        {
            if (ThrowOnSpawn is not null) throw new InvalidOperationException(ThrowOnSpawn);
            return Task.FromResult<IMcpProcess>(new FakeProcess(this));
        }
    }

    private sealed class FakeProcess : IMcpProcess
    {
        private readonly FakeHandshake _h;
        public FakeProcess(FakeHandshake h) { _h = h; }

        public int Pid => 4242;
        public IReadOnlyList<string> StderrTail => _h.StderrLines;

        public Task<JsonElement?> RpcAsync(string method, object? @params, int timeoutMs, CancellationToken cancellationToken)
        {
            JsonElement? Build(string json)
            {
                var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }

            switch (method)
            {
                case "initialize":
                    if (_h.InitializeReturnsNull) return Task.FromResult<JsonElement?>(null);
                    if (_h.InitializeError is { } err)
                    {
                        var errJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":" + err.code +
                                      ",\"message\":" + JsonSerializer.Serialize(err.message) + "}}";
                        return Task.FromResult(Build(errJson));
                    }
                    var okJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":" +
                                 JsonSerializer.Serialize(_h.InitializeProtocol ?? "2024-11-05") +
                                 ",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"0.0.0\"}}}";
                    return Task.FromResult(Build(okJson));

                case "tools/list":
                    var names = _h.ToolNames ?? Array.Empty<string>();
                    var toolsArr = string.Join(",", names.Select(n =>
                        "{\"name\":" + JsonSerializer.Serialize(n) + ",\"description\":\"x\",\"inputSchema\":{}}"));
                    return Task.FromResult(Build("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[" + toolsArr + "]}}"));

                case "tools/call":
                    var text = _h.HealthText ?? "";
                    var isErr = _h.HealthIsError ? "true" : "false";
                    var callJson = "{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"content\":[{\"type\":\"text\",\"text\":" +
                                   JsonSerializer.Serialize(text) + "}],\"isError\":" + isErr + "}}";
                    return Task.FromResult(Build(callJson));

                default:
                    return Task.FromResult<JsonElement?>(null);
            }
        }

        public Task NotifyAsync(string method, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> ShutdownAsync(int graceMs, CancellationToken cancellationToken) => Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
