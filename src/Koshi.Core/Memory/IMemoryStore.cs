namespace Koshi.Core.Memory;

/// <summary>
/// Persistence layer for memories. Supports CRUD, scoped queries, and embedding search.
/// </summary>
public interface IMemoryStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task StoreAsync(MemoryRecord memory, CancellationToken ct = default);
    Task StoreAsync(IReadOnlyList<MemoryRecord> memories, CancellationToken ct = default);
    Task<MemoryRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRecord>> GetByScopeAsync(MemoryScope scope, MemoryType? type = null, MemoryTier? tier = null, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryRecord>> SearchByEmbeddingAsync(float[] queryEmbedding, MemoryScope scope, int topK = 10, CancellationToken ct = default);
    Task UpdateAsync(MemoryRecord memory, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<int> CountAsync(MemoryScope? scope = null, CancellationToken ct = default);
    Task RecordAccessAsync(string id, CancellationToken ct = default);
}
