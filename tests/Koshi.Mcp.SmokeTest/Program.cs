// Smoke test for the Koshi MCP server.
//
// Spawns the server (via `dotnet <dll>` or directly as an AOT binary),
// performs the MCP handshake, then exercises every one of the 20 tools
// plus three bad-argument paths. Exit code = number of failures (so
// 0 == clean run, anything else => CI fails).
//
// Usage:
//   dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release --no-build
//   dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release --no-build -- --dll path
//   dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release --no-build -- --exe path
//   dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release --no-build -- --exe path --expected-version 0.4.0
//
// Background: the MCP SDK does some reflection at startup and on every
// tools/call. We want the AOT release pipeline to discover any IL2026 /
// IL3050 *runtime* surprise (not just build-time warnings) before we ship,
// so this driver intentionally hits every code path the SDK touches.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

string? dllPath = null;
string? exePath = null;
string? expectedVersion = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dll" when i + 1 < args.Length: dllPath = args[++i]; break;
        case "--exe" when i + 1 < args.Length: exePath = args[++i]; break;
        case "--expected-version" when i + 1 < args.Length: expectedVersion = args[++i]; break;
        default:
            if (!args[i].StartsWith("--") && dllPath is null && exePath is null)
            {
                // Legacy: first positional arg is the DLL path. Preserves
                // the original `dotnet run ... -- <path>` call site.
                dllPath = args[i];
            }
            break;
    }
}

if (dllPath is null && exePath is null)
{
    // Path.Join (not Path.Combine) — Join always concatenates and never
    // silently drops earlier arguments if a later segment looks rooted.
    dllPath = Path.GetFullPath(Path.Join(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "Koshi.Mcp", "bin", "Release", "net10.0", "koshi-mcp.dll"));
}

ProcessStartInfo psi;
if (exePath is not null)
{
    if (!File.Exists(exePath))
    {
        Console.Error.WriteLine($"AOT exe not found: {exePath}");
        return 1;
    }
    psi = new ProcessStartInfo(exePath);
}
else
{
    if (!File.Exists(dllPath!))
    {
        Console.Error.WriteLine($"Server DLL not found: {dllPath}");
        return 1;
    }
    psi = new ProcessStartInfo("dotnet", $"\"{dllPath}\"");
}

psi.RedirectStandardInput = true;
psi.RedirectStandardOutput = true;
psi.RedirectStandardError = true;
psi.UseShellExecute = false;
psi.StandardInputEncoding = Encoding.UTF8;
psi.StandardOutputEncoding = Encoding.UTF8;

// ─── Pre-seed a v0.3.0-shaped memory file ────────────────────────────────
// Validates that the v0.4.0 AOT source-generated JsonSerializerContext can
// still read memory files written by v0.3.0's manual JsonSerializerOptions
// pipeline. Includes all 14 MemoryRecord fields with PascalCase property
// names and string-encoded enums (the v0.3.0 wire shape). If the source
// generator silently changes the format, koshi_recall below will not find
// the seeded subject and the smoke fails — guarding against silent
// on-disk-format drift.
//
// The file is also writable: subsequent koshi_remember calls in Phase 4
// will overwrite it with the v0.4.0 format. That's the intended round-trip.
const string V030MemoryFixtureJson = """
{
  "SchemaVersion": 1,
  "SavedAt": "2026-04-01T10:00:00+00:00",
  "Memories": [
    {
      "Id": "mem-v030-000001",
      "Type": "Decision",
      "Content": "OLS offer IDs use the format OLS-{SegmentCode}-{000001}. Reserved by Koshi v0.3.0 smoke test as a backwards-compat fixture.",
      "Subject": "ols-offer-id-format",
      "Scope": {
        "UserId": "*",
        "WorkspaceId": "default",
        "ThreadId": null
      },
      "Source": "v030-fixture",
      "Confidence": 0.95,
      "CreatedAt": "2026-04-01T10:00:00+00:00",
      "LastAccessedAt": "2026-04-01T10:00:00+00:00",
      "AccessCount": 0,
      "Tier": "Hot"
    },
    {
      "Id": "mem-v030-000002",
      "Type": "Pattern",
      "Content": "DOME API controllers delegate every data access call to a stored procedure via ISQLHelperRepository.",
      "Subject": "dome-data-access",
      "Scope": { "UserId": "*", "WorkspaceId": "default" },
      "Source": "v030-fixture",
      "Confidence": 0.9,
      "CreatedAt": "2026-04-01T10:00:00+00:00",
      "LastAccessedAt": "2026-04-01T10:00:00+00:00",
      "AccessCount": 3,
      "Tier": "Warm"
    }
  ]
}
""";

