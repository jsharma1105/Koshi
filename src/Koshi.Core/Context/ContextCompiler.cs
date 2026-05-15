namespace Koshi.Core.Context;

using System.Diagnostics;
using Koshi.Core.Models;
using Koshi.Core.Tokenization;

/// <summary>
/// Compiles a context window from multiple sources (retrieval, memory, history)
/// into an optimally packed, strategically positioned context for an LLM.
/// 
/// Key responsibilities:
/// 1. Token budgeting — allocate tokens across roles
/// 2. Greedy packing — fill by priority (knapsack-style)
/// 3. Strategic positioning — "Lost in the Middle" aware ordering
/// 4. Cache optimization — stable prefix for prompt caching
/// 5. Compression triggers — summarize when budget is tight
/// </summary>
public sealed class ContextCompiler
{
    private readonly TokenCounter _tokenCounter;
    private readonly PositioningStrategy _defaultStrategy;

    public ContextCompiler(
        TokenCounter tokenCounter,
        PositioningStrategy defaultStrategy = PositioningStrategy.CacheOptimized)
    {
        _tokenCounter = tokenCounter;
        _defaultStrategy = defaultStrategy;
    }

    /// <summary>
    /// Compile a context window from the provided inputs.
    /// </summary>
    public CompiledContext Compile(ContextCompileRequest request)
    {
        var sw = Stopwatch.StartNew();
        var budget = request.Budget ?? ContextBudget.Default();
        var budgetManager = new BudgetManager(budget, _tokenCounter);
        var strategy = request.Strategy ?? _defaultStrategy;

        // Phase 1: Place mandatory sections (system prompt, user query)
        var sections = new List<ContextSection>();
        int dropped = 0;
        int compressed = 0;

        // System prompt — always first, always included
        if (request.SystemPrompt is not null)
        {
            var section = CreateSection("system", ContextRole.SystemPrompt,
                request.SystemPrompt, isCacheable: true, priority: 10f);
            budgetManager.ForceReserve(ContextRole.SystemPrompt, section.TokenCount);
            sections.Add(section);
        }

        // Team context — cacheable, high priority
        if (request.TeamContext is not null)
        {
            var section = CreateSection("team", ContextRole.TeamContext,
                request.TeamContext, isCacheable: true, priority: 9f);
            if (budgetManager.TryReserve(ContextRole.TeamContext, section.TokenCount))
                sections.Add(section);
            else
                dropped++;
        }

        // User query — always last, always included
        var querySection = CreateSection("query", ContextRole.UserQuery,
            request.UserQuery, isCacheable: false, priority: 10f);
        budgetManager.ForceReserve(ContextRole.UserQuery, querySection.TokenCount);

        // Rebalance after mandatory sections placed
        budgetManager.Rebalance();

        // Phase 2: Score and rank dynamic content
        var candidates = new List<(ContextSection Section, float Score)>();

        // Retrieved context chunks
        foreach (var result in request.RetrievedChunks)
        {
            var section = new ContextSection
            {
                Id = $"retrieval-{result.Chunk.Id}",
                Role = ContextRole.RetrievedContext,
                Content = FormatRetrievalChunk(result),
                TokenCount = _tokenCounter.CountTokens(result.Chunk.Content) + 10, // +10 for formatting
                Priority = result.Score,
                IsCacheable = false,
                SourceId = result.Chunk.Metadata.Source,
                Metadata = new ContextSectionMetadata(
                    RelevanceScore: result.Score,
                    DocumentType: result.Chunk.Metadata.DocumentType,
                    Subject: result.Chunk.Metadata.Source)
            };
            candidates.Add((section, result.Score));
        }

        // Memory sections
        foreach (var memory in request.Memories)
        {
            var content = FormatMemory(memory);
            var section = new ContextSection
            {
                Id = $"memory-{memory.Chunk.Id}",
                Role = ContextRole.Memory,
                Content = content,
                TokenCount = _tokenCounter.CountTokens(content),
                Priority = memory.Score * 0.9f, // Slightly lower than direct retrieval
                IsCacheable = false,
                SourceId = memory.Chunk.Id,
                Metadata = new ContextSectionMetadata(
                    RelevanceScore: memory.Score,
                    DecayScore: float.TryParse(
                        memory.Chunk.Metadata.Tags?.GetValueOrDefault("decay", "0"), out var d) ? d : 0f,
                    Subject: memory.Chunk.Metadata.Tags?.GetValueOrDefault("subject"))
            };
            candidates.Add((section, memory.Score * 0.9f));
        }

        // Conversation history (treat as single block or split by turn)
        if (request.ConversationHistory.Count > 0)
        {
            var historyContent = FormatConversationHistory(request.ConversationHistory);
            var section = CreateSection("history", ContextRole.ConversationHistory,
                historyContent, isCacheable: false, priority: 5f);
            candidates.Add((section, 5f));
        }

        // Phase 3: Greedy knapsack packing by score
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        foreach (var (section, _) in candidates)
        {
            if (budgetManager.TryReserve(section.Role, section.TokenCount))
                sections.Add(section);
            else
                dropped++;
        }

        // Phase 4: Position sections strategically
        var orderedSections = ApplyPositioning(sections, querySection, strategy);

        sw.Stop();

        // Build metrics
        var tokensPerRole = orderedSections
            .GroupBy(s => s.Role)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.TokenCount));

        int totalTokens = orderedSections.Sum(s => s.TokenCount);
        int cacheableTokens = orderedSections.Where(s => s.IsCacheable).Sum(s => s.TokenCount);

        var metrics = new ContextCompilationMetrics
        {
            TotalTokensUsed = totalTokens,
            TokenBudgetAvailable = budget.AvailableBudget,
            CacheableTokens = cacheableTokens,
            SectionsIncluded = orderedSections.Count,
            SectionsDropped = dropped,
            SectionsCompressed = compressed,
            TokensPerRole = tokensPerRole,
            CompilationTime = sw.Elapsed,
            StrategyUsed = strategy,
        };

        return new CompiledContext
        {
            Sections = orderedSections,
            Budget = budget,
            Metrics = metrics,
        };
    }

    /// <summary>
    /// Apply positioning strategy to reorder sections for optimal attention.
    /// </summary>
    private List<ContextSection> ApplyPositioning(
        List<ContextSection> sections,
        ContextSection querySection,
        PositioningStrategy strategy)
    {
        // Separate by role groups
        var cacheable = sections.Where(s => s.IsCacheable).OrderByDescending(s => s.Priority).ToList();
        var dynamic = sections.Where(s => !s.IsCacheable).ToList();

        return strategy switch
        {
            PositioningStrategy.CacheOptimized => PositionCacheOptimized(cacheable, dynamic, querySection),
            PositioningStrategy.PrimacyRecency => PositionPrimacyRecency(cacheable, dynamic, querySection),
            PositioningStrategy.RelevanceDescending => PositionRelevanceDescending(cacheable, dynamic, querySection),
            PositioningStrategy.Chronological => PositionChronological(cacheable, dynamic, querySection),
            _ => PositionCacheOptimized(cacheable, dynamic, querySection),
        };
    }

    /// <summary>
    /// Cache-optimized: Stable prefix (cacheable) → Dynamic content → Query
    /// Within dynamic: highest relevance at edges (primacy-recency).
    /// </summary>
    private static List<ContextSection> PositionCacheOptimized(
        List<ContextSection> cacheable,
        List<ContextSection> dynamic,
        ContextSection query)
    {
        var result = new List<ContextSection>();

        // Stable prefix (hits prompt cache)
        result.AddRange(cacheable);

        // Dynamic content with primacy-recency within its block
        if (dynamic.Count > 0)
        {
            var sorted = dynamic.OrderByDescending(s => s.Priority).ToList();
            var positioned = ApplyPrimacyRecency(sorted);
            result.AddRange(positioned);
        }

        // Query always last
        result.Add(query);
        return result;
    }

    /// <summary>
    /// Pure primacy-recency: Best at start and end, weakest in middle.
    /// Research shows 20-40% accuracy drop for middle-positioned content.
    /// </summary>
    private static List<ContextSection> PositionPrimacyRecency(
        List<ContextSection> cacheable,
        List<ContextSection> dynamic,
        ContextSection query)
    {
        var result = new List<ContextSection>();
        result.AddRange(cacheable);

        var all = dynamic.OrderByDescending(s => s.Priority).ToList();
        result.AddRange(ApplyPrimacyRecency(all));

        result.Add(query);
        return result;
    }

    /// <summary>
    /// Simple descending relevance order.
    /// </summary>
    private static List<ContextSection> PositionRelevanceDescending(
        List<ContextSection> cacheable,
        List<ContextSection> dynamic,
        ContextSection query)
    {
        var result = new List<ContextSection>();
        result.AddRange(cacheable);
        result.AddRange(dynamic.OrderByDescending(s => s.Priority));
        result.Add(query);
        return result;
    }

    /// <summary>
    /// Chronological (for conversation-heavy contexts).
    /// </summary>
    private static List<ContextSection> PositionChronological(
        List<ContextSection> cacheable,
        List<ContextSection> dynamic,
        ContextSection query)
    {
        var result = new List<ContextSection>();
        result.AddRange(cacheable);
        result.AddRange(dynamic.OrderBy(s => s.Metadata?.CreatedAt ?? DateTimeOffset.MaxValue));
        result.Add(query);
        return result;
    }

    /// <summary>
    /// Interleave items so highest-priority items are at positions 1 and N,
    /// second-highest at position 2 and N-1, etc. (sandwich pattern).
    /// </summary>
    private static List<ContextSection> ApplyPrimacyRecency(List<ContextSection> sorted)
    {
        if (sorted.Count <= 2) return sorted;

        var result = new ContextSection[sorted.Count];
        int left = 0, right = sorted.Count - 1;

        for (int i = 0; i < sorted.Count; i++)
        {
            if (i % 2 == 0)
                result[left++] = sorted[i];
            else
                result[right--] = sorted[i];
        }

        return result.ToList();
    }

    private ContextSection CreateSection(string id, ContextRole role, string content,
        bool isCacheable, float priority) => new()
    {
        Id = id,
        Role = role,
        Content = content,
        TokenCount = _tokenCounter.CountTokens(content),
        Priority = priority,
        IsCacheable = isCacheable,
    };

    private static string FormatRetrievalChunk(SearchResult result)
    {
        var source = result.Chunk.Metadata.Source;
        return $"[Source: {source}]\n{result.Chunk.Content}";
    }

    private static string FormatMemory(SearchResult memory)
    {
        var subject = memory.Chunk.Metadata.Tags?.GetValueOrDefault("subject", "unknown");
        var tier = memory.Chunk.Metadata.Tags?.GetValueOrDefault("tier", "Hot");
        return $"[Memory: {subject} ({tier})]\n{memory.Chunk.Content}";
    }

    private static string FormatConversationHistory(IReadOnlyList<ConversationTurn> turns)
    {
        return string.Join("\n\n", turns.Select(t =>
            $"[{t.Role}]: {t.Content}"));
    }
}

/// <summary>
/// Request to compile a context window.
/// </summary>
public sealed record ContextCompileRequest
{
    public required string UserQuery { get; init; }
    public string? SystemPrompt { get; init; }
    public string? TeamContext { get; init; }
    public IReadOnlyList<SearchResult> RetrievedChunks { get; init; } = [];
    public IReadOnlyList<SearchResult> Memories { get; init; } = [];
    public IReadOnlyList<ConversationTurn> ConversationHistory { get; init; } = [];
    public ContextBudget? Budget { get; init; }
    public PositioningStrategy? Strategy { get; init; }
}

/// <summary>
/// A single turn in a conversation.
/// </summary>
public sealed record ConversationTurn(string Role, string Content, DateTimeOffset? Timestamp = null);
