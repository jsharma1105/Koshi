namespace Koshi.Core.Harness;

using Koshi.Core.Context;
using Koshi.Core.Memory;
using Koshi.Core.Models;

// ─── Session State ──────────────────────────────────────────────────────────

/// <summary>
/// Tracks the lifecycle of a conversation session.
/// The orchestrator coordinates access to this state but does not own it.
/// </summary>
public sealed class SessionState
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];
    public string UserId { get; init; } = "default";
    public string WorkspaceId { get; init; } = "default";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; private set; }
    public SessionStatus Status { get; private set; } = SessionStatus.Active;

    /// <summary>Accumulated token usage across all turns.</summary>
    public TokenUsage TotalTokens { get; } = new();

    /// <summary>Number of completed turns.</summary>
    public int TurnCount { get; private set; }

    /// <summary>Memory scope derived from user/workspace.</summary>
    public MemoryScope MemoryScope => new(UserId, WorkspaceId);

    public void IncrementTurn() => TurnCount++;

    public void End()
    {
        EndedAt = DateTimeOffset.UtcNow;
        Status = SessionStatus.Ended;
    }
}

public enum SessionStatus { Active, Ended, Faulted }

// ─── Token Usage ────────────────────────────────────────────────────────────

/// <summary>
/// Mutable token counter that accumulates across turns.
/// </summary>
public sealed class TokenUsage
{
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int CachedTokens { get; private set; }
    public int TotalTokens => InputTokens + OutputTokens;

    public void Add(int input, int output, int cached = 0)
    {
        InputTokens += input;
        OutputTokens += output;
        CachedTokens += cached;
    }
}

// ─── Conversation History ───────────────────────────────────────────────────

/// <summary>
/// Maintains ordered conversation turns with a sliding window.
/// Separate from SessionState so the orchestrator doesn't own storage.
/// </summary>
public sealed class ConversationHistory
{
    private readonly List<ConversationTurn> _turns = [];
    private readonly int _maxTurns;

    public ConversationHistory(int maxTurns = 50) => _maxTurns = maxTurns;

    public IReadOnlyList<ConversationTurn> Turns => _turns;
    public int Count => _turns.Count;

    public void AddTurn(string role, string content)
    {
        _turns.Add(new ConversationTurn(role, content, DateTimeOffset.UtcNow));

        // Sliding window — drop oldest pairs when over limit
        while (_turns.Count > _maxTurns)
            _turns.RemoveAt(0);
    }

    /// <summary>Get the most recent N turns.</summary>
    public IReadOnlyList<ConversationTurn> Recent(int count) =>
        _turns.Skip(Math.Max(0, _turns.Count - count)).ToList();
}

// ─── Session Turn Context ───────────────────────────────────────────────────

/// <summary>
/// Carries all data for a single turn through the pipeline.
/// Reduces parameter sprawl — each pipeline step reads/writes to this object.
/// </summary>
public sealed class SessionTurnContext
{
    // ── Input ──
    public required string Query { get; init; }
    public required SessionState Session { get; init; }
    public required ConversationHistory History { get; init; }

    // ── Pipeline results (set by each step) ──
    public IReadOnlyList<SearchResult> RetrievedChunks { get; set; } = [];
    public IReadOnlyList<SearchResult> RecalledMemories { get; set; } = [];
    public CompiledContext? CompiledContext { get; set; }
    public string? LlmResponse { get; set; }
    public ExtractionResult? FactExtraction { get; set; }

    // ── Fallback tracking ──
    public FallbackLevel FallbackLevel { get; set; } = FallbackLevel.None;
    public List<string> FallbackReasons { get; } = [];

    // ── Timing ──
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public TimeSpan? RetrievalLatency { get; set; }
    public TimeSpan? MemoryLatency { get; set; }
    public TimeSpan? CompilationLatency { get; set; }
    public TimeSpan? LlmLatency { get; set; }
    public TimeSpan? FactExtractionLatency { get; set; }
    public TimeSpan TotalLatency => DateTimeOffset.UtcNow - StartedAt;

    // ── Extensible properties ──
    public Dictionary<string, object?> Properties { get; } = new();
}

// ─── Turn Result ────────────────────────────────────────────────────────────

/// <summary>
/// The result of processing a single turn, returned to the caller.
/// </summary>
public sealed record TurnResult
{
    public required string Response { get; init; }
    public required TurnMetrics Metrics { get; init; }
    public FallbackLevel FallbackUsed { get; init; } = FallbackLevel.None;
    public int FactsExtracted { get; init; }
    public int ContradictionsDetected { get; init; }
}

/// <summary>
/// Metrics for a single turn — the observability slice.
/// </summary>
public sealed record TurnMetrics
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CachedTokens { get; init; }
    public int ContextSectionsIncluded { get; init; }
    public int ContextSectionsDropped { get; init; }
    public float BudgetUtilization { get; init; }
    public float CacheRatio { get; init; }
    public int RetrievedChunkCount { get; init; }
    public int RecalledMemoryCount { get; init; }
    public TimeSpan RetrievalLatency { get; init; }
    public TimeSpan MemoryLatency { get; init; }
    public TimeSpan CompilationLatency { get; init; }
    public TimeSpan LlmLatency { get; init; }
    public TimeSpan TotalLatency { get; init; }
    public PositioningStrategy PositioningStrategy { get; init; }
}

// ─── Fallback Levels ────────────────────────────────────────────────────────

/// <summary>
/// Ordered degradation levels. Each level trades quality for reliability.
/// </summary>
public enum FallbackLevel
{
    /// <summary>Full pipeline — no degradation.</summary>
    None = 0,

    /// <summary>Tighter budget — fewer sections included.</summary>
    ReducedContext = 1,

    /// <summary>Memory-only — no retrieval results available.</summary>
    MemoryOnly = 2,

    /// <summary>No external context — LLM answers from its own knowledge.</summary>
    NoContext = 3,

    /// <summary>Complete failure — return error message.</summary>
    Failed = 4,
}

// ─── Pipeline Configuration ─────────────────────────────────────────────────

/// <summary>
/// Controls which pipeline steps are enabled and their parameters.
/// </summary>
public sealed record HarnessPipelineConfig
{
    public bool EnableRetrieval { get; init; } = true;
    public bool EnableMemory { get; init; } = true;
    public bool EnableFactExtraction { get; init; } = true;
    public bool EnableFallback { get; init; } = true;

    /// <summary>System prompt sent to the LLM.</summary>
    public string SystemPrompt { get; init; } = "You are a helpful assistant. Use the provided context to answer questions accurately.";

    /// <summary>Optional team context for cache-stable prefix.</summary>
    public string? TeamContext { get; init; }

    /// <summary>Max conversation turns to include in context.</summary>
    public int MaxHistoryTurns { get; init; } = 10;

    /// <summary>Retrieval options.</summary>
    public RetrievalOptions RetrievalOptions { get; init; } = new();

    /// <summary>Context budget.</summary>
    public ContextBudget? ContextBudget { get; init; }

    /// <summary>Positioning strategy.</summary>
    public PositioningStrategy PositioningStrategy { get; init; } = PositioningStrategy.CacheOptimized;

    /// <summary>Skip fact extraction during degraded/fallback modes.</summary>
    public bool SkipFactExtractionOnFallback { get; init; } = true;

    /// <summary>Only extract facts from user messages (not LLM responses).</summary>
    public bool ExtractFromUserOnly { get; init; } = false;
}
