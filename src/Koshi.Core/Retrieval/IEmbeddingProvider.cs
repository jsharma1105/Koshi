namespace Koshi.Core.Retrieval;

/// <summary>
/// Phase 1 of issue #28: optional embedding plumbing.
///
/// <para>
/// Concrete providers are <em>not</em> shipped in the AOT binary — that
/// would force a hard dependency on either the OpenAI SDK or ONNX Runtime
/// (both of which currently bring AOT-incompatible reflection). Instead,
/// the core defines the interface and a tiny in-process registry; optional
/// adapter packages (e.g. <c>Koshi.Embeddings.OpenAI</c>) will register an
/// implementation at startup via
/// <see cref="EmbeddingProviderRegistry.SetCurrent"/>.
/// </para>
///
/// <para>
/// When no provider is registered, every embedding-dependent code path in
/// Koshi is a no-op — BM25 keyword search remains the sole retriever, and
/// memories continue to store <c>null</c> in <c>MemoryRecord.Embedding</c>.
/// </para>
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Model identifier the provider will embed against (e.g. <c>text-embedding-3-small</c>).</summary>
    string ModelName { get; }

    /// <summary>Dimensionality of every vector returned by <see cref="EmbedAsync"/>.</summary>
    int Dimensions { get; }

    /// <summary>Embed a single text. Implementations must return a <c>float[Dimensions]</c>.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>Embed many texts. Implementations should batch the upstream call when possible.</summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>
/// Process-wide registry for the active <see cref="IEmbeddingProvider"/>.
/// Thread-safe; last writer wins (intended for one-shot startup registration).
/// </summary>
public static class EmbeddingProviderRegistry
{
    private static IEmbeddingProvider? _current;
    private static readonly Lock _lock = new();

    /// <summary>The provider in use, or <c>null</c> when no provider is registered.</summary>
    public static IEmbeddingProvider? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>True when an embedding provider has been registered for this process.</summary>
    public static bool IsConfigured
    {
        get { lock (_lock) return _current is not null; }
    }

    /// <summary>Register or replace the active provider.</summary>
    public static void SetCurrent(IEmbeddingProvider? provider)
    {
        lock (_lock) _current = provider;
    }
}
