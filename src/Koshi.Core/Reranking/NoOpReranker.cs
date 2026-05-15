namespace Koshi.Core.Reranking;

using Koshi.Core.Models;

/// <summary>
/// Passthrough reranker — returns candidates unchanged.
/// Used as baseline in A/B comparisons with LlmReranker.
/// </summary>
public sealed class NoOpReranker : IReranker
{
    public RerankStats LastStats { get; private set; } = new(0, 0, TimeSpan.Zero, 0);

    public Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query, IReadOnlyList<SearchResult> candidates, int topK, CancellationToken ct = default)
    {
        LastStats = new RerankStats(0, 0, TimeSpan.Zero, candidates.Count);
        IReadOnlyList<SearchResult> result = candidates.Take(topK).ToList();
        return Task.FromResult(result);
    }
}
