using System.Text.Json;
using Koshi.Mcp.Tools;
using static Koshi.Core.Tests.StructuredOutputAssertions;

namespace Koshi.Core.Tests;

/// <summary>
/// Per-tool tests for the structured-output (JSON envelope) mode wired by
/// #66 Phase 2b. Covers the 11 mutation/persistence tools that gained the
/// <c>format</c> parameter in this phase:
///   - <c>koshi_remember</c>
///   - <c>koshi_forget</c>
///   - <c>koshi_clear_memories</c>
///   - <c>koshi_capture_turn</c>
///   - <c>koshi_memory_export_to_vault</c>
///   - <c>koshi_memory_import_from_vault</c>
///   - <c>koshi_memory_sync_vault</c>
///   - <c>koshi_index</c>
///   - <c>koshi_index_directory</c>
///   - <c>koshi_clear_index</c>
///   - <c>koshi_register_team</c>
///
/// Each tool is exercised across three axes:
///   1. Text-mode default is unchanged (no JSON envelope leaks in).
///   2. <c>format="json"</c> produces a valid envelope with a tool-specific
///      data shape and the expected <c>tool</c> field.
///   3. <c>format</c> validation: unknown value → invalid_format envelope.
///
/// Plus tool-specific edge cases (idempotency, confirmation guards, structured
/// reason codes, create vs update discriminator).
/// </summary>
[Collection("RetrievalTools-static")]
public sealed class StructuredOutputPhase2bToolTests : IDisposable
{
    private const string CorpusName = "phase2b-test-corpus";
    private readonly string _scratchSubject;
    private readonly string _vaultRoot;

    public StructuredOutputPhase2bToolTests()
    {
        _scratchSubject = "phase2b-scratch-" + Guid.NewGuid().ToString("N")[..10];
        _vaultRoot = Path.Join(Path.GetTempPath(), "koshi-phase2b-vault-" + Guid.NewGuid().ToString("N")[..10]);
    }

    public void Dispose()
    {
        // Best-effort cleanup of any memories left from individual tests so a
        // failed earlier test cannot pollute subsequent runs. Tests use unique
        // subjects but Forget+ClearMemories provide defence in depth.
        try { MemoryTools.Forget(_scratchSubject); } catch (Exception ex) { _ = ex; }
        RetrievalTools.ClearIndex(CorpusName);
        try { Directory.Delete(_vaultRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        GC.SuppressFinalize(this);
    }

    // ═══════════════════════════ koshi_remember ════════════════════════════

    [Fact]
    public void Remember_text_mode_default_is_unchanged()
    {
        var output = MemoryTools.Remember(
            content: "we use Postgres 16",
            subject: _scratchSubject,
            type: "Decision");

        Assert.Contains("✅", output);
        Assert.DoesNotContain("\"schema_version\"", output);
        Assert.DoesNotContain("\"ok\":", output);
    }

    [Fact]
    public void Remember_json_mode_returns_well_formed_envelope()
    {
        var output = MemoryTools.Remember(
            content: "we use Postgres 16",
            subject: _scratchSubject,
            type: "Decision",
            confidence: 0.9f,
            source: "user",
            format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_remember");

        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("id").GetString()));
        Assert.Equal("Decision", data.GetProperty("type").GetString());
        Assert.Equal(_scratchSubject, data.GetProperty("subject").GetString());
        Assert.Equal("user", data.GetProperty("source").GetString());
        Assert.InRange(data.GetProperty("confidence").GetDouble(), 0.89, 0.91);
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("created_at").GetString()));
        Assert.False(data.GetProperty("embedded").GetBoolean());
        Assert.True(data.GetProperty("persistence_enabled").GetBoolean());

