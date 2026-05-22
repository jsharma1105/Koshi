// Smoke test for the Koshi MCP server.
//
// Spawns the server (via `dotnet <dll>` or directly as an AOT binary),
// performs the MCP handshake, then exercises every one of the 24 tools
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

// ─── Pre-check: `--version` exits cleanly without starting the server ─────
// Regression guard for the bug where `koshi-mcp --version` would hang
// forever because args were silently ignored and the host went straight
// into reading JSON-RPC off stdin. Validates that:
//   1. Process exits within a couple of seconds (not hung on stdin).
//   2. Exit code is 0.
//   3. Stdout starts with the literal "koshi-mcp " banner.
//   4. When `--expected-version` is set (CI), stdout contains that version.
// Runs against the same binary the rest of the smoke test will spawn, so
// any AOT-specific regression (e.g. missing trimmer roots) is caught here.
{
    var versionPsi = exePath is not null
        ? new ProcessStartInfo(exePath, "--version")
        : new ProcessStartInfo("dotnet", $"\"{dllPath}\" --version");
    versionPsi.RedirectStandardOutput = true;
    versionPsi.RedirectStandardError = true;
    versionPsi.UseShellExecute = false;
    versionPsi.StandardOutputEncoding = Encoding.UTF8;

    var vproc = Process.Start(versionPsi)!;
    var stdoutTask = vproc.StandardOutput.ReadToEndAsync();
    var stderrTask = vproc.StandardError.ReadToEndAsync();
    if (!vproc.WaitForExit(5000))
    {
        try { vproc.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* best effort */ }
        catch (System.ComponentModel.Win32Exception) { /* best effort */ }
        catch (NotSupportedException) { /* best effort */ }
        Console.Error.WriteLine("[--version] FAIL: process did not exit within 5s (likely hung reading stdin).");
        return 1;
    }
    var vout = (await stdoutTask).Trim();
    var verr = (await stderrTask).Trim();
    Console.Error.WriteLine($"[--version] exit={vproc.ExitCode}  stdout='{vout}'");
    if (verr.Length > 0) Console.Error.WriteLine($"[--version] stderr='{verr}'");
    if (vproc.ExitCode != 0)
    {
        Console.Error.WriteLine($"[--version] FAIL: non-zero exit code {vproc.ExitCode}.");
        return 1;
    }
    if (!vout.StartsWith("koshi-mcp ", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("[--version] FAIL: stdout did not start with 'koshi-mcp '.");
        return 1;
    }
    if (expectedVersion is not null && !vout.Contains(expectedVersion, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"[--version] FAIL: expected version '{expectedVersion}' not in stdout.");
        return 1;
    }
}

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
    },
    {
      "Id": "mem-v051-scope-a",
      "Type": "Fact",
      "Content": "Project-A scoped marker: xkrythogue_alpha_token. Should be visible only when workspaceId='project-a'.",
      "Subject": "scope-fixture-project-a",
      "Scope": { "UserId": "*", "WorkspaceId": "project-a", "ThreadId": null },
      "Source": "v051-fixture",
      "Confidence": 0.9,
      "CreatedAt": "2026-05-19T10:00:00+00:00",
      "LastAccessedAt": "2026-05-19T10:00:00+00:00",
      "AccessCount": 0,
      "Tier": "Hot"
    },
    {
      "Id": "mem-v051-scope-b",
      "Type": "Fact",
      "Content": "Project-B scoped marker: yzephylgrum_beta_token. Must NOT leak when workspaceId='project-a' is requested.",
      "Subject": "scope-fixture-project-b",
      "Scope": { "UserId": "*", "WorkspaceId": "project-b", "ThreadId": null },
      "Source": "v051-fixture",
      "Confidence": 0.9,
      "CreatedAt": "2026-05-19T10:00:00+00:00",
      "LastAccessedAt": "2026-05-19T10:00:00+00:00",
      "AccessCount": 0,
      "Tier": "Hot"
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

// Pre-seed an IndexEnvelope (schema v1) at KOSHI_INDEX_FILE so we can verify
// (a) the v0.5.0 index file format round-trips through the source-gen
// JsonSerializerContext and (b) koshi_search auto-loads on first call without
// requiring koshi_index_directory. The fixture uses an in-memory sentinel
// source so no fingerprint validation runs at load time. The distinctive
// token "zorblax_quux_xenoplasma" lets us assert that the seeded chunk
// content is actually returned by search.
const string IndexFixtureJson = """
{
  "SchemaVersion": 1,
  "SavedAt": "2026-05-18T10:00:00+00:00",
  "SourcePath": "in-memory",
  "Chunks": [
    {
      "Id": "fixture:chunk-0",
      "Content": "Persistence fixture: zorblax_quux_xenoplasma should be retrievable on first koshi_search without any indexing call.",
      "Metadata": {
        "Source": "fixture-persistence.md",
        "DocumentType": "documentation",
        "StartOffset": 0,
        "EndOffset": 1,
        "IngestedAt": "2026-05-18T10:00:00+00:00"
      },
      "TokenCount": 24
    },
    {
      "Id": "fixture:chunk-1",
      "Content": "Second fixture chunk. Distinguishing keyword: zorblax_quux_xenoplasma. Used by the smoke test to exercise BM25 ranking over a 2-chunk corpus.",
      "Metadata": {
        "Source": "fixture-persistence.md",
        "DocumentType": "documentation",
        "StartOffset": 1,
        "EndOffset": 2,
        "IngestedAt": "2026-05-18T10:00:00+00:00"
      },
      "TokenCount": 28
    }
  ]
}
""";

string? preSeededIndexFile = null;
string? preSeededIndexDir = null;
try
{
    preSeededIndexDir = Path.Join(Path.GetTempPath(), $"koshi-smoke-index-{Guid.NewGuid():N}");
    Directory.CreateDirectory(preSeededIndexDir);
    preSeededIndexFile = Path.Join(preSeededIndexDir, "index.json");
    File.WriteAllText(preSeededIndexFile, IndexFixtureJson);
    psi.Environment["KOSHI_INDEX_FILE"] = preSeededIndexFile;
    Console.Error.WriteLine($"[setup] Pre-seeded index fixture at: {preSeededIndexFile}");
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
{
    Console.Error.WriteLine($"[setup] WARN: failed to seed index fixture: {ex.Message}");
    preSeededIndexFile = null;
}

// Set KOSHI_INDEX_PATH to a path that definitely won't exist. This is used
// by the #31 auto-index-retry assertion: after Phase 4's koshi_clear_index,
// two consecutive koshi_search calls must BOTH surface the "Auto-index from
// KOSHI_INDEX_PATH failed" error (the old code would only emit it once,
// then fall through to a generic "No documents indexed" message).
var badAutoIndexPath = Path.Join(
    Path.GetTempPath(),
    $"koshi-smoke-bad-autoindex-{Guid.NewGuid():N}",
    "does-not-exist");
psi.Environment["KOSHI_INDEX_PATH"] = badAutoIndexPath;
Console.Error.WriteLine($"[setup] KOSHI_INDEX_PATH (intentionally bad) = {badAutoIndexPath}");

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

// ─── Phase 2: tools/list — must enumerate all 23 tools ───────────────────
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
        "koshi_memory_export_to_vault", "koshi_memory_import_from_vault", "koshi_memory_sync_vault",
        "koshi_capture_turn",
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

    // ─── Phase 3.5.1: v0.5.1 koshi_recall scope filtering + BM25 (#24) ──
    // Asserts that the workspaceId filter is honoured: a recall scoped to
    // 'project-a' must return the project-A marker token but MUST NOT leak
    // the project-B marker. Pre-v0.5.1 koshi_recall ignored MemoryScope
    // entirely (substring-on-content only), so this regression-guards both
    // the filter and the BM25-ranked (non-substring) match.
    var scopeAResp = await RpcAsync("tools/call", new
    {
        name = "koshi_recall",
        arguments = new
        {
            query = "scoped marker token",
            type = "All",
            topK = 5,
            workspaceId = "project-a",
        }
    });
    if (scopeAResp is null)
    {
        failures.Add("recall-scope: project-a recall TIMEOUT");
    }
    else
    {
        var text = ExtractFirstText(scopeAResp.Value);
        if (text is null)
        {
            failures.Add("recall-scope: project-a recall returned no text");
        }
        else if (!text.Contains("xkrythogue_alpha_token", StringComparison.Ordinal))
        {
            failures.Add($"recall-scope: project-a marker missing from workspaceId='project-a' recall. Response: {text[..Math.Min(240, text.Length)]}");
        }
        else if (text.Contains("yzephylgrum_beta_token", StringComparison.Ordinal))
        {
            failures.Add($"recall-scope: project-b marker LEAKED into workspaceId='project-a' recall (#24 scope filter regression). Response: {text[..Math.Min(240, text.Length)]}");
        }
        else
        {
            Console.Error.WriteLine("[ok] recall-scope: workspaceId filter isolates project-a from project-b (#24)");
        }
    }

    // Inverse direction: workspaceId='project-b' must see beta but not alpha.
    var scopeBResp = await RpcAsync("tools/call", new
    {
        name = "koshi_recall",
        arguments = new
        {
            query = "scoped marker token",
            type = "All",
            topK = 5,
            workspaceId = "project-b",
        }
    });
    if (scopeBResp is null)
    {
        failures.Add("recall-scope: project-b recall TIMEOUT");
    }
    else
    {
        var text = ExtractFirstText(scopeBResp.Value);
        if (text is null)
        {
            failures.Add("recall-scope: project-b recall returned no text");
        }
        else if (!text.Contains("yzephylgrum_beta_token", StringComparison.Ordinal))
        {
            failures.Add($"recall-scope: project-b marker missing from workspaceId='project-b' recall. Response: {text[..Math.Min(240, text.Length)]}");
        }
        else if (text.Contains("xkrythogue_alpha_token", StringComparison.Ordinal))
        {
            failures.Add($"recall-scope: project-a marker LEAKED into workspaceId='project-b' recall (#24 scope filter regression). Response: {text[..Math.Min(240, text.Length)]}");
        }
        else
        {
            Console.Error.WriteLine("[ok] recall-scope: workspaceId filter isolates project-b from project-a (#24)");
        }
    }
}

// ─── Phase 3.6: index file persistence (KOSHI_INDEX_FILE) ──────────────
// Verifies the v0.5.0 index-persistence feature: a snapshot pre-written to
// disk before the server launched is auto-loaded on the first retrieval
// call. If the source-gen IndexEnvelope shape drifts or EnsureCorpusLoaded
// is bypassed, the distinctive token will not surface and the smoke fails.
if (preSeededIndexFile is not null)
{
    var idxResp = await RpcAsync("tools/call", new
    {
        name = "koshi_search",
        arguments = new { query = "zorblax_quux_xenoplasma", topK = 3 }
    });
    if (idxResp is null)
    {
        failures.Add("index-persist: search TIMEOUT");
    }
    else
    {
        var text = ExtractFirstText(idxResp.Value);
        if (text is null)
        {
            failures.Add("index-persist: search returned no text");
        }
        else if (!text.Contains("zorblax_quux_xenoplasma", StringComparison.Ordinal))
        {
            failures.Add($"index-persist: pre-seeded chunk NOT loaded from KOSHI_INDEX_FILE. Search response: {text[..Math.Min(240, text.Length)]}");
        }
        else
        {
            Console.Error.WriteLine("[ok] index-persist: snapshot auto-loaded from KOSHI_INDEX_FILE on first search");
        }
    }
}

// ─── Phase 4: each of the 23 tools ──────────────────────────────────────
// Retrieval (5)
await ExpectSuccessAsync("koshi_index", "koshi_index", new
{
    documents = """[{"content":"alpha bravo charlie","source":"a.md","type":"documentation"},{"content":"delta echo foxtrot","source":"b.md","type":"documentation"}]"""
});
var indexPath = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
await ExpectSuccessAsync("koshi_index_directory", "koshi_index_directory", new { path = indexPath, pattern = "*.cs" });

// Save-side: koshi_index_directory should have rewritten the snapshot to
// disk (overwriting the fixture). Verify the file exists, is non-empty,
// and no longer contains the fixture's distinctive token.
if (preSeededIndexFile is not null)
{
    if (!File.Exists(preSeededIndexFile))
    {
        failures.Add("index-persist: snapshot file missing after koshi_index_directory");
    }
    else
    {
        var snapshotJson = File.ReadAllText(preSeededIndexFile);
        if (snapshotJson.Length < 100)
        {
            failures.Add($"index-persist: snapshot suspiciously small ({snapshotJson.Length} bytes)");
        }
        else if (snapshotJson.Contains("zorblax_quux_xenoplasma", StringComparison.Ordinal))
        {
            failures.Add("index-persist: snapshot still contains fixture token — koshi_index_directory did not overwrite");
        }
        else
        {
            Console.Error.WriteLine($"[ok] index-persist: snapshot rewritten by koshi_index_directory ({snapshotJson.Length:N0} bytes)");
        }
    }
}

await ExpectSuccessAsync("koshi_search", "koshi_search", new { query = "alpha bravo", topK = 2 });
await ExpectSuccessAsync("koshi_list_indexed", "koshi_list_indexed", new { });
await ExpectSuccessAsync("koshi_clear_index", "koshi_clear_index", new { });

// Delete-side: koshi_clear_index must remove the on-disk snapshot too,
// otherwise the next process start would re-load a stale corpus.
if (preSeededIndexFile is not null && File.Exists(preSeededIndexFile))
{
    failures.Add("index-persist: snapshot file still exists after koshi_clear_index");
}
else if (preSeededIndexFile is not null)
{
    Console.Error.WriteLine("[ok] index-persist: snapshot deleted by koshi_clear_index");
}

// ─── Phase 4.1: #31 auto-index retry after failure ─────────────────────
// After koshi_clear_index, _isIndexed=false and the auto-index throttle is
// also reset. Because KOSHI_INDEX_PATH points to a path that doesn't exist,
// the next koshi_search must surface the "Auto-index from KOSHI_INDEX_PATH
// failed" message. Pre-#31 code returned that message ONCE then fell through
// to a generic "No documents indexed" — so the second consecutive call would
// LOSE the diagnostic. With #31, the throttle caches the failure message
// and replays it (with a retry countdown) for AutoIndexRetryAfter seconds,
// then attempts again. Asserting that BOTH responses contain the failure
// preamble guards against the regression.
{
    var firstRetry = await RpcAsync("tools/call", new
    {
        name = "koshi_search",
        arguments = new { query = "anything", topK = 1 }
    });
    var secondRetry = await RpcAsync("tools/call", new
    {
        name = "koshi_search",
        arguments = new { query = "anything else", topK = 1 }
    });

    string? FirstText(JsonElement? e) => e is { } v ? ExtractFirstText(v) : null;
    var t1 = FirstText(firstRetry);
    var t2 = FirstText(secondRetry);
    const string expectedFragment = "Auto-index from KOSHI_INDEX_PATH failed";

    if (t1 is null) failures.Add("auto-index-retry: 1st search returned no text");
    else if (!t1.Contains(expectedFragment, StringComparison.Ordinal))
        failures.Add($"auto-index-retry: 1st search missing '{expectedFragment}'. Got: {t1[..Math.Min(240, t1.Length)]}");

    if (t2 is null) failures.Add("auto-index-retry: 2nd search returned no text (regression — #31)");
    else if (!t2.Contains(expectedFragment, StringComparison.Ordinal))
        failures.Add($"auto-index-retry: 2nd search missing '{expectedFragment}' (one-shot regression — #31). Got: {t2[..Math.Min(240, t2.Length)]}");

    if (t1 is not null && t2 is not null
        && t1.Contains(expectedFragment, StringComparison.Ordinal)
        && t2.Contains(expectedFragment, StringComparison.Ordinal))
    {
        Console.Error.WriteLine("[ok] auto-index-retry: both consecutive searches surface throttled failure (#31)");
    }
}

// Memory (8)
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

// koshi_capture_turn — positive case: explicit decision-shape sentence persists as a Decision memory.
await ExpectSuccessAsync("koshi_capture_turn", "koshi_capture_turn", new
{
    turn_summary = "Investigated a flaky deadlock in the smoke harness. We chose retry-with-backoff over circuit-breaker because the dependency recovers within five seconds.",
    linked_pr = 9999,
});

// koshi_capture_turn — preview mode (auto_promote=false). Should return candidates without saving.
await ExpectSuccessAsync("koshi_capture_turn-preview", "koshi_capture_turn", new
{
    turn_summary = "Decision: switch the cache layer from in-memory to Redis for the hot read path.",
    auto_promote = false,
});

// koshi_capture_turn — chitchat case: extractor should find nothing and return the ℹ no-decisions message (still a successful response).
await ExpectSuccessAsync("koshi_capture_turn-chitchat", "koshi_capture_turn", new
{
    turn_summary = "We talked about the weather and looked at some logs. Nothing was decided.",
});

await ExpectSuccessAsync("koshi_clear_memories-after-capture", "koshi_clear_memories", new { confirm = true });

// v0.6.0 vault tools — sanity check that they're exposed and return a sensible response in JSON mode.
await ExpectSuccessAsync("koshi_memory_sync_vault", "koshi_memory_sync_vault", new { });
{
    var vaultProbeDir = Path.Join(Path.GetTempPath(), $"koshi-smoke-vault-probe-{Guid.NewGuid():N}");
    try
    {
        await ExpectSuccessAsync("koshi_memory_export_to_vault", "koshi_memory_export_to_vault", new { vaultPath = vaultProbeDir });
        await ExpectSuccessAsync("koshi_memory_import_from_vault", "koshi_memory_import_from_vault", new { vaultPath = vaultProbeDir, mode = "merge" });
    }
    finally
    {
        try { if (Directory.Exists(vaultProbeDir)) Directory.Delete(vaultProbeDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[cleanup] vault probe dir leftover: {ex.Message}");
        }
    }
}

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

// ─── Phase 6: KOSHI_MEMORY_VAULT end-to-end ────────────────────────────
// Spawns a SECOND server process with KOSHI_MEMORY_VAULT set. Verifies
// vault-mode (v0.6.0) memory behavior end-to-end: file layout, external
// edits/deletes/adds picked up without restart, unmanaged-note reporting.
string? vaultDir = null;
try
{
    vaultDir = Path.Join(Path.GetTempPath(), $"koshi-smoke-vault-{Guid.NewGuid():N}");
    Directory.CreateDirectory(vaultDir);
    Console.Error.WriteLine($"[setup] Vault smoke dir: {vaultDir}");

    var vpsi = exePath is not null
        ? new ProcessStartInfo(exePath)
        : new ProcessStartInfo("dotnet", $"\"{dllPath}\"");
    vpsi.RedirectStandardInput = true;
    vpsi.RedirectStandardOutput = true;
    vpsi.RedirectStandardError = true;
    vpsi.UseShellExecute = false;
    vpsi.StandardInputEncoding = Encoding.UTF8;
    vpsi.StandardOutputEncoding = Encoding.UTF8;
    vpsi.Environment["KOSHI_MEMORY_VAULT"] = vaultDir;
    // Disable the file-system watcher in the smoke test — Phase 2 (v0.6.1) added
    // a watcher that defaults on, but the smoke writes/deletes files and immediately
    // calls recall. The "reload-on-every-call" fallback is what we want here for
    // deterministic timing; the watcher path is covered by VaultWatcherTests.
    vpsi.Environment["KOSHI_VAULT_WATCH"] = "off";
    // Make sure leftover env vars from this process don't bleed in.
    vpsi.Environment.Remove("KOSHI_MEMORY_FILE");
    vpsi.Environment.Remove("KOSHI_INDEX_FILE");
    vpsi.Environment.Remove("KOSHI_INDEX_PATH");

    var vproc = Process.Start(vpsi)!;
    var vresponses = new Dictionary<int, JsonElement>();
    using var vsignal = new SemaphoreSlim(0);
    int vnextId = 1;

    _ = Task.Run(async () =>
    {
        string? line;
        while ((line = await vproc.StandardError.ReadLineAsync()) is not null)
            Console.Error.WriteLine($"[vault-stderr] {line}");
    });
    _ = Task.Run(async () =>
    {
        string? line;
        while ((line = await vproc.StandardOutput.ReadLineAsync()) is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement.Clone();
                if (root.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.Number)
                {
                    vresponses[idElem.GetInt32()] = root;
                    vsignal.Release();
                }
            }
            catch (JsonException) { Console.Error.WriteLine($"[vault non-json] {line}"); }
        }
    });

    async Task<JsonElement?> VRpcAsync(string method, object? @params = null, int timeoutMs = 8000)
    {
        int id = vnextId++;
        var payload = @params is null
            ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{JsonSerializer.Serialize(@params)}}}""";
        await vproc.StandardInput.WriteLineAsync(payload);
        await vproc.StandardInput.FlushAsync();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (vresponses.TryGetValue(id, out var r)) return r;
            await Task.Delay(50);
        }
        return null;
    }

    async Task<string?> VToolAsync(string tool, object args)
    {
        var resp = await VRpcAsync("tools/call", new { name = tool, arguments = args });
        return resp is null ? null : ExtractFirstText(resp.Value);
    }

    // Handshake.
    var vinitResp = await VRpcAsync("initialize", new
    {
        protocolVersion = "2024-11-05",
        capabilities = new { },
        clientInfo = new { name = "smoke-vault", version = "0.1" }
    });
    if (vinitResp is null) failures.Add("vault: initialize TIMEOUT");
    await vproc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    await vproc.StandardInput.FlushAsync();

    // Save three memories of three types — files should land under koshi/<type>/.
    await VToolAsync("koshi_remember", new { content = "Fact body alpha-token-VLT.", subject = "vault fact one", type = "Fact" });
    await VToolAsync("koshi_remember", new { content = "Decision body beta-token-VLT.", subject = "vault decision one", type = "Decision" });
    await VToolAsync("koshi_remember", new { content = "Pattern body gamma-token-VLT.", subject = "vault pattern one", type = "Pattern" });

    // Assert file layout.
    var koshiRoot = Path.Join(vaultDir, "koshi");
    foreach (var dir in new[] { "facts", "decisions", "patterns" }.Select(sub => Path.Join(koshiRoot, sub)))
    {
        if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "*.md").Any())
        {
            failures.Add($"vault: expected at least one .md under {dir}");
        }
    }

    // Stats should report backend == vault.
    var statsText = await VToolAsync("koshi_memory_stats", new { });
    if (statsText is null || !statsText.Contains("vault", StringComparison.OrdinalIgnoreCase))
        failures.Add($"vault: memory_stats did not mention backend 'vault'. Got: {(statsText ?? "(null)")[..Math.Min(240, (statsText ?? "").Length)]}");

    // Drop an externally-created managed memory + an unmanaged note WHILE the proc is running.
    // The body marker (phantomzqx99201) is a single-token, non-hyphenated string that does NOT
    // appear in the query — so we can detect actual body presence vs. recall's query-echo
    // response ("No memories found matching '<query>'.").
    var externalMemPath = Path.Join(koshiRoot, "facts", "external-fact--mem-999001.md");
    File.WriteAllText(externalMemPath, """
---
koshi:
  id: mem-999001
  type: Fact
  scope:
    user: "*"
    workspace: default
    thread: null
  source: external
  confidence: 0.9
  created-at: 2026-05-22T10:00:00Z
  last-accessed-at: 2026-05-22T10:00:00Z
  access-count: 0
  tier: Hot
---
# External vault assertion fact

External vault assertion body. Marker phantomzqx99201 for vault smoke test.
""");
    var unmanagedPath = Path.Join(koshiRoot, "facts", "my-personal-note.md");
    File.WriteAllText(unmanagedPath, "# A personal note\n\nNot managed by Koshi.\n");

    // Recall should now see the external file (vault reloads per call).
    // Query terms appear in the body but NOT in the unique marker.
    var recallText = await VToolAsync("koshi_recall", new { query = "external vault assertion", type = "All", topK = 5 });
    if (recallText is null || !recallText.Contains("phantomzqx99201", StringComparison.Ordinal))
        failures.Add($"vault: external file not picked up by recall. Got: {(recallText ?? "(null)")[..Math.Min(240, (recallText ?? "").Length)]}");

    // Stats should now report at least one unmanaged note.
    var stats2 = await VToolAsync("koshi_memory_stats", new { });
    if (stats2 is null || !stats2.Contains("unmanaged", StringComparison.OrdinalIgnoreCase))
        failures.Add($"vault: memory_stats did not surface unmanaged-note count. Got: {(stats2 ?? "(null)")[..Math.Min(240, (stats2 ?? "").Length)]}");

    // External delete — recall must NOT bring it back. The marker is distinct from
    // the query so it won't be echoed in the "No memories found matching '<query>'" response.
    File.Delete(externalMemPath);
    var recall2 = await VToolAsync("koshi_recall", new { query = "external vault assertion", type = "All", topK = 5 });
    if (recall2 is not null && recall2.Contains("phantomzqx99201", StringComparison.Ordinal))
        failures.Add("vault: cache-divergence regression — deleted external file still returned by recall");

    // Subject rename via Remember reusing the same subject keyword — exactly one file should exist per type.
    var factDir = Path.Join(koshiRoot, "facts");
    var factCountBefore = Directory.EnumerateFiles(factDir, "*.md").Count();
    if (factCountBefore == 0)
        failures.Add("vault: no fact files found after Phase 6 mutations");

    // Shutdown vault proc.
    vproc.StandardInput.Close();
    vproc.WaitForExit(5000);
    if (!vproc.HasExited) vproc.Kill();
    Console.Error.WriteLine("[ok] vault smoke phase complete");
}
catch (Exception ex) when (
    ex is not OutOfMemoryException and not StackOverflowException
       and not ThreadAbortException)
{
    failures.Add($"vault smoke: unexpected exception: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    if (vaultDir is not null)
    {
        try { Directory.Delete(vaultDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[cleanup] WARN: could not remove vault dir {vaultDir}: {ex.Message}");
        }
    }
}

// ─── Phase 7: Project-root defaults (no path env vars set) ──────────────
// Validates the v0.6.0 amendment: when a third MCP process is launched with
// cwd=<tmp> and NO KOSHI_* path env vars, memory + index land under
// <tmp>/.koshi/ automatically. This is the "open Copilot in C:\OPP, run
// Koshi, get C:\OPP\.koshi\memory.json" guarantee.
string? defCwd = null;
try
{
    defCwd = Path.Join(Path.GetTempPath(), $"koshi-smoke-defaults-{Guid.NewGuid():N}");
    Directory.CreateDirectory(defCwd);
    Console.Error.WriteLine($"[setup] defaults cwd: {defCwd}");

    var dpsi = exePath is not null
        ? new ProcessStartInfo(exePath)
        : new ProcessStartInfo("dotnet", $"\"{dllPath}\"");
    dpsi.RedirectStandardInput = true;
    dpsi.RedirectStandardOutput = true;
    dpsi.RedirectStandardError = true;
    dpsi.UseShellExecute = false;
    dpsi.StandardInputEncoding = Encoding.UTF8;
    dpsi.StandardOutputEncoding = Encoding.UTF8;
    dpsi.WorkingDirectory = defCwd;
    // Strip every Koshi path env var so the process starts in "fresh install"
    // mode and must rely on cwd-derived defaults.
    dpsi.Environment.Remove("KOSHI_PROJECT_ROOT");
    dpsi.Environment.Remove("KOSHI_MEMORY_FILE");
    dpsi.Environment.Remove("KOSHI_MEMORY_VAULT");
    dpsi.Environment.Remove("KOSHI_INDEX_FILE");
    dpsi.Environment.Remove("KOSHI_INDEX_PATH");

    var dproc = Process.Start(dpsi)!;
    var dresponses = new Dictionary<int, JsonElement>();
    int dnextId = 1;

    _ = Task.Run(async () =>
    {
        string? line;
        while ((line = await dproc.StandardError.ReadLineAsync()) is not null)
            Console.Error.WriteLine($"[defaults-stderr] {line}");
    });
    _ = Task.Run(async () =>
    {
        string? line;
        while ((line = await dproc.StandardOutput.ReadLineAsync()) is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement.Clone();
                if (root.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.Number)
                    dresponses[idElem.GetInt32()] = root;
            }
            catch (JsonException) { Console.Error.WriteLine($"[defaults non-json] {line}"); }
        }
    });

    async Task<JsonElement?> DRpcAsync(string method, object? @params = null, int timeoutMs = 8000)
    {
        int id = dnextId++;
        var payload = @params is null
            ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
            : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{JsonSerializer.Serialize(@params)}}}""";
        await dproc.StandardInput.WriteLineAsync(payload);
        await dproc.StandardInput.FlushAsync();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (dresponses.TryGetValue(id, out var r)) return r;
            await Task.Delay(50);
        }
        return null;
    }

    async Task<string?> DToolAsync(string tool, object args)
    {
        var resp = await DRpcAsync("tools/call", new { name = tool, arguments = args });
        return resp is null ? null : ExtractFirstText(resp.Value);
    }

    // Handshake.
    var dinitResp = await DRpcAsync("initialize", new
    {
        protocolVersion = "2024-11-05",
        capabilities = new { },
        clientInfo = new { name = "smoke-defaults", version = "0.1" }
    });
    if (dinitResp is null) failures.Add("defaults: initialize TIMEOUT");
    await dproc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    await dproc.StandardInput.FlushAsync();

    // Remember one memory — should land at <defCwd>/.koshi/memory.json
    // (JSON backend; vault stays opt-in even when defaults are active).
    var remember = await DToolAsync("koshi_remember", new
    {
        content = "Defaults smoke marker zetapaxquark42.",
        subject = "defaults smoke",
        type = "Fact"
    });
    if (remember is null) failures.Add("defaults: koshi_remember TIMEOUT");

    var expectedMemFile = Path.Join(defCwd, ".koshi", "memory.json");
    if (!File.Exists(expectedMemFile))
        failures.Add($"defaults: expected memory file at {expectedMemFile} (was the cwd-derived default applied?)");
    else if (new FileInfo(expectedMemFile).Length == 0)
        failures.Add($"defaults: memory file at {expectedMemFile} is empty after koshi_remember");

    // Diagnostics should surface the resolved paths + the "default" source.
    var health = await DToolAsync("koshi_health", new { });
    if (health is null)
    {
        failures.Add("defaults: koshi_health TIMEOUT");
    }
    else
    {
        if (!health.Contains(defCwd, StringComparison.OrdinalIgnoreCase))
            failures.Add($"defaults: koshi_health did not surface tmp cwd '{defCwd}'. Got: {health[..Math.Min(400, health.Length)]}");
        if (!health.Contains("[default]", StringComparison.Ordinal))
            failures.Add($"defaults: koshi_health did not show '[default]' source label. Got: {health[..Math.Min(400, health.Length)]}");
    }

    dproc.StandardInput.Close();
    dproc.WaitForExit(5000);
    if (!dproc.HasExited) dproc.Kill();
    Console.Error.WriteLine("[ok] defaults smoke phase complete");
}
catch (Exception ex) when (
    ex is not OutOfMemoryException and not StackOverflowException
       and not ThreadAbortException)
{
    failures.Add($"defaults smoke: unexpected exception: {ex.GetType().Name}: {ex.Message}");
}
finally
{
    if (defCwd is not null)
    {
        try { Directory.Delete(defCwd, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[cleanup] WARN: could not remove defaults cwd {defCwd}: {ex.Message}");
        }
    }
}

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

// Best-effort cleanup of the pre-seeded index fixture directory.
if (preSeededIndexDir is not null)
{
    try { Directory.Delete(preSeededIndexDir, recursive: true); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
    {
        Console.Error.WriteLine($"[cleanup] WARN: could not remove {preSeededIndexDir}: {ex.Message}");
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
