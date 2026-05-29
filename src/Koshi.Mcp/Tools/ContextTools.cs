using System.ComponentModel;
using Koshi.Core.Context;
using Koshi.Core.Models;
using Koshi.Core.Tokenization;
using Koshi.Mcp.Internal;
using ModelContextProtocol.Server;
using static Koshi.Mcp.Internal.JsonShapes;

namespace Koshi.Mcp.Tools;

/// <summary>
/// MCP tools for context compilation — budget, position, and pack content for LLM consumption.
/// </summary>
[McpServerToolType]
public sealed class ContextTools
{
    // Token counter is shared with RetrievalTools via TokenCounters.Shared
    // (Koshi.Core.Tokenization) — was a duplicate Lazy<TokenCounter> here
    // before issue #29.

    [McpServerTool(Name = "koshi_compile_context"), Description(
        "WHEN TO CALL: Once you have collected retrieval results AND/OR memories AND need to pack them " +
        "into a token-budgeted prompt window before sending to an LLM. Call AFTER koshi_search and/or " +
        "koshi_recall — never in place of them.\n" +
        "WHAT IT DOES: Positions retrieved chunks, memories, team context, and the user query inside " +
        "tokenBudget using a cache-first strategy by default. Deterministic. No LLM, no network.\n" +
        "WHAT YOU GIVE IT: systemPrompt + userQuery (required); retrievedContent (sections joined with " +
        "'\\n---\\n'); memories (JSON array, or raw koshi_recall output — auto-parsed); teamContext; " +
        "tokenBudget (default 8192); strategy (CacheOptimized|PrimacyRecency|RelevanceDescending|Chronological). " +
        "Pass format=\"json\" for a parseable envelope (#66).")]
    public static string CompileContext(
        [Description("System prompt for the LLM")] string systemPrompt,
        [Description("The user's query")] string userQuery,
        [Description("Retrieved content sections separated by '---' on its own line")] string? retrievedContent = null,
        [Description("Memory/facts to include. Preferred structured form: JSON array of {type,subject,content,confidence}. " +
                     "Also accepts raw koshi_recall output (auto-detected and parsed into one section per entry). " +
                     "Plain text is treated as a single memory verbatim — use the JSON form for multiple distinct memories.")]
        string? memories = null,
        [Description("Team context/conventions to include (stable, cacheable)")] string? teamContext = null,
        [Description("Total token budget (default: 8192)")] int tokenBudget = 8192,
        [Description("Positioning strategy: CacheOptimized, PrimacyRecency, RelevanceDescending, Chronological")]
        string strategy = "CacheOptimized",
        [Description("Output mode: 'text' (default, human-readable) or 'json' (stable structured envelope, issue #66).")]
        string? format = null)
    {
        var fmt = OutputFormatting.Resolve(format, out var fmtErr);
        if (fmtErr is not null)
            return fmt == OutputFormat.Json
                ? OutputFormatting.Error<CompileContextResultData>("koshi_compile_context", OutputErrorCodes.InvalidFormat, fmtErr,
                    KoshiOutputJsonContext.Default.JsonEnvelopeCompileContextResultData)
                : "❌ " + fmtErr;

        if (tokenBudget < 1) tokenBudget = 8192;
        var tokenCounter = TokenCounters.Shared;
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
        foreach (var parsed in MemoryInputParser.Parse(memories))
        {
            string text = FormatMemoryForChunk(parsed);
            var tags = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(parsed.Subject)) tags["subject"] = parsed.Subject!;
            if (parsed.Type is not null) tags["type"] = parsed.Type.Value.ToString();
            var c = new Chunk(
                $"mcp-memory-{memoryResults.Count}",
                text,
                new ChunkMetadata("memory", "memory", 0, text.Length, DateTimeOffset.UtcNow,
                    Tags: tags.Count > 0 ? tags : null))
            {
                TokenCount = tokenCounter.CountTokens(text),
            };
            memoryResults.Add(new SearchResult(c, 0.8f, "memory"));
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

        if (fmt == OutputFormat.Json)
        {
            var sections = new List<CompileSectionData>(result.Sections.Count);
            foreach (var section in result.Sections)
            {
                sections.Add(new CompileSectionData(
                    Role: section.Role.ToString(),
                    Id: section.Id,
                    TokenCount: section.TokenCount,
                    Content: section.Content));
            }
            var metrics = new CompileMetricsData(
                TotalTokensUsed: result.Metrics.TotalTokensUsed,
                TokenBudgetAvailable: result.Metrics.TokenBudgetAvailable,
                BudgetUtilization: result.Metrics.BudgetUtilization,
                SectionsIncluded: result.Metrics.SectionsIncluded,
                SectionsDropped: result.Metrics.SectionsDropped,
                Strategy: posStrategy.ToString(),
                CacheableTokens: result.Metrics.CacheableTokens,
                CacheRatio: result.Metrics.CacheRatio);
            var payload = new CompileContextResultData(metrics, sections);
            return OutputFormatting.Ok("koshi_compile_context", payload,
                KoshiOutputJsonContext.Default.JsonEnvelopeCompileContextResultData);
        }

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

    /// <summary>
    /// Render a <see cref="ParsedMemory"/> as the text that will be packed into
    /// a memory section. For recall-format input the Content already contains
    /// the full header+content+scope block. For JSON-derived memories we add a
    /// compact header so structured metadata isn't lost.
    /// </summary>
    private static string FormatMemoryForChunk(ParsedMemory parsed)
    {
        // Recall-format entries: the block we captured already starts with
        // the "[Type] Subject (score:..., confidence:...%)" header. Use it
        // verbatim so provenance is preserved.
        if (parsed.Content.StartsWith("  [", StringComparison.Ordinal) ||
            parsed.Content.StartsWith("[", StringComparison.Ordinal))
        {
            return parsed.Content;
        }

        // JSON-derived entries: synthesise a header so type/subject/confidence
        // make it into the prompt.
        if (parsed.Type is not null || parsed.Subject is not null || parsed.Confidence is not null)
        {
            var header = new System.Text.StringBuilder();
            header.Append('[');
            header.Append(parsed.Type?.ToString() ?? "Memory");
            header.Append("] ");
            header.Append(parsed.Subject ?? "(unspecified)");
            if (parsed.Confidence is float conf)
            {
                header.Append(" (confidence: ");
                header.Append((conf * 100).ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
                header.Append("%)");
            }
            return $"{header}\n  {parsed.Content}";
        }

        // Plain text: include verbatim.
        return parsed.Content;
    }

    [McpServerTool(Name = "koshi_token_count"), Description(
        "WHEN TO CALL: To check whether a string fits a budget, or to size a prospective chunk before " +
        "sending it to an LLM. Useful inside loops that build prompts.\n" +
        "WHAT IT DOES: Returns token count using the GPT-4 tokenizer plus character count and chars/token " +
        "ratio. Deterministic. No network. Pass format=\"json\" for a parseable envelope (#66).\n" +
        "WHAT YOU GIVE IT: text (the string to measure).")]
    public static string CountTokens(
        [Description("The text to count tokens for")] string text,
        [Description("Output mode: 'text' (default, human-readable) or 'json' (stable structured envelope, issue #66).")]
        string? format = null)
    {
        var fmt = OutputFormatting.Resolve(format, out var fmtErr);
        if (fmtErr is not null)
            return fmt == OutputFormat.Json
                ? OutputFormatting.Error<TokenCountResultData>("koshi_token_count", OutputErrorCodes.InvalidFormat, fmtErr,
                    KoshiOutputJsonContext.Default.JsonEnvelopeTokenCountResultData)
                : "❌ " + fmtErr;

        int count = TokenCounters.Shared.CountTokens(text);

        if (fmt == OutputFormat.Json)
        {
            var charsPerToken = count > 0 ? (double)text.Length / count : 0.0;
            var payload = new TokenCountResultData(
                Tokens: count,
                Characters: text.Length,
                CharsPerToken: charsPerToken);
            return OutputFormatting.Ok("koshi_token_count", payload,
                KoshiOutputJsonContext.Default.JsonEnvelopeTokenCountResultData);
        }

        return $"{count} tokens ({text.Length} characters, ratio: {(float)text.Length / count:F1} chars/token)";
    }

    [McpServerTool(Name = "koshi_budget_plan"), Description(
        "WHEN TO CALL: BEFORE building a prompt window when you want to see how a total budget will " +
        "split across system prompt, team context, retrieval, memory, and history. Skip if you already " +
        "know your allocation.\n" +
        "WHAT IT DOES: Computes fixed costs (system + team), then suggests how to divide the remaining " +
        "budget across retrieval/memory/history. Default split is 50/25/25; pass reserveHistory=false " +
        "for one-shot/batch flows that don't supply conversation history (reclaims the 25% history slice " +
        "into a 67/33 retrieval/memory split). Override entirely with retrievalPct/memoryPct/historyPct. " +
        "Pass format=\"json\" for a parseable envelope (#66).\n" +
        "WHAT YOU GIVE IT: totalBudget (default 8192); systemPrompt / teamContext (optional, sized for " +
        "fixed cost); reserveHistory (default true); retrievalPct + memoryPct + historyPct (optional " +
        "explicit override — must sum to 100).")]
    public static string PlanBudget(
        [Description("Total token budget")] int totalBudget = 8192,
        [Description("System prompt text (to measure its size)")] string? systemPrompt = null,
        [Description("Team context text")] string? teamContext = null,
        [Description("Reserve a slice for conversation history (default true). Pass false for one-shot " +
                     "or batch flows with no history to reclaim that slice into retrieval+memory.")]
        bool reserveHistory = true,
        [Description("Explicit retrieval percentage 0-100 (optional). When set, memoryPct and historyPct " +
                     "must also be set and the three must sum to 100. Overrides reserveHistory.")]
        int? retrievalPct = null,
        [Description("Explicit memory percentage 0-100 (optional, paired with retrievalPct + historyPct).")]
        int? memoryPct = null,
        [Description("Explicit history percentage 0-100 (optional, paired with retrievalPct + memoryPct).")]
        int? historyPct = null,
        [Description("Output mode: 'text' (default, human-readable) or 'json' (stable structured envelope, issue #66).")]
        string? format = null)
    {
        var fmt = OutputFormatting.Resolve(format, out var fmtErr);
        if (fmtErr is not null)
            return fmt == OutputFormat.Json
                ? OutputFormatting.Error<BudgetPlanResultData>("koshi_budget_plan", OutputErrorCodes.InvalidFormat, fmtErr,
                    KoshiOutputJsonContext.Default.JsonEnvelopeBudgetPlanResultData)
                : "❌ " + fmtErr;

        if (totalBudget < 1) totalBudget = 8192;
        var tokenCounter = TokenCounters.Shared;

        int systemTokens = systemPrompt is not null ? tokenCounter.CountTokens(systemPrompt) : 0;
        int teamTokens = teamContext is not null ? tokenCounter.CountTokens(teamContext) : 0;
        int fixedCost = systemTokens + teamTokens;
        int remaining = Math.Max(0, totalBudget - fixedCost);

        int rPct, mPct, hPct;
        string splitNote;
        try
        {
            (rPct, mPct, hPct, splitNote) = ResolveSplit(reserveHistory, retrievalPct, memoryPct, historyPct);
        }
        catch (ArgumentException ex)
        {
            // Bad explicit-split combinations should not crash JSON callers —
            // surface as an envelope so orchestrators can recover gracefully.
            // Text mode preserves the historical contract and rethrows so the
            // MCP framework can map it to a tool-call error.
            if (fmt == OutputFormat.Json)
                return OutputFormatting.Error<BudgetPlanResultData>("koshi_budget_plan", OutputErrorCodes.InvalidSplit,
                    ex.Message, KoshiOutputJsonContext.Default.JsonEnvelopeBudgetPlanResultData);
            throw;
        }

        int retrievalTokens = remaining * rPct / 100;
        int memoryTokens = remaining * mPct / 100;
        int historyTokens = remaining * hPct / 100;

        if (fmt == OutputFormat.Json)
        {
            var payload = new BudgetPlanResultData(
                TotalBudget: totalBudget,
                SystemTokens: systemTokens,
                TeamTokens: teamTokens,
                FixedCost: fixedCost,
                Remaining: remaining,
                Split: new BudgetSplitData(rPct, mPct, hPct),
                Allocation: new BudgetAllocationData(
                    RetrievalTokens: retrievalTokens,
                    MemoryTokens: memoryTokens,
                    HistoryTokens: historyTokens,
                    RetrievalChunksEstimate: retrievalTokens / 512,
                    MemoryItemsEstimate: memoryTokens / 100,
                    HistoryTurnsEstimate: historyTokens / 200),
                CacheSavingsEstimate: (int)Math.Round(fixedCost * 0.5),
                SplitNote: string.IsNullOrEmpty(splitNote) ? null : splitNote);
            return OutputFormatting.Ok("koshi_budget_plan", payload,
                KoshiOutputJsonContext.Default.JsonEnvelopeBudgetPlanResultData);
        }

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
        if (!string.IsNullOrEmpty(splitNote))
        {
            sb.AppendLine($"Suggested allocation ({splitNote}):");
        }
        else
        {
            sb.AppendLine($"Suggested allocation:");
        }
        sb.AppendLine($"  Retrieval ({rPct,3}%): {retrievalTokens,6} tokens (~{retrievalTokens / 512} chunks)");
        sb.AppendLine($"  Memory    ({mPct,3}%): {memoryTokens,6} tokens (~{memoryTokens / 100} memories)");
        if (hPct > 0)
        {
            sb.AppendLine($"  History   ({hPct,3}%): {historyTokens,6} tokens (~{historyTokens / 200} turns)");
        }
        else
        {
            sb.AppendLine($"  History   (  0%):      0 tokens (not reserved)");
        }
        sb.AppendLine();
        sb.AppendLine($"Cache savings: stable prefix of {fixedCost} tokens saves ~{fixedCost * 0.5:F0} tokens/call with prompt caching");

        return sb.ToString();
    }

    /// <summary>
    /// Resolves the retrieval/memory/history percentage split for the budget
    /// plan. Explicit per-role percentages (Option C in #73) take precedence
    /// over the reserveHistory toggle (Option B); if none is supplied we fall
    /// back to the historical 50/25/25 default.
    /// </summary>
    /// <returns>
    /// Tuple of (retrievalPct, memoryPct, historyPct, splitNote) where
    /// splitNote is an empty string for the default split and a short
    /// human-readable explanation otherwise.
    /// </returns>
    internal static (int RetrievalPct, int MemoryPct, int HistoryPct, string Note) ResolveSplit(
        bool reserveHistory, int? retrievalPct, int? memoryPct, int? historyPct)
    {
        bool anyExplicit = retrievalPct.HasValue || memoryPct.HasValue || historyPct.HasValue;
        if (anyExplicit)
        {
            int r = retrievalPct ?? 0;
            int m = memoryPct ?? 0;
            int h = historyPct ?? 0;
            if (r < 0 || m < 0 || h < 0)
            {
                throw new ArgumentException(
                    $"retrievalPct/memoryPct/historyPct must each be >= 0 (got {r}/{m}/{h}).");
            }
            if (r + m + h != 100)
            {
                throw new ArgumentException(
                    $"retrievalPct + memoryPct + historyPct must sum to 100 (got {r}+{m}+{h}={r + m + h}).");
            }
            return (r, m, h, $"explicit override {r}/{m}/{h}");
        }

        if (!reserveHistory)
        {
            // Reclaim the 25% history slice into the 50/25 retrieval+memory
            // base proportionally, yielding ~67/33.
            return (67, 33, 0, "no history reserved, reclaiming 25%");
        }

        return (50, 25, 25, string.Empty);
    }
}
