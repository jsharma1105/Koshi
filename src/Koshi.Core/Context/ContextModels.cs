namespace Koshi.Core.Context;

/// <summary>
/// A section of the compiled context window. Each section has a role,
/// priority, and token allocation.
/// </summary>
public sealed record ContextSection
{
    public required string Id { get; init; }
    public required ContextRole Role { get; init; }
    public required string Content { get; init; }
    public required int TokenCount { get; init; }
    public float Priority { get; init; } = 1.0f;
    public bool IsCacheable { get; init; }
    public string? SourceId { get; init; }
    public ContextSectionMetadata? Metadata { get; init; }
}

public sealed record ContextSectionMetadata(
    float RelevanceScore = 0f,
    float DecayScore = 0f,
    string? DocumentType = null,
    string? Subject = null,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// The role a section plays in the context window.
/// Determines default priority and positioning.
/// </summary>
public enum ContextRole
{
    SystemPrompt,       // Stable, cacheable, always first
    TeamContext,        // Conventions, standards — cacheable
    RetrievedContext,   // Dynamic per-query — positioned strategically
    Memory,            // Persistent knowledge from past sessions
    ConversationHistory,// Recent turns — sliding window
    UserQuery          // Always last — high attention zone
}

/// <summary>
/// Strategy for positioning content within the context window.
/// Based on "Lost in the Middle" research.
/// </summary>
public enum PositioningStrategy
{
    /// <summary>Most relevant at start and end, lowest in middle.</summary>
    PrimacyRecency,

    /// <summary>Most relevant first, decreasing order.</summary>
    RelevanceDescending,

    /// <summary>Chronological order (for conversation history).</summary>
    Chronological,

    /// <summary>Cache-stable prefix first, dynamic content after.</summary>
    CacheOptimized
}

/// <summary>
/// Token budget allocation for the context window.
/// Supports both fixed and proportional allocations.
/// </summary>
public sealed record ContextBudget
{
    public required int TotalBudget { get; init; }
    public required int ResponseReserve { get; init; }

    /// <summary>Available tokens for context (Total - ResponseReserve).</summary>
    public int AvailableBudget => TotalBudget - ResponseReserve;

    /// <summary>Per-role allocations as fraction of AvailableBudget.</summary>
    public required IReadOnlyDictionary<ContextRole, BudgetAllocation> Allocations { get; init; }

    /// <summary>Create a default budget for a given model's context window.</summary>
    public static ContextBudget Default(int totalTokens = 8192) => new()
    {
        TotalBudget = totalTokens,
        ResponseReserve = (int)(totalTokens * 0.25),
        Allocations = new Dictionary<ContextRole, BudgetAllocation>
        {
            [ContextRole.SystemPrompt] = new(0.12f, MinTokens: 200, MaxTokens: 1000),
            [ContextRole.TeamContext] = new(0.08f, MinTokens: 0, MaxTokens: 600),
            [ContextRole.RetrievedContext] = new(0.45f, MinTokens: 500, MaxTokens: 4000),
            [ContextRole.Memory] = new(0.15f, MinTokens: 0, MaxTokens: 1200),
            [ContextRole.ConversationHistory] = new(0.15f, MinTokens: 100, MaxTokens: 1200),
            [ContextRole.UserQuery] = new(0.05f, MinTokens: 50, MaxTokens: 500),
        }
    };

    /// <summary>Create a budget optimized for large-context models (128K+).</summary>
    public static ContextBudget LargeContext(int totalTokens = 128000) => new()
    {
        TotalBudget = totalTokens,
        ResponseReserve = 4096,
        Allocations = new Dictionary<ContextRole, BudgetAllocation>
        {
            [ContextRole.SystemPrompt] = new(0.05f, MinTokens: 500, MaxTokens: 4000),
            [ContextRole.TeamContext] = new(0.10f, MinTokens: 0, MaxTokens: 8000),
            [ContextRole.RetrievedContext] = new(0.50f, MinTokens: 2000, MaxTokens: 60000),
            [ContextRole.Memory] = new(0.15f, MinTokens: 0, MaxTokens: 16000),
            [ContextRole.ConversationHistory] = new(0.15f, MinTokens: 500, MaxTokens: 16000),
            [ContextRole.UserQuery] = new(0.05f, MinTokens: 100, MaxTokens: 2000),
        }
    };
}

/// <summary>
/// Budget allocation for a single context role.
/// Fraction is the target proportion; Min/Max are hard bounds.
/// </summary>
public sealed record BudgetAllocation(
    float Fraction,
    int MinTokens = 0,
    int MaxTokens = int.MaxValue)
{
    /// <summary>Compute allocated tokens given an available budget.</summary>
    public int ComputeTokens(int availableBudget) =>
        Math.Clamp((int)(availableBudget * Fraction), MinTokens, MaxTokens);
}

/// <summary>
/// The final compiled context ready to send to an LLM.
/// Includes the assembled text plus metrics about how it was compiled.
/// </summary>
public sealed record CompiledContext
{
    public required IReadOnlyList<ContextSection> Sections { get; init; }
    public required ContextBudget Budget { get; init; }
    public required ContextCompilationMetrics Metrics { get; init; }

    /// <summary>Get the full assembled text in correct order.</summary>
    public string AssembleText() => string.Join("\n\n", Sections.Select(s => s.Content));

    /// <summary>Get only the cacheable prefix (for prompt caching APIs).</summary>
    public string CacheablePrefix() => string.Join("\n\n",
        Sections.Where(s => s.IsCacheable).Select(s => s.Content));
}

/// <summary>
/// Metrics from the context compilation process.
/// </summary>
public sealed record ContextCompilationMetrics
{
    public int TotalTokensUsed { get; init; }
    public int TokenBudgetAvailable { get; init; }
    public float BudgetUtilization => TokenBudgetAvailable > 0
        ? (float)TotalTokensUsed / TokenBudgetAvailable : 0f;
    public int CacheableTokens { get; init; }
    public float CacheRatio => TotalTokensUsed > 0
        ? (float)CacheableTokens / TotalTokensUsed : 0f;
    public int SectionsIncluded { get; init; }
    public int SectionsDropped { get; init; }
    public int SectionsCompressed { get; init; }
    public IReadOnlyDictionary<ContextRole, int> TokensPerRole { get; init; } = new Dictionary<ContextRole, int>();
    public TimeSpan CompilationTime { get; init; }
    public PositioningStrategy StrategyUsed { get; init; }
}
