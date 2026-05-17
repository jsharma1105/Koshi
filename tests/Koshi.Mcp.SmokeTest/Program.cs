// Smoke test for the Koshi MCP server.
//
// Spawns the server (via `dotnet <dll>` or directly as an AOT binary),
// performs the MCP handshake, then exercises every one of the 20 tools
// plus three bad-argument paths. Exit code = number of failures (so
// 0 == clean run, anything else => CI fails).
//
// Usage:
//   dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release --no-build
//   dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release --no-build -- --dll <path>
//   dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release --no-build -- --exe <path>
//   dotnet run --project tests/Koshi.Mcp.SmokeTest -c Release --no-build -- --exe <path> --expected-version 0.4.0
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
    dllPath = Path.GetFullPath(Path.Combine(
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
var responseSignal = new SemaphoreSlim(0);

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
    while (!responses.ContainsKey(id))
    {
        int remaining = deadline - Environment.TickCount;
        if (remaining <= 0)
        {
            return null;
        }
        await responseSignal.WaitAsync(Math.Max(1, remaining));
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
        foreach (var t in tools.EnumerateArray())
        {
            if (t.TryGetProperty("name", out var n)) toolNames.Add(n.GetString() ?? "");
        }
    }
    string[] expected = [
        "koshi_index", "koshi_index_directory", "koshi_search", "koshi_list_indexed", "koshi_clear_index",
        "koshi_remember", "koshi_recall", "koshi_memory_stats", "koshi_forget", "koshi_clear_memories",
        "koshi_compile_context", "koshi_token_count", "koshi_budget_plan",
        "koshi_register_team", "koshi_score_turn", "koshi_team_dashboard", "koshi_analyze_feedback", "koshi_list_teams",
        "koshi_version", "koshi_health",
    ];
    foreach (var name in expected)
    {
        if (!toolNames.Contains(name)) failures.Add($"tools/list: missing tool '{name}'");
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

// ─── Phase 4: each of the 20 tools ──────────────────────────────────────
// Retrieval (5)
await ExpectSuccessAsync("koshi_index", "koshi_index", new
{
    documents = """[{"content":"alpha bravo charlie","source":"a.md","type":"documentation"},{"content":"delta echo foxtrot","source":"b.md","type":"documentation"}]"""
});
var indexPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
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

Console.Error.WriteLine();
Console.Error.WriteLine($"=== Smoke summary: {(failures.Count == 0 ? "PASS" : "FAIL")} ({failures.Count} failure(s), {responses.Count} responses received) ===");
foreach (var f in failures) Console.Error.WriteLine($"  ✗ {f}");

return failures.Count;

void KillAndExit(int code)
{
    if (!proc.HasExited) proc.Kill();
    Environment.Exit(code);
}
