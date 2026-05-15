namespace Koshi.Core.Retrieval;

using Koshi.Core.Models;

/// <summary>
/// Pluggable retrieval contract. Implementations search a corpus and return
/// ranked <see cref="SearchResult"/> chunks for context assembly.
/// </summary>
public interface IRetriever
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        RetrievalOptions options,
        CancellationToken ct = default);
}
