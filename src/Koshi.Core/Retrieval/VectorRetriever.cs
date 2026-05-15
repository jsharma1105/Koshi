namespace Koshi.Core.Retrieval;

using Koshi.Core.Models;
using Koshi.Core.Store;
using Microsoft.Extensions.AI;

/// <summary>
/// Retrieves chunks using embedding similarity via a vector store.
/// Handles semantic queries like "how does authentication work".
/// </summary>
public sealed class VectorRetriever : IRetriever
{
    private readonly IVectorStore _store;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;

    public VectorRetriever(IVectorStore store, IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
    {
        _store = store;
        _embeddingGenerator = embeddingGenerator;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, RetrievalOptions options, CancellationToken ct = default)
    {
        // Request more than TopK before filtering to ensure enough results survive
        int fetchCount = options.TopK * 3;
        var queryEmbedding = await _embeddingGenerator.GenerateAsync(query, cancellationToken: ct);
        var vector = queryEmbedding.Vector.ToArray();

        var results = await _store.SearchAsync(vector, fetchCount, ct);

        return results
            .Where(r => r.Score >= options.MinScore)
            .Where(r => options.DocumentTypeFilter is null || r.Chunk.Metadata.DocumentType == options.DocumentTypeFilter)
            .Take(options.TopK)
            .ToList();
    }
}