string? preSeededMemoryFile = null;
string? preSeededMemoryDir = null;
try
{
    preSeededMemoryDir = Path.Join(Path.GetTempPath(), $"koshi-smoke-{Guid.NewGuid():N}");
    Directory.CreateDirectory(preSeededMemoryDir);
    preSeededMemoryFile = Path.Join(preSeededMemoryDir, "v030-memories.json");
    File.WriteAllText(preSeededMemoryFile, V030MemoryFixtureJson);
    psi.Environment["KOSHI_MEMORY_FILE"] = preSeededMemoryFile;
    Console.Error.WriteLine($"[setup] Pre-seeded v0.3.0 memory fixture at: {preSeededMemoryFile}");
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
{
    Console.Error.WriteLine($"[setup] WARN: failed to seed memory fixture: {ex.Message}");
    preSeededMemoryFile = null;
}

Console.Error.WriteLine($"=== Launching: {psi.FileName} {psi.Arguments} ===");
var proc = Process.Start(psi)!;

_ = Task.Run(async () =>
{
    string? line;
    while ((line = await proc.StandardError.ReadLineAsync()) is not null)
    {
        Console.Error.WriteLine($"[stderr] {line}");
    }
});

var responses = new Dictionary<int, JsonElement>();
using var responseSignal = new SemaphoreSlim(0);

_ = Task.Run(async () =>
{
    string? line;
    while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            if (root.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.Number)
            {
                responses[idElem.GetInt32()] = root;
                responseSignal.Release();
            }
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"[non-json stdout] {line}");
        }
    }
});

int nextId = 1;
var failures = new List<string>();

