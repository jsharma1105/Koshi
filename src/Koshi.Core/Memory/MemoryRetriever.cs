namespace Koshi.Core.Memory;

using Koshi.Core.Models;
using Koshi.Core.Retrieval;
using Koshi.Core.Tokenization;
using Microsoft.Extensions.AI;

/// <summary>
/// Retrieves relevant memories for a query. Combines embedding similarity
/// with time-decay relevance to rank memories. Implements IRetriever so it
/// can plug into HybridRetriever alongside document retrievers.
/// </summary>
public sealed class MemoryRetriever : IRetriever
{
    private readonly IMemoryStore _memoryStore;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly TokenCounter _tokenCounter;
    private readonly MemoryScope _scope;

    /// <summary>Results from the last recall operation with decay metadata.</summary>
    public IReadOnlyList<MemoryRecallResult> LastRecallResults { get; private set; } = [];

    public MemoryRetriever(
        IMemoryStore memoryStore,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        TokenCounter tokenCounter,
        MemoryScope scope)
    {
        _memoryStore = memoryStore;
        _embeddingGenerator = embeddingGenerator;
        _tokenCounter = tokenCounter;
        _scope = scope;
    }

    /// <summary>
    /// Search memories by query. Returns results as Chunks (IRetriever contract)
    /// with scores that blend embedding similarity and decay relevance.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, RetrievalOptions options, CancellationToken ct = default)
    {
        // Generate query embedding
        var embeddingResult = await _embeddingGenerator.GenerateAsync([query], cancellationToken: ct);
        var queryEmbedding = embeddingResult[0].Vector.ToArray();

        // Fetch candidate memories by embedding similarity (fetch extra for post-scoring)
        int fetchCount = options.TopK * 3;
        var candidates = await _memoryStore.SearchByEmbeddingAsync(
            queryEmbedding, _scope, fetchCount, ct);

        if (candidates.Count == 0)
        {
            LastRecallResults = [];
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var scored = new List<MemoryRecallResult>();

        foreach (var memory in candidates)
        {
            ct.ThrowIfCancellationRequested();

            float embeddingSimilarity = memory.Embedding is not null
                ? Similarity.Cosine(queryEmbedding, memory.Embedding)
                : 0f;

            float decayedRelevance = MemoryDecay.ComputeRelevance(memory, now);

            // Blend: 70% embedding similarity + 30% decay relevance
            float finalScore = 0.7f * embeddingSimilarity + 0.3f * decayedRelevance;

            // Use tier-appropriate content for token counting
            string content = memory.Tier switch
            {
                MemoryTier.Warm when memory.CompressedContent is not null => memory.CompressedContent,
                MemoryTier.Cold when memory.CompressedContent is not null => memory.CompressedContent,
                _ => memory.Content,
            };

            scored.Add(new MemoryRecallResult(memory, embeddingSimilarity, decayedRelevance, finalScore)
            {
                EstimatedTokens = _tokenCounter.CountTokens(content),
            });
        }

        // Sort by final score, apply token budget, take topK
        scored.Sort((a, b) => b.FinalScore.CompareTo(a.FinalScore));

        var results = new List<SearchResult>();
        int tokenBudget = options.MaxTokenBudget;
        int tokensUsed = 0;

        foreach (var recall in scored)
        {
            if (results.Count >= options.TopK) break;
            if (tokensUsed + recall.EstimatedTokens > tokenBudget) continue;

            tokensUsed += recall.EstimatedTokens;

            // Record access (confirmed use — only when we actually return it)
            await _memoryStore.RecordAccessAsync(recall.Memory.Id, ct);

            // Convert to Chunk for IRetriever contract
            string content = recall.Memory.Tier switch
            {
                MemoryTier.Warm when recall.Memory.CompressedContent is not null => recall.Memory.CompressedContent,
                MemoryTier.Cold when recall.Memory.CompressedContent is not null => recall.Memory.CompressedContent,
                _ => recall.Memory.Content,
            };

            var chunk = new Chunk(
                recall.Memory.Id,
                content,
                new ChunkMetadata(
                    recall.Memory.Source,
                    $"memory:{recall.Memory.Type}",
                    0, content.Length,
                    recall.Memory.CreatedAt,
                    new Dictionary<string, string>
                    {
                        ["subject"] = recall.Memory.Subject,
                        ["tier"] = recall.Memory.Tier.ToString(),
                        ["decay"] = recall.DecayedRelevance.ToString("F3"),
                    }))
            {
                TokenCount = recall.EstimatedTokens,
            };

            results.Add(new SearchResult(chunk, recall.FinalScore, "memory"));
        }

        LastRecallResults = scored.Take(options.TopK).ToList();
        return results;
    }

    /// <summary>
    /// Recall memories with full metadata (not as Chunks).
    /// Use this for direct memory operations outside the IRetriever pipeline.
    /// </summary>
    public async Task<IReadOnlyList<MemoryRecallResult>> RecallAsync(
        string query, int topK = 5, int maxTokenBudget = 2048, CancellationToken ct = default)
    {
        var options = new RetrievalOptions(topK, MaxTokenBudget: maxTokenBudget);
        await SearchAsync(query, options, ct);
        return LastRecallResults;
    }
}
