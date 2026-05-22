using System.Threading;
using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Owns the in-memory memory cache, the lock, and ID allocation. Dispatches mutations
/// to the configured <see cref="IMemoryBackend"/>. Refreshes from disk on every
/// tool-call entry when the backend reports <see cref="IMemoryBackend.RequiresReloadPerCall"/>.
/// </summary>
internal sealed class MemoryStore
{
    private readonly IMemoryBackend _backend;
    private readonly Lock _lock = new();
    private List<MemoryRecord> _memories;
    private int _nextId;

    public MemoryStore(IMemoryBackend backend)
    {
        _backend = backend;
        _memories = backend.LoadAll();
        RecomputeNextId();
    }

    public IMemoryBackend Backend => _backend;

    /// <summary>
    /// Run <paramref name="action"/> under the store's lock with a fresh <c>_memories</c> list.
    /// In vault mode, <see cref="IMemoryBackend.LoadAll"/> is called first and
    /// <c>_nextId</c> is refreshed; in JSON mode, the cache is used as-is.
    /// </summary>
    public T WithFreshState<T>(Func<List<MemoryRecord>, T> action)
    {
        lock (_lock)
        {
            if (_backend.RequiresReloadPerCall)
            {
                _memories = _backend.LoadAll();
                RecomputeNextId();
            }
            return action(_memories);
        }
    }

    /// <summary>
    /// Allocate the next <c>mem-NNNNNN</c> id. Caller must hold the store lock — call this
    /// from within a <see cref="WithFreshState{T}"/> action so the disk has already been
    /// scanned (vault mode) and we won't collide with externally-added ids.
    /// </summary>
    public string AllocateId()
    {
        var n = Interlocked.Increment(ref _nextId);
        return $"mem-{n:D6}";
    }

    public void Upsert(MemoryRecord record) => _backend.Upsert(record, _memories);

    public void Delete(string id) => _backend.Delete(id, _memories);

    public void ReplaceAll(IReadOnlyList<MemoryRecord> records)
    {
        lock (_lock)
        {
            _backend.ReplaceAll(records);
            _memories = [.. records];
            RecomputeNextId();
        }
    }

    public void ForceReload()
    {
        lock (_lock)
        {
            _memories = _backend.LoadAll();
            RecomputeNextId();
        }
    }

    private void RecomputeNextId()
    {
        int max = _memories
            .Select(m => int.TryParse(m.Id.AsSpan(m.Id.LastIndexOf('-') + 1), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        if (max > _nextId) _nextId = max;
    }
}
