using System.ComponentModel;
using Koshi.Core.Context;
using Koshi.Core.Models;
using Koshi.Core.Tokenization;
using ModelContextProtocol.Server;

namespace Koshi.Mcp.Tools;

/// <summary>
/// MCP tools for context compilation — budget, position, and pack content for LLM consumption.
/// </summary>
[McpServerToolType]
public sealed class ContextTools
{
    private static readonly Lazy<TokenCounter> _tokenCounter = new(() =>
        TokenCounter.CreateAsync("gpt-4").GetAwaiter().GetResult());

    [McpServerTool(Name = "koshi_compile_context"), Description(
        "Compile content into an optimally packed context window. " +
        "Takes retrieval results, system prompt, and optional memory/history " +
        "and returns positioned content within your token budget.")]
    public static string CompileContext(
        [Description("System prompt for the LLM")] string systemPrompt,
        [Description("The user's query")] string userQuery,
        [Description("Retrieved content sections separated by '---' on its own line")] string? retrievedContent = null,
        [Description("Memory/facts to include (newline-separated)")] string? memories = null,
        [Description("Team context/conventions to include (stable, cacheable)")] string? teamContext = null,
        [Description("Total token budget (default: 8192)")] int tokenBudget = 8192,
        [Description("Positioning strategy: CacheOptimized, PrimacyRecency, RelevanceDescending, Chronological")]
        string strategy = "CacheOptimized")
    {
        if (tokenBudget < 1) tokenBudget = 8192;
        var tokenCounter = _tokenCounter.Value;
        var posStrategy = Enum.TryParse<PositioningStrategy>(strategy, true, out var ps)
            ? ps : PositioningStrategy.CacheOptimized;

        var compiler = new ContextCompiler(tokenCounter, posStrategy);

        var retrievedChunks = new List<SearchResult>();
        if (!string.IsNullOrWhiteSpace(retrievedContent))
        {
            var chunks = retrievedContent.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
            float score = 1.0f;
            foreach (var chunk in chunks)
            {
                var c = new Chunk(
                    $"mcp-chunk-{retrievedChunks.Count}",
                    chunk.Trim(),
                    new ChunkMetadata("mcp-input", "document", 0, chunk.Length, DateTimeOffset.UtcNow))
                {
                    TokenCount = tokenCounter.CountTokens(chunk),
                };
                retrievedChunks.Add(new SearchResult(c, score, "mcp"));
                score -= 0.1f;
            }
        }

        var memoryResults = new List<SearchResult>();
        if (!string.IsNullOrWhiteSpace(memories))
        {
            var memLines = memories.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var mem in memLines)
            {
                var c = new Chunk(
                    $"mcp-memory-{memoryResults.Count}",
                    mem.Trim(),
                    new ChunkMetadata("memory", "memory", 0, mem.Length, DateTimeOffset.UtcNow))
                {
                    TokenCount = tokenCounter.CountTokens(mem),
                };
                memoryResults.Add(new SearchResult(c, 0.8f, "memory"));
            }
        }

        var request = new ContextCompileRequest
        {
            UserQuery = userQuery,
            SystemPrompt = systemPrompt,
            TeamContext = teamContext,
            RetrievedChunks = retrievedChunks,
            Memories = memoryResults,
            Budget = ContextBudget.Default(tokenBudget),
            Strategy = posStrategy,
        };

        var result = compiler.Compile(request);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ Compiled Context ═══");
        sb.AppendLine($"Budget: {result.Metrics.TotalTokensUsed}/{result.Metrics.TokenBudgetAvailable} tokens ({result.Metrics.BudgetUtilization:P0} utilization)");
        sb.AppendLine($"Sections: {result.Metrics.SectionsIncluded} included, {result.Metrics.SectionsDropped} dropped");
        sb.AppendLine($"Strategy: {posStrategy}");
        sb.AppendLine($"Cache prefix: {result.Metrics.CacheableTokens} tokens cacheable ({result.Metrics.CacheRatio:P0})");
        sb.AppendLine();

        foreach (var section in result.Sections)
        {
            sb.AppendLine($"─── [{section.Role}] {section.Id} ({section.TokenCount} tokens) ───");
            sb.AppendLine(section.Content.Length > 300 ? section.Content[..300] + "..." : section.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "koshi_token_count"), Description(
        "Count tokens in a piece of text using the GPT-4 tokenizer.")]
    public static string CountTokens(
        [Description("The text to count tokens for")] string text)
    {
        int count = _tokenCounter.Value.CountTokens(text);
        return $"{count} tokens ({text.Length} characters, ratio: {(float)text.Length / count:F1} chars/token)";
    }

    [McpServerTool(Name = "koshi_budget_plan"), Description(
        "Plan a token budget allocation across roles. " +
        "Shows how tokens would be divided between system prompt, retrieval, memory, and history.")]
    public static string PlanBudget(
        [Description("Total token budget")] int totalBudget = 8192,
        [Description("System prompt text (to measure its size)")] string? systemPrompt = null,
        [Description("Team context text")] string? teamContext = null)
    {
        if (totalBudget < 1) totalBudget = 8192;
        var tokenCounter = _tokenCounter.Value;
        var budget = ContextBudget.Default(totalBudget);

        int systemTokens = systemPrompt is not null ? tokenCounter.CountTokens(systemPrompt) : 0;
        int teamTokens = teamContext is not null ? tokenCounter.CountTokens(teamContext) : 0;
        int fixedCost = systemTokens + teamTokens;
        int remaining = totalBudget - fixedCost;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ Budget Plan ({totalBudget} tokens total) ═══\n");
        sb.AppendLine($"Fixed costs:");
        sb.AppendLine($"  System prompt:  {systemTokens,6} tokens");
        sb.AppendLine($"  Team context:   {teamTokens,6} tokens");
        sb.AppendLine($"  ────────────────────────");
        sb.AppendLine($"  Total fixed:    {fixedCost,6} tokens");
        sb.AppendLine();
        sb.AppendLine($"Available for dynamic content: {remaining} tokens");
        sb.AppendLine();
        sb.AppendLine($"Suggested allocation:");
        sb.AppendLine($"  Retrieval (50%): {remaining * 50 / 100,6} tokens (~{remaining * 50 / 100 / 512} chunks)");
        sb.AppendLine($"  Memory (25%):    {remaining * 25 / 100,6} tokens (~{remaining * 25 / 100 / 100} memories)");
        sb.AppendLine($"  History (25%):   {remaining * 25 / 100,6} tokens (~{remaining * 25 / 100 / 200} turns)");
        sb.AppendLine();
        sb.AppendLine($"Cache savings: stable prefix of {fixedCost} tokens saves ~{fixedCost * 0.5:F0} tokens/call with prompt caching");

        return sb.ToString();
    }
}
