using System.Text.Json;
using Koshi.Mcp.Tools;
using static Koshi.Core.Tests.StructuredOutputAssertions;

namespace Koshi.Core.Tests;

/// <summary>
/// Per-tool tests for the structured-output (JSON envelope) mode wired by
/// #66 Phase 2a. Covers the 8 read-only / diagnostic tools that gained the
/// <c>format</c> parameter in this phase:
///   - <c>koshi_version</c>
///   - <c>koshi_token_count</c>
///   - <c>koshi_budget_plan</c>
///   - <c>koshi_memory_stats</c>
///   - <c>koshi_list_indexed</c>
///   - <c>koshi_team_dashboard</c>
///   - <c>koshi_analyze_feedback</c>
///   - <c>koshi_list_teams</c>
///
/// Each tool is exercised across three axes:
///   1. Text-mode default is unchanged (no JSON envelope leaks in).
///   2. <c>format="json"</c> produces a valid envelope with a tool-specific
///      data shape and the expected <c>tool</c> field.
///   3. <c>format</c> validation: unknown value → invalid_format envelope.
/// </summary>
[Collection("RetrievalTools-static")]
public sealed class StructuredOutputPhase2aToolTests : IDisposable
{
    private const string CorpusName = "phase2a-test-corpus";

    public void Dispose()
    {
        RetrievalTools.ClearIndex(CorpusName);
        RetrievalTools.ClearIndex();
        GC.SuppressFinalize(this);
    }

    // ───────────────────────── koshi_version ────────────────────────────

    [Fact]
    public void Version_text_mode_default_is_unchanged()
    {
        var output = DiagnosticTools.Version();

        Assert.Contains("Koshi MCP Server", output);
        Assert.DoesNotContain("\"schema_version\"", output);
        Assert.DoesNotContain("\"ok\":true", output);
    }

