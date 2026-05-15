namespace Koshi.Core.Store;

using Koshi.Core.Models;
using Koshi.Core.Retrieval;

public interface IVectorStore
{
    Task AddAsync(IReadOnlyList<Chunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(float[] queryEmbedding, int topK, CancellationToken ct = default);
    int Count { get; }
    int TotalTokens { get; }
}

/// <summary>
/// In-memory vector store with brute-force cosine similarity.
/// Thread-safe via ReaderWriterLockSlim. Good enough for 10K chunks.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore, IDisposable
{
    private readonly List<Chunk> _chunks = [];
    private readonly ReaderWriterLockSlim _lock = new();
    private int? _expectedDimensions;

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _chunks.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public int TotalTokens
    {
        get
        {
            _lock.EnterReadLock();
            try { return _chunks.Sum(c => c.TokenCount); }
            finally { _lock.ExitReadLock(); }
        }
    }

    public Task AddAsync(IReadOnlyList<Chunk> chunks, CancellationToken ct = default)
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                if (chunk.Embedding is null)
                    throw new ArgumentException($"Chunk '{chunk.Id}' has no embedding");

                // Validate embedding dimensions are consistent
                if (_expectedDimensions is null)
                    _expectedDimensions = chunk.Embedding.Length;
                else if (chunk.Embedding.Length != _expectedDimensions)
                    throw new ArgumentException(
                        $"Chunk '{chunk.Id}' has {chunk.Embedding.Length} dimensions, expected {_expectedDimensions}");

                _chunks.Add(chunk);
            }
        }
        finally { _lock.ExitWriteLock(); }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        float[] queryEmbedding, int topK, CancellationToken ct = default)
    {
        _lock.EnterReadLock();
        try
        {
            if (_expectedDimensions is not null && queryEmbedding.Length != _expectedDimensions)
                throw new ArgumentException(
                    $"Query embedding has {queryEmbedding.Length} dimensions, expected {_expectedDimensions}");

            // Use a min-heap (PriorityQueue) to avoid sorting the full corpus
            var heap = new PriorityQueue<SearchResult, float>(topK + 1);

            foreach (var chunk in _chunks)
            {
                var score = Similarity.Cosine(queryEmbedding, chunk.Embedding!);
                if (heap.Count < topK)
                {
                    heap.Enqueue(new SearchResult(chunk, score, "vector"), score);
                }
                else if (heap.TryPeek(out _, out float minScore) && score > minScore)
                {
                    heap.DequeueEnqueue(new SearchResult(chunk, score, "vector"), score);
                }
            }

            // Extract results in descending score order
            var results = new SearchResult[heap.Count];
            for (int i = results.Length - 1; i >= 0; i--)
                results[i] = heap.Dequeue();

            return Task.FromResult<IReadOnlyList<SearchResult>>(results);
        }
        finally { _lock.ExitReadLock(); }
    }

    public void Dispose() => _lock.Dispose();
}