async Task<JsonElement?> RpcAsync(string method, object? @params = null, int timeoutMs = 8000)
{
    int id = nextId++;
    var payload = @params is null
        ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
        : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{JsonSerializer.Serialize(@params)}}}""";

    await proc.StandardInput.WriteLineAsync(payload);
    await proc.StandardInput.FlushAsync();

    var deadline = Environment.TickCount + timeoutMs;
    while (!responses.TryGetValue(id, out var cached))
    {
        int remaining = deadline - Environment.TickCount;
        if (remaining <= 0)
        {
            return null;
        }
        await responseSignal.WaitAsync(Math.Max(1, remaining));
        if (responses.TryGetValue(id, out cached))
        {
            return cached;
        }
    }
    return responses[id];
}

async Task SendNotificationAsync(string json)
{
    await proc.StandardInput.WriteLineAsync(json);
    await proc.StandardInput.FlushAsync();
}

static string? ExtractFirstText(JsonElement response)
{
    if (response.TryGetProperty("result", out var result) &&
        result.TryGetProperty("content", out var content) &&
        content.ValueKind == JsonValueKind.Array &&
        content.GetArrayLength() > 0 &&
        content[0].TryGetProperty("text", out var text))
    {
        return text.GetString();
    }
    return null;
}

static bool IsErrorResponse(JsonElement response) =>
    response.TryGetProperty("error", out _) ||
    (response.TryGetProperty("result", out var r) &&
     r.TryGetProperty("isError", out var isErr) &&
     isErr.ValueKind == JsonValueKind.True);

async Task ExpectSuccessAsync(string label, string toolName, object args)
{
    var resp = await RpcAsync("tools/call", new { name = toolName, arguments = args });
    if (resp is null) { failures.Add($"{label}: TIMEOUT"); return; }
    if (IsErrorResponse(resp.Value)) { failures.Add($"{label}: server returned error: {resp.Value.GetRawText()[..Math.Min(200, resp.Value.GetRawText().Length)]}"); return; }
    var text = ExtractFirstText(resp.Value);
    if (text is null) { failures.Add($"{label}: missing result.content[0].text"); return; }
    if (text.StartsWith('❌')) { failures.Add($"{label}: tool returned error text: {text[..Math.Min(160, text.Length)]}"); return; }
    Console.Error.WriteLine($"[ok] {label}");
}

async Task ExpectToolErrorAsync(string label, string toolName, object args)
{
    var resp = await RpcAsync("tools/call", new { name = toolName, arguments = args });
    if (resp is null) { failures.Add($"{label}: TIMEOUT (expected an error)"); return; }
    if (IsErrorResponse(resp.Value)) { Console.Error.WriteLine($"[ok-error] {label} (JSON-RPC error)"); return; }
    var text = ExtractFirstText(resp.Value);
    if (text is not null && text.StartsWith('❌')) { Console.Error.WriteLine($"[ok-error] {label} (\"❌\" text)"); return; }
    failures.Add($"{label}: expected an error but tool succeeded with: {(text ?? "(no text)")[..Math.Min(160, (text ?? "").Length)]}");
}

// ─── Phase 1: handshake ──────────────────────────────────────────────────
var initResp = await RpcAsync("initialize", new
{
    protocolVersion = "2024-11-05",
    capabilities = new { },
    clientInfo = new { name = "koshi-smoke", version = "0.4.0" }
});
if (initResp is null) { Console.Error.WriteLine("FAIL: no response to initialize"); KillAndExit(2); }
await SendNotificationAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

// ─── Phase 2: tools/list — must enumerate all 20 tools ───────────────────
var listResp = await RpcAsync("tools/list");
if (listResp is null) { failures.Add("tools/list: TIMEOUT"); }
else
{
    var toolNames = new HashSet<string>();
    if (listResp.Value.TryGetProperty("result", out var lr) &&
        lr.TryGetProperty("tools", out var tools) &&
        tools.ValueKind == JsonValueKind.Array)
    {
        var named = tools.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("name", out _))
            .Select(t => t.GetProperty("name").GetString() ?? string.Empty);
        foreach (var name in named)
        {
            toolNames.Add(name);
        }
    }
    string[] expected = [
        "koshi_index", "koshi_index_directory", "koshi_search", "koshi_list_indexed", "koshi_clear_index",
        "koshi_remember", "koshi_recall", "koshi_memory_stats", "koshi_forget", "koshi_clear_memories",
        "koshi_compile_context", "koshi_token_count", "koshi_budget_plan",
        "koshi_register_team", "koshi_score_turn", "koshi_team_dashboard", "koshi_analyze_feedback", "koshi_list_teams",
        "koshi_version", "koshi_health",
    ];
    foreach (var name in expected.Where(n => !toolNames.Contains(n)))
    {
        failures.Add($"tools/list: missing tool '{name}'");
    }
    Console.Error.WriteLine($"[info] tools/list reported {toolNames.Count} tools");
}

// ─── Phase 3: version-stamp assertion ───────────────────────────────────
var versionResp = await RpcAsync("tools/call", new { name = "koshi_version", arguments = new { } });
if (versionResp is null) failures.Add("koshi_version: TIMEOUT");
else
{
    var text = ExtractFirstText(versionResp.Value);
    if (text is null) failures.Add("koshi_version: missing text");
    else
    {
        Console.Error.WriteLine($"[info] koshi_version: {text.Split('\n')[0]}");
        if (expectedVersion is not null && !text.Contains(expectedVersion, StringComparison.Ordinal))
        {
            failures.Add($"koshi_version: expected '{expectedVersion}' in response but got: {text.Split('\n')[0]}");
        }
    }
}

// ─── Phase 3.5: v0.3.0 memory file compatibility ────────────────────────
// Asserts that the file written at startup by a v0.3.0-shaped pipeline
// deserializes cleanly under the v0.4.0 source-generated context. If
// either of the two seeded subjects fails to come back, the on-disk
// memory format has drifted and any v0.3.0 user upgrading to v0.4.0
// would silently lose their stored memories.
if (preSeededMemoryFile is not null)
{
    var compatResp = await RpcAsync("tools/call", new
    {
        name = "koshi_recall",
        arguments = new { query = "OLS offer id format", type = "All", topK = 5 }
    });
    if (compatResp is null)
    {
        failures.Add("memory-compat: recall TIMEOUT");
    }
    else
    {
        var text = ExtractFirstText(compatResp.Value);
        if (text is null)
        {
            failures.Add("memory-compat: recall returned no text");
        }
        else if (!text.Contains("OLS-{SegmentCode}", StringComparison.Ordinal)
              || !text.Contains("ols-offer-id-format", StringComparison.Ordinal))
        {
            failures.Add($"memory-compat: pre-seeded v0.3.0 Decision memory NOT loaded. Recall response: {text[..Math.Min(240, text.Length)]}");
        }
        else
        {
            Console.Error.WriteLine("[ok] memory-compat: v0.3.0 Decision memory loaded under v0.4.0 source-gen");
        }
    }

    // Also exercise a second memory with a different Tier (Warm) — covers the
    // enum source-gen converter on a less-common value.
    var compatResp2 = await RpcAsync("tools/call", new
    {
        name = "koshi_recall",
        arguments = new { query = "DOME stored procedure ISQLHelperRepository", type = "Pattern", topK = 5 }
    });
    if (compatResp2 is null)
    {
        failures.Add("memory-compat: second recall TIMEOUT");
    }
    else
    {
        var text2 = ExtractFirstText(compatResp2.Value);
        if (text2 is null || !text2.Contains("dome-data-access", StringComparison.Ordinal))
        {
            failures.Add($"memory-compat: pre-seeded v0.3.0 Pattern memory NOT loaded (Warm tier). Recall response: {(text2 ?? "(no text)")[..Math.Min(240, (text2 ?? "").Length)]}");
        }
        else
        {
            Console.Error.WriteLine("[ok] memory-compat: v0.3.0 Pattern/Warm memory loaded under v0.4.0 source-gen");
        }
    }
}

// ─── Phase 4: each of the 20 tools ──────────────────────────────────────
// Retrieval (5)
await ExpectSuccessAsync("koshi_index", "koshi_index", new
{
    documents = """[{"content":"alpha bravo charlie","source":"a.md","type":"documentation"},{"content":"delta echo foxtrot","source":"b.md","type":"documentation"}]"""
});
var indexPath = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
await ExpectSuccessAsync("koshi_index_directory", "koshi_index_directory", new { path = indexPath, pattern = "*.cs" });
await ExpectSuccessAsync("koshi_search", "koshi_search", new { query = "alpha bravo", topK = 2 });
await ExpectSuccessAsync("koshi_list_indexed", "koshi_list_indexed", new { });
await ExpectSuccessAsync("koshi_clear_index", "koshi_clear_index", new { });

// Memory (5)
await ExpectSuccessAsync("koshi_remember", "koshi_remember", new
{
    content = "Smoke test stores this fact",
    subject = "smoke",
    type = "Fact",
});
await ExpectSuccessAsync("koshi_recall", "koshi_recall", new { query = "smoke", type = "All", topK = 3 });
await ExpectSuccessAsync("koshi_memory_stats", "koshi_memory_stats", new { });
await ExpectSuccessAsync("koshi_forget", "koshi_forget", new { subject = "smoke" });
await ExpectSuccessAsync("koshi_clear_memories", "koshi_clear_memories", new { confirm = true });

// Context (3) — koshi_token_count is the tokenizer canary for AOT trimming.
await ExpectSuccessAsync("koshi_token_count", "koshi_token_count", new { text = "hello world how are you today" });
await ExpectSuccessAsync("koshi_compile_context", "koshi_compile_context", new
{
    systemPrompt = "You are a helpful assistant.",
    userQuery = "Summarise the project."
});
await ExpectSuccessAsync("koshi_budget_plan", "koshi_budget_plan", new { totalBudget = 8000, systemPrompt = "be terse" });

// Team (5)
await ExpectSuccessAsync("koshi_register_team", "koshi_register_team", new { teamId = "smoke-team", name = "Smoke Test Team" });
await ExpectSuccessAsync("koshi_score_turn", "koshi_score_turn", new
{
    teamId = "smoke-team",
    retrievedChunks = 5,
    memoriesRecalled = 2,
    budgetUtilization = 0.6f,
    cacheRatio = 0.3f,
    latencyMs = 2500,
});
await ExpectSuccessAsync("koshi_team_dashboard", "koshi_team_dashboard", new { teamId = "smoke-team" });
await ExpectSuccessAsync("koshi_analyze_feedback", "koshi_analyze_feedback", new { teamId = "smoke-team" });
await ExpectSuccessAsync("koshi_list_teams", "koshi_list_teams", new { });

// Diagnostics (2) — version already covered above; check health
await ExpectSuccessAsync("koshi_health", "koshi_health", new { });

// ─── Phase 5: bad-argument paths ────────────────────────────────────────
await ExpectToolErrorAsync("bad: search empty query", "koshi_search", new { query = "", topK = 5 });
await ExpectToolErrorAsync("bad: forget empty subject", "koshi_forget", new { subject = "" });
await ExpectToolErrorAsync("bad: index invalid JSON", "koshi_index", new { documents = "this is not json" });

// ─── Shutdown ───────────────────────────────────────────────────────────
proc.StandardInput.Close();
proc.WaitForExit(5000);
if (!proc.HasExited) proc.Kill();

// Best-effort cleanup of the pre-seeded memory fixture directory.
if (preSeededMemoryDir is not null)
{
    try { Directory.Delete(preSeededMemoryDir, recursive: true); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
    {
        Console.Error.WriteLine($"[cleanup] WARN: could not remove {preSeededMemoryDir}: {ex.Message}");
    }
}

Console.Error.WriteLine();
Console.Error.WriteLine($"=== Smoke summary: {(failures.Count == 0 ? "PASS" : "FAIL")} ({failures.Count} failure(s), {responses.Count} responses received) ===");
foreach (var f in failures) Console.Error.WriteLine($"  ✗ {f}");

return failures.Count;

void KillAndExit(int code)
{
    if (!proc.HasExited) proc.Kill();
    Environment.Exit(code);
}