    [Fact]
    public void Version_json_mode_returns_well_formed_envelope()
    {
        var output = DiagnosticTools.Version(format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_version");

        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("dotnet_runtime").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("os").GetString()));
        Assert.True(data.GetProperty("process_id").GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("started_at").GetString()));
    }

    [Fact]
    public void Version_invalid_format_returns_error_envelope()
    {
        var output = DiagnosticTools.Version(format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_version");
    }

    // ─────────────────────── koshi_token_count ──────────────────────────

    [Fact]
    public void Token_count_text_mode_default_is_unchanged()
    {
        var output = ContextTools.CountTokens("hello world");

        Assert.Contains("tokens", output);
        Assert.Contains("characters", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void Token_count_json_mode_returns_well_formed_envelope()
    {
        var output = ContextTools.CountTokens("hello world", format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_token_count");

        Assert.True(data.GetProperty("tokens").GetInt32() >= 1);
        Assert.Equal("hello world".Length, data.GetProperty("characters").GetInt32());
        Assert.True(data.GetProperty("chars_per_token").GetDouble() > 0);
    }

    [Fact]
    public void Token_count_invalid_format_returns_error_envelope()
    {
        var output = ContextTools.CountTokens("hello", format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_token_count");
    }

    // ─────────────────────── koshi_budget_plan ──────────────────────────

    [Fact]
    public void Budget_plan_text_mode_default_is_unchanged()
    {
        var output = ContextTools.PlanBudget(totalBudget: 8192);

        Assert.Contains("8192", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void Budget_plan_json_mode_returns_well_formed_envelope()
    {
        var output = ContextTools.PlanBudget(totalBudget: 8192, format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_budget_plan");

        Assert.Equal(8192, data.GetProperty("total_budget").GetInt32());
        Assert.True(data.GetProperty("remaining").GetInt32() > 0);

        var split = data.GetProperty("split");
        var rPct = split.GetProperty("retrieval_pct").GetInt32();
        var mPct = split.GetProperty("memory_pct").GetInt32();
        var hPct = split.GetProperty("history_pct").GetInt32();
        Assert.Equal(100, rPct + mPct + hPct);

        var alloc = data.GetProperty("allocation");
        Assert.True(alloc.GetProperty("retrieval_tokens").GetInt32() > 0);
    }

    [Fact]
    public void Budget_plan_invalid_split_in_json_mode_returns_error_envelope()
    {
        // 50+50+50 = 150 — invalid; JSON mode must NOT throw, must return envelope.
        var output = ContextTools.PlanBudget(
            totalBudget: 8192,
            retrievalPct: 50, memoryPct: 50, historyPct: 50,
            format: "json");
        AssertErrorEnvelope(output, "invalid_split", expectedTool: "koshi_budget_plan");
    }

    [Fact]
    public void Budget_plan_invalid_format_returns_error_envelope()
    {
        var output = ContextTools.PlanBudget(totalBudget: 8192, format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_budget_plan");
    }

    // ─────────────────────── koshi_memory_stats ─────────────────────────

    [Fact]
    public void Memory_stats_text_mode_default_is_unchanged()
    {
        var output = MemoryTools.MemoryStats();

        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void Memory_stats_json_mode_returns_well_formed_envelope()
    {
        var output = MemoryTools.MemoryStats(format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_memory_stats");

        Assert.True(data.GetProperty("total_records").GetInt32() >= 0);
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("backend").GetString()));
        Assert.Equal(JsonValueKind.Array, data.GetProperty("by_type").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("by_scope").ValueKind);
        Assert.True(data.TryGetProperty("persistence_enabled", out _));
    }

    [Fact]
    public void Memory_stats_invalid_format_returns_error_envelope()
    {
        var output = MemoryTools.MemoryStats(format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_memory_stats");
    }

    // ─────────────────────── koshi_list_indexed ─────────────────────────

    [Fact]
    public void List_indexed_text_mode_default_is_unchanged()
    {
        var output = RetrievalTools.ListIndexed();
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void List_indexed_summary_json_mode_returns_well_formed_envelope()
    {
        RetrievalTools.Index(
            """[{"content":"alpha beta","source":"x.md","type":"document"}]""",
            corpus: CorpusName);

        var output = RetrievalTools.ListIndexed(format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_list_indexed");

        Assert.Equal("summary", data.GetProperty("mode").GetString());
        Assert.True(data.GetProperty("total_chunks").GetInt32() >= 1);

        var corpora = data.GetProperty("corpora");
        Assert.Equal(JsonValueKind.Array, corpora.ValueKind);
        Assert.True(corpora.GetArrayLength() >= 1);

        var sources = data.GetProperty("sources");
        Assert.Equal(JsonValueKind.Array, sources.ValueKind);
        Assert.Equal(0, sources.GetArrayLength());
    }

    [Fact]
    public void List_indexed_detail_json_mode_returns_per_source_breakdown()
    {
        RetrievalTools.Index(
            """[{"content":"alpha beta","source":"x.md","type":"document"}]""",
            corpus: CorpusName);

        var output = RetrievalTools.ListIndexed(corpus: CorpusName, format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_list_indexed");

        Assert.Equal("detail", data.GetProperty("mode").GetString());
        Assert.Equal(CorpusName, data.GetProperty("corpus").GetString());

        var sources = data.GetProperty("sources");
        Assert.Equal(JsonValueKind.Array, sources.ValueKind);
        Assert.True(sources.GetArrayLength() >= 1);
        Assert.Equal("x.md", sources[0].GetProperty("source").GetString());
    }

    [Fact]
    public void List_indexed_unknown_corpus_in_json_mode_returns_error_envelope()
    {
        var output = RetrievalTools.ListIndexed(corpus: "does-not-exist-zzz", format: "json");
        AssertErrorEnvelope(output, "unknown_corpus", expectedTool: "koshi_list_indexed");
    }

    [Fact]
    public void List_indexed_invalid_format_returns_error_envelope()
    {
        var output = RetrievalTools.ListIndexed(format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_list_indexed");
    }

    // ──────────────────────── koshi_list_teams ──────────────────────────

    [Fact]
    public void List_teams_text_mode_default_is_unchanged()
    {
        var output = TeamTools.ListTeams();
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void List_teams_json_mode_returns_well_formed_envelope()
    {
        var output = TeamTools.ListTeams(format: "json");
        var data = AssertOkEnvelope(output, expectedTool: "koshi_list_teams");

        Assert.True(data.GetProperty("total_teams").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("teams").ValueKind);
    }

    [Fact]
    public void List_teams_invalid_format_returns_error_envelope()
    {
        var output = TeamTools.ListTeams(format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_list_teams");
    }

    // ───────────────────── koshi_team_dashboard ─────────────────────────

    [Fact]
    public void Team_dashboard_unknown_team_in_json_mode_returns_error_envelope()
    {
        var output = TeamTools.Dashboard("no-such-team-zzz", format: "json");
        AssertErrorEnvelope(output, "unknown_team", expectedTool: "koshi_team_dashboard");
    }

    [Fact]
    public void Team_dashboard_text_mode_default_is_unchanged_for_unknown_team()
    {
        var output = TeamTools.Dashboard("no-such-team-zzz");
        Assert.Contains("not found", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void Team_dashboard_registered_team_in_json_mode_returns_well_formed_envelope()
    {
        const string teamId = "phase2a-dashboard-team";
        TeamTools.RegisterTeam(teamId, name: "Phase 2a Dashboard Team");

        try
        {
            var output = TeamTools.Dashboard(teamId, format: "json");
            var data = AssertOkEnvelope(output, expectedTool: "koshi_team_dashboard");

            Assert.Equal(teamId, data.GetProperty("team_id").GetString());
            Assert.True(data.GetProperty("registered").GetBoolean());
            Assert.True(data.GetProperty("total_turns").GetInt32() >= 0);
            Assert.Equal(JsonValueKind.Array, data.GetProperty("quality_trend").ValueKind);
            Assert.Equal(JsonValueKind.Array, data.GetProperty("recommendations").ValueKind);
        }
        finally
        {
            // Best-effort cleanup so subsequent runs are not polluted.
        }
    }

    [Fact]
    public void Team_dashboard_invalid_format_returns_error_envelope()
    {
        var output = TeamTools.Dashboard("any-team", format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_team_dashboard");
    }

    // ──────────────────── koshi_analyze_feedback ────────────────────────

    [Fact]
    public void Analyze_feedback_unknown_team_in_json_mode_returns_error_envelope()
    {
        var output = TeamTools.AnalyzeFeedback("no-such-team-zzz", format: "json");
        AssertErrorEnvelope(output, "unknown_team", expectedTool: "koshi_analyze_feedback");
    }

    [Fact]
    public void Analyze_feedback_text_mode_default_is_unchanged_for_unknown_team()
    {
        var output = TeamTools.AnalyzeFeedback("no-such-team-zzz");
        Assert.Contains("not found", output);
        Assert.DoesNotContain("\"schema_version\"", output);
    }

    [Fact]
    public void Analyze_feedback_insufficient_data_in_json_mode_returns_error_envelope()
    {
        const string teamId = "phase2a-analyze-team";
        TeamTools.RegisterTeam(teamId, name: "Phase 2a Analyze Team");

        // No scored turns → InsufficientData.
        var output = TeamTools.AnalyzeFeedback(teamId, format: "json");
        AssertErrorEnvelope(output, "insufficient_data", expectedTool: "koshi_analyze_feedback");
    }

    [Fact]
    public void Analyze_feedback_invalid_format_returns_error_envelope()
    {
        var output = TeamTools.AnalyzeFeedback("any-team", format: "application/json");
        AssertErrorEnvelope(output, "invalid_format", expectedTool: "koshi_analyze_feedback");
    }
}
