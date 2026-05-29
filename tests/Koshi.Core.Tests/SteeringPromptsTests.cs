using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Koshi.Mcp.Prompts;
using ModelContextProtocol.Server;

namespace Koshi.Core.Tests;

/// <summary>
/// Locks down the 4 MCP prompts that ship cross-client steering (issue
/// #77 Layer 2). The smoke harness verifies the JSON-RPC surface; these
/// tests pin the metadata (names + descriptions) so a typo or accidental
/// rename is caught at unit-test time without needing the full transport.
/// </summary>
public sealed class SteeringPromptsTests
{
    private static readonly string[] ExpectedPromptNames =
    [
        "koshi/capture-turn-guide",
        "koshi/recall-before-answer",
        "koshi/context-pack-discipline",
        "koshi/score-every-turn",
    ];

    private static MethodInfo[] PromptMethods() =>
        typeof(SteeringPrompts)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() is not null)
            .ToArray();

    [Fact]
    public void Class_is_decorated_with_McpServerPromptType()
    {
        var attr = typeof(SteeringPrompts).GetCustomAttribute<McpServerPromptTypeAttribute>();
        Assert.NotNull(attr);
    }

    [Fact]
    public void Exposes_exactly_four_prompts()
    {
        Assert.Equal(4, PromptMethods().Length);
    }

    [Fact]
    public void Prompt_names_match_expected_slugs()
    {
        var actual = PromptMethods()
            .Select(m => m.GetCustomAttribute<McpServerPromptAttribute>()!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var expected = ExpectedPromptNames
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Every_prompt_has_a_non_empty_description()
    {
        foreach (var m in PromptMethods())
        {
            var desc = m.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrWhiteSpace(desc),
                $"Prompt '{m.Name}' is missing a [Description(...)] attribute.");
            Assert.True(desc!.Length >= 30,
                $"Prompt '{m.Name}' description is too short ({desc.Length} chars).");
        }
    }

    [Fact]
    public void Every_prompt_body_is_substantive()
    {
        foreach (var m in PromptMethods())
        {
            var result = m.Invoke(null, null) as string;
            Assert.False(string.IsNullOrWhiteSpace(result),
                $"Prompt '{m.Name}' returned a null/empty body.");
            Assert.True(result!.Length >= 100,
                $"Prompt '{m.Name}' body is too short ({result.Length} chars) — agent steering needs real content.");
        }
    }

    [Theory]
    [InlineData("koshi/capture-turn-guide", "koshi_capture_turn")]
    [InlineData("koshi/recall-before-answer", "koshi_recall")]
    [InlineData("koshi/context-pack-discipline", "koshi_compile_context")]
    [InlineData("koshi/score-every-turn", "koshi_score_turn")]
    public void Each_prompt_body_references_its_primary_tool(string promptName, string expectedToolMention)
    {
        var body = (string)BodyFor(promptName)!;
        Assert.Contains(expectedToolMention, body, StringComparison.Ordinal);
    }

    // Anti-drift: the example tool calls in prompt bodies must use the
    // ACTUAL parameter names accepted by the MCP tool surface. If a future
    // refactor renames a parameter and forgets the prompt, the agent
    // would otherwise be silently told to send the wrong arguments.
    [Fact]
    public void Context_pack_discipline_example_uses_real_parameter_names()
    {
        var body = (string)BodyFor("koshi/context-pack-discipline")!;

        // Must mention the real parameter names of koshi_compile_context
        // and koshi_budget_plan.
        Assert.Contains("systemPrompt", body, StringComparison.Ordinal);
        Assert.Contains("userQuery", body, StringComparison.Ordinal);
        Assert.Contains("retrievedContent", body, StringComparison.Ordinal);
        Assert.Contains("tokenBudget", body, StringComparison.Ordinal);
        Assert.Contains("totalBudget", body, StringComparison.Ordinal);

        // Must NOT use the stale snake_case aliases that an earlier draft used.
        Assert.DoesNotContain("total_budget", body, StringComparison.Ordinal);
        Assert.DoesNotContain("retrieved_chunks", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_every_turn_directs_at_list_teams_not_dashboard_for_discovery()
    {
        var body = (string)BodyFor("koshi/score-every-turn")!;

        // koshi_team_dashboard REQUIRES a teamId — wrong tool for
        // "is any team registered?". koshi_list_teams is parameterless.
        Assert.Contains("koshi_list_teams", body, StringComparison.Ordinal);

        // The WHY section legitimately mentions koshi_team_dashboard as
        // a downstream consumer of score data; what must NOT appear is
        // using it as the discovery tool.
        Assert.DoesNotContain("check via `koshi_team_dashboard`", body, StringComparison.Ordinal);
        Assert.DoesNotContain("check via koshi_team_dashboard", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_every_turn_documents_all_score_turn_parameters()
    {
        var body = (string)BodyFor("koshi/score-every-turn")!;

        foreach (var param in new[]
                 {
                     "teamId",
                     "retrievedChunks",
                     "memoriesRecalled",
                     "budgetUtilization",
                     "cacheRatio",
                     "latencyMs",
                     "userRating",
                     "issues",
                     "tokensUsed",
                 })
        {
            Assert.Contains(param, body, StringComparison.Ordinal);
        }
    }

    private static string? BodyFor(string promptName)
    {
        var m = PromptMethods().Single(mi =>
            mi.GetCustomAttribute<McpServerPromptAttribute>()!.Name == promptName);
        return m.Invoke(null, null) as string;
    }
}
