namespace Koshi.Core.Retrieval;

using Koshi.Core.Models;

/// <summary>
/// Merges results from multiple retrievers using Reciprocal Rank Fusion.
/// This is the core orchestration — fuses vector + keyword (and later Bluebird).
/// </summary>
public sealed class HybridRetriever : IRetriever
{
    private readonly IReadOnlyList<IRetriever> _retrievers;
    private readonly int _fusionK;

    public HybridRetriever(IReadOnlyList<IRetriever> retrievers, int fusionK = 60)
    {
        _retrievers = retrievers;
        _fusionK = fusionK;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, RetrievalOptions options, CancellationToken ct = default)
    {
        // Scale fetch count by number of retrievers to ensure enough candidates for fusion
        int perRetrieverTopK = options.TopK * Math.Max(2, _retrievers.Count);
        var tasks = _retrievers.Select(r => r.SearchAsync(query, options with { TopK = perRetrieverTopK }, ct));
        var allResults = await Task.WhenAll(tasks);

        // Reciprocal Rank Fusion
        var scores = new Dictionary<string, (float Score, Chunk Chunk)>();

        foreach (var resultList in allResults)
        {
            for (int rank = 0; rank < resultList.Count; rank++)
            {
                var result = resultList[rank];
                var key = result.Chunk.Id;
                float rrfScore = 1.0f / (_fusionK + rank + 1);

                if (scores.TryGetValue(key, out var existing))
                    scores[key] = (existing.Score + rrfScore, existing.Chunk);
                else
                    scores[key] = (rrfScore, result.Chunk);
            }
        }

        return scores
            .OrderByDescending(kvp => kvp.Value.Score)
            .Take(options.TopK)
            .Select(kvp => new SearchResult(kvp.Value.Chunk, kvp.Value.Score, "hybrid"))
            .ToList();
    }
}
