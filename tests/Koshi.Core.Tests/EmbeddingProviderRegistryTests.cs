using Koshi.Core.Retrieval;

namespace Koshi.Core.Tests;

/// <summary>
/// Phase 1 of issue #28: embedding provider registry must be plug-replaceable
/// at startup and thread-safe to read.
/// </summary>
public sealed class EmbeddingProviderRegistryTests : IDisposable
{
    public void Dispose()
    {
        // Each test must leave the registry empty so we don't bleed
        // state into unrelated test cases.
        EmbeddingProviderRegistry.SetCurrent(null);
    }

    [Fact]
    public void Defaults_to_not_configured()
    {
        EmbeddingProviderRegistry.SetCurrent(null);
        Assert.False(EmbeddingProviderRegistry.IsConfigured);
        Assert.Null(EmbeddingProviderRegistry.Current);
    }

    [Fact]
    public void SetCurrent_makes_provider_visible()
    {
        var mock = new MockEmbeddingProvider("test-model", 16);
        EmbeddingProviderRegistry.SetCurrent(mock);

        Assert.True(EmbeddingProviderRegistry.IsConfigured);
        Assert.Same(mock, EmbeddingProviderRegistry.Current);
    }

    [Fact]
    public void SetCurrent_null_clears_provider()
    {
        EmbeddingProviderRegistry.SetCurrent(new MockEmbeddingProvider("m", 4));
        EmbeddingProviderRegistry.SetCurrent(null);

        Assert.False(EmbeddingProviderRegistry.IsConfigured);
    }

    [Fact]
    public async Task Mock_provider_returns_fixed_dimensions()
    {
        var mock = new MockEmbeddingProvider("test", 8);
        EmbeddingProviderRegistry.SetCurrent(mock);

        var vec = await EmbeddingProviderRegistry.Current!.EmbedAsync("hello");
        Assert.Equal(8, vec.Length);

        var batch = await EmbeddingProviderRegistry.Current!.EmbedBatchAsync(new[] { "a", "b", "c" });
        Assert.Equal(3, batch.Count);
        Assert.All(batch, v => Assert.Equal(8, v.Length));
    }

    private sealed class MockEmbeddingProvider(string modelName, int dim) : IEmbeddingProvider
    {
        public string ModelName { get; } = modelName;
        public int Dimensions { get; } = dim;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            // Deterministic fixed vector: dim-of-hash mod 7 plus index.
            var v = new float[Dimensions];
            var seed = text.GetHashCode();
            for (int i = 0; i < Dimensions; i++) v[i] = (seed + i) % 7;
            return Task.FromResult(v);
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            var results = new List<float[]>(texts.Count);
            foreach (var t in texts) results.Add(EmbedAsync(t, ct).Result);
            return Task.FromResult<IReadOnlyList<float[]>>(results);
        }
    }
}
