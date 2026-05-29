using System.Text.Json;
using Koshi.Mcp.Internal;
using Koshi.Mcp.Tools;
using static Koshi.Core.Tests.StructuredOutputAssertions;

namespace Koshi.Core.Tests;

/// <summary>
/// Per-tool tests for the structured-output (JSON envelope) mode introduced
/// by #66 Phase 1. Covers the 5 tools that ship with the <c>format</c>
/// parameter: <c>koshi_search</c>, <c>koshi_recall</c>,
/// <c>koshi_compile_context</c>, <c>koshi_health</c>, <c>koshi_score_turn</c>.
///
/// Each tool is exercised across three axes:
///   1. Text-mode default is unchanged (no JSON braces leak in).
///   2. <c>format="json"</c> produces a valid envelope with the
///      tool-specific data shape.
///   3. <c>format</c> validation works: case-insensitive accept,
///      unknown value yields an <c>invalid_format</c> error envelope
///      (or a plain "❌" prefix in text-coerced fallback).
/// </summary>
[Collection("RetrievalTools-static")]
public sealed class StructuredOutputToolTests : IDisposable
{
    private const string CorpusName = "structured-output-test";

    public void Dispose()
    {
        RetrievalTools.ClearIndex(CorpusName);
        GC.SuppressFinalize(this);
    }

    // ───────────────────────── koshi_search ────────────────────────────

    [Fact]
    public void Search_text_mode_default_returns_human_readable_output()
    {
        RetrievalTools.Index(
            """[{"content":"apple banana cherry","source":"a.md","type":"document"}]""",
            corpus: CorpusName);

        var output = RetrievalTools.Search("apple", topK: 3, corpus: CorpusName);

        Assert.Contains("a.md", output);
        Assert.Contains($"corpus={CorpusName}", output);
        Assert.DoesNotContain("\"schema_version\"", output);
        Assert.DoesNotContain("\"ok\":true", output);
    }

    [Fact]
    public void Search_json_mode_returns_well_formed_envelope_with_hits()
    {
        RetrievalTools.Index(
            """[{"content":"apple banana cherry","source":"a.md","type":"document"}]""",
            corpus: CorpusName);

        var output = RetrievalTools.Search("apple", topK: 3, corpus: CorpusName, format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_search");

        Assert.Equal("apple", data.GetProperty("query").GetString());
        Assert.Equal(CorpusName, data.GetProperty("corpus").GetString());
        Assert.True(data.GetProperty("total").GetInt32() >= 1);

        var results = data.GetProperty("results");
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        Assert.True(results.GetArrayLength() >= 1);

        var first = results[0];
        Assert.Equal(1, first.GetProperty("rank").GetInt32());
        Assert.True(first.GetProperty("score").GetDouble() > 0);
        Assert.Equal("a.md", first.GetProperty("source").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("chunk_id").GetString()));
        Assert.Contains("apple", first.GetProperty("content").GetString());
    }

    [Fact]
    public void Search_json_mode_is_case_insensitive_for_format_value()
    {
        RetrievalTools.Index(
            """[{"content":"zebra yam xylophone","source":"b.md","type":"document"}]""",
            corpus: CorpusName);

        var upper = RetrievalTools.Search("zebra", corpus: CorpusName, format: "JSON");
        var lower = RetrievalTools.Search("zebra", corpus: CorpusName, format: "json");
        AssertOkEnvelope(upper);
        AssertOkEnvelope(lower);
    }

    [Fact]
    public void Search_json_mode_unknown_corpus_yields_error_envelope()
    {
        var output = RetrievalTools.Search(
            "anything", corpus: "no-such-corpus-12345", format: "json");
        AssertErrorEnvelope(output, OutputErrorCodes.UnknownCorpus, expectedTool: "koshi_search");
    }

    [Fact]
    public void Search_json_mode_empty_query_yields_error_envelope()
    {
        var output = RetrievalTools.Search("   ", corpus: CorpusName, format: "json");
        AssertErrorEnvelope(output, OutputErrorCodes.EmptyQuery);
    }

