using Koshi.Mcp.Tools;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for the configurable history-reserve split in <c>koshi_budget_plan</c>
/// (issue #73). Focuses on <see cref="ContextTools.ResolveSplit"/> for
/// deterministic unit coverage and <see cref="ContextTools.PlanBudget"/> for
/// the rendered output contract.
/// </summary>
public class BudgetPlanTests
{
    [Fact]
    public void ResolveSplit_default_keeps_50_25_25()
    {
        var (r, m, h, note) = ContextTools.ResolveSplit(reserveHistory: true, null, null, null);
        Assert.Equal(50, r);
        Assert.Equal(25, m);
        Assert.Equal(25, h);
        Assert.Equal(string.Empty, note);
    }

    [Fact]
    public void ResolveSplit_no_history_reclaims_to_67_33()
    {
        var (r, m, h, note) = ContextTools.ResolveSplit(reserveHistory: false, null, null, null);
        Assert.Equal(67, r);
        Assert.Equal(33, m);
        Assert.Equal(0, h);
        Assert.Equal(100, r + m + h);
        Assert.Contains("reclaiming", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSplit_explicit_override_takes_precedence_over_reserveHistory()
    {
        // reserveHistory=true should be ignored when explicit pcts are supplied.
        var (r, m, h, note) = ContextTools.ResolveSplit(reserveHistory: true, 70, 20, 10);
        Assert.Equal(70, r);
        Assert.Equal(20, m);
        Assert.Equal(10, h);
        Assert.Contains("override", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSplit_explicit_override_must_sum_to_100()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ContextTools.ResolveSplit(reserveHistory: false, 50, 30, 10));
        Assert.Contains("sum to 100", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSplit_explicit_override_rejects_negatives()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ContextTools.ResolveSplit(reserveHistory: false, 110, -5, -5));
        Assert.Contains(">= 0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSplit_partial_explicit_treats_unset_as_zero()
    {
        var (r, m, h, _) = ContextTools.ResolveSplit(reserveHistory: true, 60, 40, null);
        Assert.Equal(60, r);
        Assert.Equal(40, m);
        Assert.Equal(0, h);
    }

    [Fact]
    public void PlanBudget_default_render_still_shows_50_25_25()
    {
        var output = ContextTools.PlanBudget(totalBudget: 16384);
        Assert.Contains("Retrieval ( 50%):", output, StringComparison.Ordinal);
        Assert.Contains("Memory    ( 25%):", output, StringComparison.Ordinal);
        Assert.Contains("History   ( 25%):", output, StringComparison.Ordinal);
        Assert.DoesNotContain("reclaiming", output, StringComparison.Ordinal);
        Assert.DoesNotContain("not reserved", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanBudget_reserveHistory_false_reclaims_into_retrieval_memory()
    {
        var output = ContextTools.PlanBudget(totalBudget: 16384, reserveHistory: false);
        Assert.Contains("Retrieval ( 67%):", output, StringComparison.Ordinal);
        Assert.Contains("Memory    ( 33%):", output, StringComparison.Ordinal);
        Assert.Contains("History   (  0%):      0 tokens (not reserved)", output, StringComparison.Ordinal);
        Assert.Contains("no history reserved, reclaiming 25%", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanBudget_explicit_split_renders_custom_percentages()
    {
        var output = ContextTools.PlanBudget(
            totalBudget: 10000,
            retrievalPct: 70,
            memoryPct: 30,
            historyPct: 0);
        Assert.Contains("Retrieval ( 70%):", output, StringComparison.Ordinal);
        Assert.Contains("Memory    ( 30%):", output, StringComparison.Ordinal);
        Assert.Contains("explicit override 70/30/0", output, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanBudget_invalid_split_surfaces_argument_exception()
    {
        // The MCP tool layer throws — the framework will turn this into a
        // tool-call error visible to the caller. Important: we want the
        // exception, not a silently-corrupted plan.
        Assert.Throws<ArgumentException>(() => ContextTools.PlanBudget(
            totalBudget: 8192,
            retrievalPct: 50,
            memoryPct: 50,
            historyPct: 50));
    }

    [Fact]
    public void PlanBudget_no_history_split_token_math_is_consistent()
    {
        // With reserveHistory=false and a 16334-token dynamic budget (16384 - 50 fixed),
        // 67% retrieval = 10943, 33% memory = 5390. They should not overflow the
        // dynamic remainder.
        const int total = 16384;
        var output = ContextTools.PlanBudget(
            totalBudget: total,
            systemPrompt: "be terse",
            reserveHistory: false);

        // Pull the retrieval & memory numbers out of the rendered output and
        // assert they don't sum to more than the dynamic budget.
        int retrieval = ExtractTokens(output, "Retrieval");
        int memory = ExtractTokens(output, "Memory");
        Assert.True(retrieval > 0);
        Assert.True(memory > 0);
        Assert.True(retrieval + memory <= total,
            $"retrieval + memory ({retrieval + memory}) must not exceed totalBudget ({total})");
    }

    private static int ExtractTokens(string output, string sectionLabel)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            int idx = line.IndexOf(sectionLabel, StringComparison.Ordinal);
            if (idx < 0) continue;
            // Looking for the second number on the line, the one before "tokens".
            int tokensIdx = line.IndexOf("tokens", StringComparison.Ordinal);
            if (tokensIdx < 0) continue;
            var prefix = line[..tokensIdx].TrimEnd();
            int spaceIdx = prefix.LastIndexOf(' ');
            if (spaceIdx < 0) continue;
            var numStr = prefix[(spaceIdx + 1)..];
            if (int.TryParse(numStr, out var n)) return n;
        }
        return -1;
    }
}
