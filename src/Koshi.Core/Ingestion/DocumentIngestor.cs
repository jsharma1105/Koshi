namespace Koshi.Core.Ingestion;

using Koshi.Core.Models;
using Koshi.Core.Tokenization;
using Microsoft.Extensions.AI;

/// <summary>
/// Ingests documents: loads → chunks → embeds → stores.
/// The full pipeline from raw text to searchable chunks.
/// </summary>
public sealed class DocumentIngestor
{
    private readonly IChunker _chunker;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly TokenCounter _tokenCounter;

    public DocumentIngestor(
        IChunker chunker,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        TokenCounter tokenCounter)
    {
        _chunker = chunker;
        _embeddingGenerator = embeddingGenerator;
        _tokenCounter = tokenCounter;
    }

    public async Task<IReadOnlyList<Chunk>> IngestAsync(
        string content, string source, string documentType, CancellationToken ct = default)
    {
        // 1. Chunk the content
        var chunks = _chunker.Chunk(content, source, documentType);
        if (chunks.Count == 0) return [];

        // 2. Embed all chunks in batch (more efficient than one-by-one)
        var texts = chunks.Select(c => c.Content).ToList();
        var embeddings = await _embeddingGenerator.GenerateAsync(texts, cancellationToken: ct);

        // 3. Validate embedding count matches chunk count
        if (embeddings.Count != chunks.Count)
            throw new InvalidOperationException(
                $"Embedding generator returned {embeddings.Count} embeddings for {chunks.Count} chunks");

        // 4. Attach embeddings to chunks
        var embeddedChunks = new List<Chunk>();
        for (int i = 0; i < chunks.Count; i++)
        {
            embeddedChunks.Add(chunks[i] with { Embedding = embeddings[i].Vector.ToArray() });
        }

        return embeddedChunks;
    }

    public IngestionStats GetStats(IReadOnlyList<Chunk> chunks)
    {
        var totalTokens = chunks.Sum(c => c.TokenCount);
        var avgTokens = chunks.Count > 0 ? totalTokens / chunks.Count : 0;
        var totalChars = chunks.Sum(c => c.Content.Length);

        return new IngestionStats(
            ChunkCount: chunks.Count,
            TotalTokens: totalTokens,
            AverageTokensPerChunk: avgTokens,
            TotalCharacters: totalChars,
            TokenCharRatio: totalChars > 0 ? (double)totalTokens / totalChars : 0,
            EstimatedEmbeddingCost: totalTokens * 0.00002 / 1000 // text-embedding-3-small pricing
        );
    }
}

public record IngestionStats(
    int ChunkCount,
    int TotalTokens,
    int AverageTokensPerChunk,
    int TotalCharacters,
    double TokenCharRatio,
    double EstimatedEmbeddingCost);
