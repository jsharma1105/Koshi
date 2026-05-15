namespace Koshi.Core.Memory;

/// <summary>
/// A unit of extracted knowledge persisted across sessions.
/// Represents a fact, decision, pattern, or preference learned from interactions.
/// </summary>
public sealed record MemoryRecord
{
    public required string Id { get; init; }
    public required MemoryType Type { get; init; }
    public required string Content { get; init; }
    public required string Subject { get; init; }
    public required MemoryScope Scope { get; init; }
    public required string Source { get; init; }
    public required float Confidence { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;
    public int AccessCount { get; set; }
    public MemoryTier Tier { get; set; } = MemoryTier.Hot;
    public string? CompressedContent { get; set; }
    public string? SupersededBy { get; set; }
    public string? ContradictionNote { get; set; }
    public float[]? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    public int EmbeddingDimensions { get; set; }
}

public enum MemoryType
{
    Fact,        // "The API uses stored procedures, not EF Core"
    Decision,    // "We chose Dapper over EF Core because of performance"
    Pattern,     // "When X happens, the team usually does Y"
    Preference   // "The user prefers tabs over spaces"
}

public enum MemoryTier
{
    Hot,   // Full content, recent, high token cost (~500 tokens/memory)
    Warm,  // Compressed summary, moderate token cost (~100 tokens/memory)
    Cold   // Key facts only, low token cost (~20 tokens/memory)
}

/// <summary>
/// Scoping boundary for memories. Prevents cross-contamination.
/// </summary>
public sealed record MemoryScope(
    string UserId,
    string WorkspaceId = "default",
    string? ThreadId = null)
{
    /// <summary>Global scope — memories visible to all users in a workspace.</summary>
    public static MemoryScope Global(string workspaceId = "default") =>
        new("*", workspaceId);
}

/// <summary>
/// Result from fact extraction with quality metadata.
/// </summary>
public sealed record ExtractionResult
{
    public required IReadOnlyList<MemoryRecord> Extracted { get; init; }
    public int Accepted { get; init; }
    public int Rejected { get; init; }
    public IReadOnlyList<string> RejectionReasons { get; init; } = [];
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// A possible contradiction between two memories.
/// </summary>
public sealed record Contradiction(
    MemoryRecord Existing,
    MemoryRecord Incoming,
    float Similarity,
    string? Explanation = null);
