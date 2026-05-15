namespace Koshi.Core.Reranking;

using Koshi.Core.Models;

/// <summary>
/// Reranks search results for improved precision.
/// Two-stage retrieval: fast recall (hybrid) → precise ranking (reranker).
/// </summary>
public interface IReranker
{
    Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> candidates,
        int topK,
        CancellationToken ct = default);

    /// <summary>Tokens consumed by the last rerank call (for cost tracking).</summary>
    RerankStats LastStats { get; }
}

public record RerankStats(
    int InputTokens,
    int OutputTokens,
    TimeSpan Latency,
    int CandidatesEvaluated);