    [Fact]
    public void Search_invalid_format_value_yields_invalid_format_error()
    {
        // 'xml' contains no 'json' substring → text-mode fallback with ❌ prefix.
        var texty = RetrievalTools.Search("apple", corpus: CorpusName, format: "xml");
        Assert.StartsWith("❌", texty);
        Assert.Contains("Unknown format 'xml'", texty);

        // Anything containing 'json' (e.g. media type) coerces to a JSON error
        // envelope so a programmatic caller still gets a parseable response.
        var jsony = RetrievalTools.Search("apple", corpus: CorpusName, format: "application/json");
        AssertErrorEnvelope(jsony, OutputErrorCodes.InvalidFormat);
    }

    // ───────────────────────── koshi_recall ────────────────────────────

    [Fact]
    public void Recall_text_mode_default_is_unchanged_when_store_empty()
    {
        var output = MemoryTools.Recall("anything");
        Assert.StartsWith("❌", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void Recall_json_mode_empty_store_yields_ok_envelope_with_zero_total()
    {
        var output = MemoryTools.Recall("anything", format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_recall");
        Assert.Equal(0, data.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Array, data.GetProperty("memories").ValueKind);
        Assert.Equal(0, data.GetProperty("memories").GetArrayLength());
    }

    [Fact]
    public void Recall_json_mode_empty_query_yields_error_envelope()
    {
        var output = MemoryTools.Recall("   ", format: "json");
        AssertErrorEnvelope(output, OutputErrorCodes.EmptyQuery, expectedTool: "koshi_recall");
    }

    [Fact]
    public void Recall_invalid_format_value_yields_invalid_format_error()
    {
        var texty = MemoryTools.Recall("anything", format: "yaml");
        Assert.StartsWith("❌", texty);
        Assert.Contains("Unknown format 'yaml'", texty);
    }

    // ─────────────────────── koshi_compile_context ─────────────────────

    [Fact]
    public void Compile_text_mode_default_is_unchanged()
    {
        var output = ContextTools.CompileContext(
            systemPrompt: "You are a helpful assistant.",
            userQuery: "What is the capital of France?");

        // Text mode renders a header block — not the JSON envelope shape.
        Assert.DoesNotContain("\"schema_version\"", output);
        Assert.DoesNotContain("\"ok\":true", output);
    }

    [Fact]
    public void Compile_json_mode_returns_envelope_with_metrics_and_sections()
    {
        var output = ContextTools.CompileContext(
            systemPrompt: "You are a helpful assistant.",
            userQuery: "What is the capital of France?",
            format: "json");

        var data = AssertOkEnvelope(output, expectedTool: "koshi_compile_context");

        var metrics = data.GetProperty("metrics");
        Assert.True(metrics.GetProperty("total_tokens_used").GetInt32() >= 0);
        Assert.True(metrics.GetProperty("token_budget_available").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(metrics.GetProperty("strategy").GetString()));

        var sections = data.GetProperty("sections");
        Assert.Equal(JsonValueKind.Array, sections.ValueKind);
        Assert.True(sections.GetArrayLength() >= 1);

        // Every section carries the canonical fields (role/id/token_count/content).
        var first = sections[0];
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("role").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("id").GetString()));
        Assert.True(first.GetProperty("token_count").GetInt32() >= 0);
        Assert.NotNull(first.GetProperty("content").GetString());
    }

    [Fact]
    public void Compile_invalid_format_value_yields_invalid_format_error()
    {
        var jsony = ContextTools.CompileContext(
            systemPrompt: "x", userQuery: "y", format: "application/json");
        AssertErrorEnvelope(jsony, OutputErrorCodes.InvalidFormat);
    }

    // ───────────────────────── koshi_health ────────────────────────────

    [Fact]
    public void Health_text_mode_default_is_unchanged()
    {
        var output = DiagnosticTools.Health();
        Assert.Contains("Koshi Health", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void Health_json_mode_returns_envelope_with_all_sections()
    {
        var output = DiagnosticTools.Health(format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_health");

        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("version").GetString()));
        Assert.True(data.GetProperty("working_set_mb").GetInt64() >= 0);

        // Uptime serialises as an ISO-8601 TimeSpan string (e.g. "00:00:01.2345").
        var uptime = data.GetProperty("uptime").GetString();
        Assert.False(string.IsNullOrWhiteSpace(uptime));

        var retrieval = data.GetProperty("retrieval");
        Assert.Equal(JsonValueKind.False, retrieval.GetProperty("indexed").ValueKind switch
        {
            JsonValueKind.True => JsonValueKind.True,
            _ => JsonValueKind.False,
        });
        Assert.True(retrieval.GetProperty("chunk_count").GetInt32() >= 0);
        var retrievalPersistence = retrieval.GetProperty("persistence");
        Assert.True(retrievalPersistence.TryGetProperty("enabled", out _));
        Assert.True(retrievalPersistence.TryGetProperty("load_attempted", out _));
        Assert.True(retrievalPersistence.TryGetProperty("file_on_disk", out _));

        var memory = data.GetProperty("memory");
        Assert.True(memory.GetProperty("record_count").GetInt32() >= 0);
        Assert.False(string.IsNullOrWhiteSpace(memory.GetProperty("backend").GetString()));

        var teams = data.GetProperty("teams");
        Assert.True(teams.GetProperty("team_count").GetInt32() >= 0);
        Assert.Equal("json", teams.GetProperty("backend").GetString());

        var config = data.GetProperty("configuration");
        Assert.False(string.IsNullOrWhiteSpace(config.GetProperty("project_root").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(config.GetProperty("tokenizer_model").GetString()));
    }

    [Fact]
    public void Health_invalid_format_value_yields_invalid_format_error()
    {
        var jsony = DiagnosticTools.Health(format: "application/json");
        AssertErrorEnvelope(jsony, OutputErrorCodes.InvalidFormat);
    }

    // ───────────────────────── koshi_score_turn ────────────────────────

    [Fact]
    public void ScoreTurn_text_mode_default_is_unchanged()
    {
        var output = TeamTools.ScoreTurn("unknown-team-xyz", retrievedChunks: 3);
        Assert.Contains("Quality Score", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void ScoreTurn_json_mode_returns_envelope_when_team_not_registered()
    {
        var output = TeamTools.ScoreTurn(
            teamId: "unknown-team-xyz",
            retrievedChunks: 3,
            memoriesRecalled: 2,
            budgetUtilization: 0.5f,
            cacheRatio: 0.2f,
            latencyMs: 1000,
            userRating: 4,
            issues: "verbose,slow",
            format: "json");

        var data = AssertOkEnvelope(output, expectedTool: "koshi_score_turn");

        Assert.Equal("unknown-team-xyz", data.GetProperty("team_id").GetString());
        Assert.False(data.GetProperty("team_registered").GetBoolean());
        var composite = data.GetProperty("composite").GetDouble();
        Assert.InRange(composite, 0.0, 1.0);
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("grade").GetString()));

        var breakdown = data.GetProperty("breakdown");
        Assert.InRange(breakdown.GetProperty("retrieval").GetDouble(), 0.0, 1.0);
        Assert.InRange(breakdown.GetProperty("efficiency").GetDouble(), 0.0, 1.0);
        Assert.InRange(breakdown.GetProperty("cache").GetDouble(), 0.0, 1.0);
        Assert.InRange(breakdown.GetProperty("latency").GetDouble(), 0.0, 1.0);
        Assert.InRange(breakdown.GetProperty("user").GetDouble(), 0.0, 1.0);

        var target = data.GetProperty("target");
        // Unregistered team → target is null/null.
        Assert.Equal(JsonValueKind.Null, target.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("met").ValueKind);

        var issues = data.GetProperty("issues_parsed");
        Assert.Equal(JsonValueKind.Array, issues.ValueKind);
        // Issues are parsed via FeedbackIssue flags so we should see the two we set.
        var issueList = new List<string?>();
        foreach (var e in issues.EnumerateArray()) issueList.Add(e.GetString());
        Assert.Contains("verbose", issueList);
        Assert.Contains("slow", issueList);
    }

    [Fact]
    public void ScoreTurn_invalid_format_value_yields_invalid_format_error()
    {
        var jsony = TeamTools.ScoreTurn(
            teamId: "any", format: "application/json");
        AssertErrorEnvelope(jsony, OutputErrorCodes.InvalidFormat);
    }
}
