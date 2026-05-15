namespace Koshi.Core.Retrieval;

using System.Numerics.Tensors;
using Koshi.Core.Models;

public interface IRetriever
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        RetrievalOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Similarity functions for comparing embedding vectors.
/// Includes all three metrics so you can compare real numbers.
/// </summary>
public static class Similarity
{
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => TensorPrimitives.CosineSimilarity(a, b);

    public static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => TensorPrimitives.Dot(a, b);

    public static float Euclidean(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => TensorPrimitives.Distance(a, b);
}
