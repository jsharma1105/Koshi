namespace Koshi.Core.Models;

/// <summary>
/// A chunk of content with metadata and optional embedding.
/// This is the fundamental unit of retrieval.
/// </summary>
public record Chunk(
    string Id,
    string Content,
    ChunkMetadata Metadata,
    float[]? Embedding = null)
{
    public int TokenCount { get; init; }
}

public record ChunkMetadata(
    string Source,
    string DocumentType,
    int StartOffset,
    int EndOffset,
    DateTimeOffset IngestedAt,
    Dictionary<string, string>? Tags = null);

public record SearchResult(
    Chunk Chunk,
    float Score,
    string Source)
{
    public override string ToString() =>
        $"[{Score:F4}] ({Source}) {Chunk.Metadata.DocumentType}:{Chunk.Metadata.Source}";
}

public record RetrievalOptions(
    int TopK = 5,
    float MinScore = 0.0f,
    int MaxTokenBudget = 4096,
    string? DocumentTypeFilter = null);