        var scope = data.GetProperty("scope");
        Assert.Equal("*", scope.GetProperty("user_id").GetString());
        Assert.Equal("default", scope.GetProperty("workspace_id").GetString());
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("thread_id").ValueKind);
    }

    [Fact]
    public void Remember_invalid_format_returns_error_envelope()
    {
        // Format string must contain "json" so Resolve returns OutputFormat.Json
        // even on rejection — that is what makes the error envelope renderable.
        var output = MemoryTools.Remember(
            content: "x", subject: _scratchSubject, format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_remember");
    }

    [Fact]
    public void Remember_empty_content_returns_error_envelope_in_json_mode()
    {
        var output = MemoryTools.Remember(
            content: "   ", subject: _scratchSubject, format: "json");
        AssertErrorEnvelope(output, "empty_content", expectedTool: "koshi_remember");
    }

    [Fact]
    public void Remember_empty_subject_returns_error_envelope_in_json_mode()
    {
        var output = MemoryTools.Remember(
            content: "non-empty", subject: "", format: "json");
        AssertErrorEnvelope(output, "empty_subject", expectedTool: "koshi_remember");
    }

    // ═══════════════════════════ koshi_forget ══════════════════════════════

    [Fact]
    public void Forget_text_mode_default_is_unchanged()
    {
        MemoryTools.Remember("dummy", _scratchSubject);
        var output = MemoryTools.Forget(_scratchSubject);

        Assert.Contains("✅", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void Forget_json_mode_returns_well_formed_envelope_with_removed_count()
    {
        MemoryTools.Remember("dummy", _scratchSubject);
        var output = MemoryTools.Forget(_scratchSubject, format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_forget");

        Assert.Equal(_scratchSubject, data.GetProperty("subject").GetString());
        Assert.True(data.GetProperty("removed").GetInt32() >= 1);
        Assert.True(data.GetProperty("deleted_ids").GetArrayLength() >= 1);
    }

    [Fact]
    public void Forget_no_match_is_idempotent_returns_ok_with_zero_removed()
    {
        // N4: missing subject is not an error in JSON mode — successful no-op.
        var output = MemoryTools.Forget("nonexistent-" + Guid.NewGuid().ToString("N"), format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_forget");

        Assert.Equal(0, data.GetProperty("removed").GetInt32());
        Assert.Equal(0, data.GetProperty("deleted_ids").GetArrayLength());
    }

    [Fact]
    public void Forget_invalid_format_returns_error_envelope()
    {
        var output = MemoryTools.Forget(_scratchSubject, format: "ndjson");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_forget");
    }

    [Fact]
    public void Forget_empty_subject_returns_error_envelope_in_json_mode()
    {
        var output = MemoryTools.Forget("", format: "json");
        AssertErrorEnvelope(output, "empty_subject", expectedTool: "koshi_forget");
    }

    // Multi-model review S1: koshi_forget's tool description promises
    // case-insensitive substring matching against the subject. Prior to the
    // fix the implementation used Equals which silently dropped every
    // forget call whose argument wasn't an exact whole-subject match. Lock
    // the substring semantics in so the contract can't regress again.
    [Fact]
    public void Forget_matches_subject_via_case_insensitive_substring()
    {
        var unique = "psql-substring-" + Guid.NewGuid().ToString("N")[..8];
        MemoryTools.Remember("decision body", subject: $"we use {unique} for auth", type: "Decision");

        // Lower-case partial match should still remove the row.
        var output = MemoryTools.Forget(unique.ToLowerInvariant(), format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_forget");

        Assert.True(data.GetProperty("removed").GetInt32() >= 1,
            "Forget should match via case-insensitive substring, not Equals");
    }

    // ════════════════════════ koshi_clear_memories ═════════════════════════

    [Fact]
    public void ClearMemories_text_mode_unconfirmed_is_unchanged()
    {
        var output = MemoryTools.ClearMemories(confirm: false);

        Assert.Contains("confirm", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void ClearMemories_unconfirmed_returns_confirmation_required_error_in_json_mode()
    {
        // Judgment call: even though the call did no work, returning ok=true
        // would silently look like a successful clear when it was actually a
        // guard rejection. The error envelope makes the two-step contract
        // explicit and machine-checkable.
        var output = MemoryTools.ClearMemories(confirm: false, format: "json");
        AssertErrorEnvelope(output, "confirmation_required", expectedTool: "koshi_clear_memories");
    }

    [Fact]
    public void ClearMemories_invalid_format_returns_error_envelope()
    {
        var output = MemoryTools.ClearMemories(confirm: true, format: "json-array");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_clear_memories");
    }

    // ═══════════════════════════ koshi_capture_turn ════════════════════════

    [Fact]
    public void CaptureTurn_text_mode_default_is_unchanged()
    {
        var output = MemoryTools.CaptureTurn(
            turn_summary: $"Decision: we use Postgres 16 for {_scratchSubject}");

        Assert.DoesNotContain("\"schema_version\"", output);
        // Either persisted (✅) or preview text — both are pure text artefacts.
        Assert.False(output.TrimStart().StartsWith("{"), $"text mode must not emit JSON: {output}");
    }

    [Fact]
    public void CaptureTurn_json_mode_returns_well_formed_envelope()
    {
        var output = MemoryTools.CaptureTurn(
            turn_summary: $"Decision: capture-turn json wiring for {_scratchSubject}",
            workspaceId: _scratchSubject,
            format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_capture_turn");

        Assert.True(data.GetProperty("auto_promoted").GetBoolean());
        Assert.True(data.GetProperty("candidates_extracted").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("saved").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("skipped").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("candidates").ValueKind);

        var scope = data.GetProperty("scope");
        Assert.Equal(_scratchSubject, scope.GetProperty("workspace_id").GetString());

        // Cleanup — capture_turn may have promoted a memory.
        MemoryTools.Forget("capture-turn json wiring");
    }

    [Fact]
    public void CaptureTurn_no_decisions_detected_returns_ok_with_reason_code()
    {
        // N8: empty extraction is a structured ok result with a reason code,
        // not an error envelope.
        var output = MemoryTools.CaptureTurn(
            turn_summary: "hello world, how are you today, this is just chitchat",
            format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_capture_turn");

        var reason = data.GetProperty("reason").GetString();
        Assert.Contains(reason, new[] { "no_decisions_detected", "all_candidates_below_floor" });
        Assert.Equal(0, data.GetProperty("saved").GetArrayLength());
    }

    [Fact]
    public void CaptureTurn_auto_promote_false_exposes_candidates_without_saving()
    {
        var output = MemoryTools.CaptureTurn(
            turn_summary: $"Decision: preview-only capture for {_scratchSubject}",
            auto_promote: false,
            format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_capture_turn");

        Assert.False(data.GetProperty("auto_promoted").GetBoolean());
        Assert.Equal(0, data.GetProperty("saved").GetArrayLength());
        Assert.True(data.GetProperty("candidates").GetArrayLength() >= 1);
    }

    [Fact]
    public void CaptureTurn_invalid_format_returns_error_envelope()
    {
        var output = MemoryTools.CaptureTurn(turn_summary: "Decision: x", format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_capture_turn");
    }

    [Fact]
    public void CaptureTurn_empty_summary_returns_error_envelope_in_json_mode()
    {
        var output = MemoryTools.CaptureTurn(turn_summary: "   ", format: "json");
        AssertErrorEnvelope(output, "empty_summary", expectedTool: "koshi_capture_turn");
    }

    // ═══════════════════════ koshi_memory_export_to_vault ══════════════════

    [Fact]
    public void ExportToVault_text_mode_default_is_unchanged()
    {
        Directory.CreateDirectory(_vaultRoot);
        var output = MemoryTools.ExportToVault(_vaultRoot, overwrite: true);

        Assert.Contains("✅", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void ExportToVault_json_mode_returns_well_formed_envelope()
    {
        Directory.CreateDirectory(_vaultRoot);
        var output = MemoryTools.ExportToVault(_vaultRoot, overwrite: true, format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_memory_export_to_vault");

        // VaultBackend canonicalises the path — we just assert it is a valid
        // string and the resolved path actually exists on disk.
        var vaultPathOut = data.GetProperty("vault_path").GetString();
        Assert.False(string.IsNullOrWhiteSpace(vaultPathOut));
        Assert.True(Directory.Exists(vaultPathOut));

        Assert.Equal("obsidian", data.GetProperty("flavor").GetString());
        Assert.True(data.GetProperty("exported").GetInt32() >= 0);
    }

    [Fact]
    public void ExportToVault_empty_path_returns_error_envelope_in_json_mode()
    {
        var output = MemoryTools.ExportToVault("   ", format: "json");
        AssertErrorEnvelope(output, "empty_path", expectedTool: "koshi_memory_export_to_vault");
    }

    [Fact]
    public void ExportToVault_invalid_format_returns_error_envelope()
    {
        var output = MemoryTools.ExportToVault(_vaultRoot, format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_memory_export_to_vault");
    }

    // ═══════════════════ koshi_memory_import_from_vault ════════════════════

    [Fact]
    public void ImportFromVault_text_mode_default_is_unchanged()
    {
        Directory.CreateDirectory(_vaultRoot);
        var output = MemoryTools.ImportFromVault(_vaultRoot);

        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void ImportFromVault_empty_vault_returns_ok_with_added_zero()
    {
        // N3: idempotent — importing from an empty vault is not an error.
        Directory.CreateDirectory(_vaultRoot);
        var output = MemoryTools.ImportFromVault(_vaultRoot, format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_memory_import_from_vault");

        Assert.Equal("merge", data.GetProperty("mode").GetString());
        Assert.Equal(0, data.GetProperty("incoming").GetInt32());
        Assert.Equal(0, data.GetProperty("added").GetInt32());
    }

    [Fact]
    public void ImportFromVault_invalid_mode_returns_error_envelope_in_json_mode()
    {
        Directory.CreateDirectory(_vaultRoot);
        var output = MemoryTools.ImportFromVault(_vaultRoot, mode: "rebase", format: "json");
        AssertErrorEnvelope(output, "invalid_mode", expectedTool: "koshi_memory_import_from_vault");
    }

    [Fact]
    public void ImportFromVault_invalid_format_returns_error_envelope()
    {
        Directory.CreateDirectory(_vaultRoot);
        var output = MemoryTools.ImportFromVault(_vaultRoot, format: "json-lines");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_memory_import_from_vault");
    }

    // ═══════════════════════ koshi_memory_sync_vault ═══════════════════════

    [Fact]
    public void SyncVault_text_mode_default_is_unchanged()
    {
        var output = MemoryTools.SyncVault();
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void SyncVault_non_vault_backend_returns_ok_with_reason_not_a_vault()
    {
        // Default backend in tests is the JSON file backend, not a vault — the
        // tool returns ok=true with synced=false and reason="not_a_vault".
        var output = MemoryTools.SyncVault(format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_memory_sync_vault");

        if (data.GetProperty("backend").GetString() != "vault")
        {
            Assert.False(data.GetProperty("synced").GetBoolean());
            Assert.Equal("not_a_vault", data.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public void SyncVault_invalid_format_returns_error_envelope()
    {
        var output = MemoryTools.SyncVault(format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_memory_sync_vault");
    }

    // ═════════════════════════════ koshi_index ═════════════════════════════

    [Fact]
    public void Index_text_mode_default_is_unchanged()
    {
        var docs = "[{\"content\":\"hello world\",\"source\":\"a.md\"}]";
        var output = RetrievalTools.Index(docs, corpus: CorpusName);

        Assert.Contains("✅", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void Index_json_mode_returns_well_formed_envelope()
    {
        var docs = "[{\"content\":\"hello world\",\"source\":\"a.md\"}]";
        var output = RetrievalTools.Index(docs, corpus: CorpusName, format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_index");

        Assert.Equal(CorpusName, data.GetProperty("corpus").GetString());
        Assert.True(data.GetProperty("is_named_corpus").GetBoolean());
        Assert.Equal(1, data.GetProperty("documents").GetInt32());
        Assert.True(data.GetProperty("chunks").GetInt32() >= 1);
        Assert.True(data.GetProperty("tokens").GetInt32() > 0);
    }

    [Fact]
    public void Index_invalid_json_returns_error_envelope_in_json_mode()
    {
        var output = RetrievalTools.Index("not a json array", corpus: CorpusName, format: "json");
        AssertErrorEnvelope(output, "invalid_json", expectedTool: "koshi_index");
    }

    [Fact]
    public void Index_no_documents_returns_error_envelope_in_json_mode()
    {
        var output = RetrievalTools.Index("[]", corpus: CorpusName, format: "json");
        AssertErrorEnvelope(output, "no_documents", expectedTool: "koshi_index");
    }

    [Fact]
    public void Index_invalid_format_returns_error_envelope()
    {
        var output = RetrievalTools.Index("[]", corpus: CorpusName, format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_index");
    }

    // ════════════════════════ koshi_index_directory ════════════════════════

    [Fact]
    public async Task IndexDirectory_text_mode_default_is_unchanged()
    {
        var tmp = Path.Join(Path.GetTempPath(), "koshi-idxdir-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Join(tmp, "a.md"), "hello world");
            var output = await RetrievalTools.IndexDirectory(path: tmp, corpus: CorpusName);

            Assert.Contains("✅", output);
            Assert.DoesNotContain("\"schema_version\"", output);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    [Fact]
    public async Task IndexDirectory_json_mode_returns_well_formed_envelope()
    {
        var tmp = Path.Join(Path.GetTempPath(), "koshi-idxdir-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Join(tmp, "a.md"), "hello world from phase 2b test");
            var output = await RetrievalTools.IndexDirectory(path: tmp, corpus: CorpusName, format: "json");
            var data = AssertOkEnvelope(output, expectedTool: "koshi_index_directory");

            Assert.Equal(CorpusName, data.GetProperty("corpus").GetString());
            Assert.True(data.GetProperty("is_named_corpus").GetBoolean());
            Assert.Equal(tmp, data.GetProperty("path").GetString());
            Assert.True(data.GetProperty("files_indexed").GetInt32() >= 1);
            Assert.True(data.GetProperty("chunks").GetInt32() >= 1);
            // Named corpora are never persisted to a snapshot.
            Assert.False(data.GetProperty("snapshot_saved").GetBoolean());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("snapshot_path").ValueKind);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    [Fact]
    public async Task IndexDirectory_directory_not_found_returns_error_envelope_in_json_mode()
    {
        var nonExistent = Path.Join(Path.GetTempPath(), "koshi-does-not-exist-" + Guid.NewGuid().ToString("N"));
        var output = await RetrievalTools.IndexDirectory(path: nonExistent, format: "json");
        AssertErrorEnvelope(output, "directory_not_found", expectedTool: "koshi_index_directory");
    }

    [Fact]
    public async Task IndexDirectory_cancellation_propagates_oce_in_json_mode()
    {
        // OCE must propagate (not become an envelope) — MCP host treats it as
        // cancellation, not a tool response.
        var tmp = Path.Join(Path.GetTempPath(), "koshi-idxdir-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(tmp);
        try
        {
            for (int i = 0; i < 20; i++)
                File.WriteAllText(Path.Join(tmp, $"f{i}.md"), "content " + i);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await RetrievalTools.IndexDirectory(
                    path: tmp, corpus: CorpusName,
                    format: "json", cancellationToken: cts.Token));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    [Fact]
    public async Task IndexDirectory_invalid_format_returns_error_envelope()
    {
        var output = await RetrievalTools.IndexDirectory(path: Path.GetTempPath(), format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_index_directory");
    }

    // ══════════════════════════ koshi_clear_index ══════════════════════════

    [Fact]
    public void ClearIndex_text_mode_default_is_unchanged()
    {
        RetrievalTools.Index("[{\"content\":\"x\",\"source\":\"a\"}]", corpus: CorpusName);
        var output = RetrievalTools.ClearIndex(CorpusName);

        Assert.Contains("✅", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void ClearIndex_named_corpus_json_mode_returns_well_formed_envelope()
    {
        RetrievalTools.Index("[{\"content\":\"x\",\"source\":\"a\"}]", corpus: CorpusName);
        var output = RetrievalTools.ClearIndex(corpus: CorpusName, format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_clear_index");

        Assert.Equal(CorpusName, data.GetProperty("corpus_resolved").GetString());
        Assert.Equal("named", data.GetProperty("mode").GetString());
        Assert.True(data.GetProperty("chunks_removed").GetInt32() >= 1);
        Assert.False(data.GetProperty("snapshot_deleted").GetBoolean());
    }

    [Fact]
    public void ClearIndex_unknown_named_corpus_is_idempotent_returns_ok_with_zero_removed()
    {
        // N5 (judgment): mutations are idempotent — unknown corpus → ok with zero.
        var unknownName = "no-such-corpus-" + Guid.NewGuid().ToString("N")[..10];
        var output = RetrievalTools.ClearIndex(corpus: unknownName, format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_clear_index");

        Assert.Equal(unknownName, data.GetProperty("corpus_resolved").GetString());
        Assert.Equal("named", data.GetProperty("mode").GetString());
        Assert.Equal(0, data.GetProperty("chunks_removed").GetInt32());
        Assert.Equal(0, data.GetProperty("named_corpora_cleared").GetArrayLength());
    }

    [Fact]
    public void ClearIndex_default_corpus_json_mode_returns_well_formed_envelope()
    {
        var output = RetrievalTools.ClearIndex(format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_clear_index");

        Assert.Equal("default", data.GetProperty("corpus_resolved").GetString());
        Assert.Equal("default", data.GetProperty("mode").GetString());
    }

    [Fact]
    public void ClearIndex_star_clears_all_in_json_mode()
    {
        RetrievalTools.Index("[{\"content\":\"x\",\"source\":\"a\"}]", corpus: CorpusName);
        var output = RetrievalTools.ClearIndex(corpus: "*", format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_clear_index");

        Assert.Equal("*", data.GetProperty("corpus_resolved").GetString());
        Assert.Equal("all", data.GetProperty("mode").GetString());
    }

    [Fact]
    public void ClearIndex_invalid_format_returns_error_envelope()
    {
        var output = RetrievalTools.ClearIndex(format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_clear_index");
    }

    // ════════════════════════ koshi_register_team ══════════════════════════

    [Fact]
    public void RegisterTeam_text_mode_default_is_unchanged()
    {
        var teamId = "phase2b-team-" + Guid.NewGuid().ToString("N")[..10];
        var output = TeamTools.RegisterTeam(teamId: teamId, name: "Phase 2b Team");

        Assert.Contains("✅", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void RegisterTeam_json_mode_created_returns_well_formed_envelope()
    {
        var teamId = "phase2b-team-" + Guid.NewGuid().ToString("N")[..10];
        var output = TeamTools.RegisterTeam(
            teamId: teamId, name: "Phase 2b Team",
            tokenBudget: 12000, topK: 7, qualityTarget: 0.85f,
            format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_register_team");

        Assert.Equal(teamId, data.GetProperty("team_id").GetString());
        Assert.Equal("Phase 2b Team", data.GetProperty("name").GetString());
        Assert.Equal("created", data.GetProperty("operation").GetString());
        Assert.Equal(12000, data.GetProperty("context_budget_tokens").GetInt32());
        Assert.Equal(7, data.GetProperty("retrieval_top_k").GetInt32());
        Assert.InRange(data.GetProperty("quality_target").GetDouble(), 0.84, 0.86);
        Assert.False(data.GetProperty("has_system_prompt").GetBoolean());
        Assert.False(data.GetProperty("has_team_context").GetBoolean());
    }

    [Fact]
    public void RegisterTeam_repeated_call_returns_operation_updated()
    {
        var teamId = "phase2b-team-update-" + Guid.NewGuid().ToString("N")[..10];
        TeamTools.RegisterTeam(teamId: teamId, name: "Initial");
        var output = TeamTools.RegisterTeam(
            teamId: teamId, name: "Updated",
            tokenBudget: 4096, format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_register_team");

        Assert.Equal("updated", data.GetProperty("operation").GetString());
        Assert.Equal("Updated", data.GetProperty("name").GetString());
        Assert.Equal(4096, data.GetProperty("context_budget_tokens").GetInt32());
    }

    [Fact]
    public void RegisterTeam_invalid_team_returns_error_envelope_in_json_mode()
    {
        // Empty teamId fails the input validation guard → invalid_team.
        var output = TeamTools.RegisterTeam(teamId: "", name: "x", format: "json");
        AssertErrorEnvelope(output, "invalid_team", expectedTool: "koshi_register_team");
    }

    [Fact]
    public void RegisterTeam_invalid_format_returns_error_envelope()
    {
        var output = TeamTools.RegisterTeam(
            teamId: "phase2b-team-fmt", name: "x", format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_register_team");
    }

    // ═════════════════ Critique-driven follow-ups (rubber-duck) ═════════════════
    // The block below covers the gaps the rubber-duck pass surfaced before this
    // PR was committed: invalid numeric config on RegisterTeam, nonexistent
    // import source path, the Resolve() fallback contract for non-JSON-shaped
    // sentinels, and the Upsert race / history-preservation contract.

    [Fact]
    public void RegisterTeam_zero_token_budget_returns_invalid_team_envelope()
    {
        var output = TeamTools.RegisterTeam(
            teamId: "phase2b-team-budget", name: "x", tokenBudget: 0, format: "json");
        AssertErrorEnvelope(output, "invalid_team", expectedTool: "koshi_register_team");
    }

    [Fact]
    public void RegisterTeam_negative_topk_returns_invalid_team_envelope()
    {
        var output = TeamTools.RegisterTeam(
            teamId: "phase2b-team-topk", name: "x", topK: -1, format: "json");
        AssertErrorEnvelope(output, "invalid_team", expectedTool: "koshi_register_team");
    }

    [Fact]
    public void RegisterTeam_quality_target_above_one_returns_invalid_team_envelope()
    {
        var output = TeamTools.RegisterTeam(
            teamId: "phase2b-team-qt", name: "x", qualityTarget: 1.5f, format: "json");
        AssertErrorEnvelope(output, "invalid_team", expectedTool: "koshi_register_team");
    }

    [Fact]
    public void RegisterTeam_quality_target_below_zero_returns_invalid_team_envelope()
    {
        var output = TeamTools.RegisterTeam(
            teamId: "phase2b-team-qtn", name: "x", qualityTarget: -0.1f, format: "json");
        AssertErrorEnvelope(output, "invalid_team", expectedTool: "koshi_register_team");
    }

    [Fact]
    public void ImportFromVault_nonexistent_path_returns_vault_open_failed_in_json_mode()
    {
        // Critical: VaultBackend's ctor calls Layout.EnsureDirs which would
        // CREATE the typo'd directory tree and then report a successful empty
        // import. The Directory.Exists guard short-circuits before construction.
        var ghostPath = Path.Join(Path.GetTempPath(), "koshi-phase2b-ghost-" + Guid.NewGuid().ToString("N")[..10]);
        Assert.False(Directory.Exists(ghostPath));

        var output = MemoryTools.ImportFromVault(vaultPath: ghostPath, format: "json");

        AssertErrorEnvelope(output, "vault_open_failed", expectedTool: "koshi_memory_import_from_vault");
        // Ensure we did NOT silently create the directory as a side-effect.
        Assert.False(Directory.Exists(ghostPath),
            "ImportFromVault must not auto-create the source directory — that would mask typos.");
    }

    [Fact]
    public void Resolve_unknown_non_json_sentinel_falls_back_to_text_mode()
    {
        // OutputFormatting.Resolve only returns JSON for unknown formats when
        // the sentinel contains "json" (so error envelopes are renderable).
        // For all other unknown sentinels (yaml/xml/csv/...) the tool replies
        // with a plain-text error. Lock that contract in via the Forget tool.
        var output = MemoryTools.Forget(subject: _scratchSubject, format: "yaml");
        Assert.StartsWith("❌", output);
        Assert.DoesNotContain("\"schema_version\"", output);
        Assert.DoesNotContain("\"ok\":", output);
    }

    [Fact]
    public void RegisterTeam_repeated_call_preserves_accumulated_quality_history()
    {
        // The Upsert contract: re-registering a team REPLACES the profile
        // (so config updates take effect) but PRESERVES any accumulated
        // score / feedback / metrics history. A user updating their token
        // budget mid-project must not lose their last week of dashboards.
        var teamId = "phase2b-upsert-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            var created = TeamTools.RegisterTeam(teamId: teamId, name: "v1", tokenBudget: 4096, format: "json");
            var createdData = AssertOkEnvelope(created, expectedTool: "koshi_register_team");
            Assert.Equal("created", createdData.GetProperty("operation").GetString());

            // Record a turn so the team accumulates history we can later
            // verify survives re-registration.
            TeamTools.ScoreTurn(
                teamId: teamId,
                retrievedChunks: 3,
                memoriesRecalled: 1,
                budgetUtilization: 0.5f,
                cacheRatio: 0.4f,
                latencyMs: 250);

            var dashboardBefore = TeamTools.Dashboard(teamId: teamId, format: "json");
            var dashBeforeData = AssertOkEnvelope(dashboardBefore, expectedTool: "koshi_team_dashboard");
            var turnsBefore = dashBeforeData.GetProperty("total_turns").GetInt32();
            Assert.True(turnsBefore >= 1, "scored turn should be observable in dashboard before re-register");

            // Re-register with a different budget — Upsert path.
            var updated = TeamTools.RegisterTeam(teamId: teamId, name: "v2", tokenBudget: 8192, format: "json");
            var updatedData = AssertOkEnvelope(updated, expectedTool: "koshi_register_team");
            Assert.Equal("updated", updatedData.GetProperty("operation").GetString());
            Assert.Equal(8192, updatedData.GetProperty("context_budget_tokens").GetInt32());

            // History must survive.
            var dashboardAfter = TeamTools.Dashboard(teamId: teamId, format: "json");
            var dashAfterData = AssertOkEnvelope(dashboardAfter, expectedTool: "koshi_team_dashboard");
            var turnsAfter = dashAfterData.GetProperty("total_turns").GetInt32();
            Assert.Equal(turnsBefore, turnsAfter);
        }
        finally
        {
            // No public TeamRegistry.Remove on the tools surface; the registry
            // is process-scoped + persists nothing in tests when the
            // KOSHI_TEAM_REGISTRY env var is unset, so leaking a unique
            // teamId is harmless across the rest of this test class.
        }
    }
}
