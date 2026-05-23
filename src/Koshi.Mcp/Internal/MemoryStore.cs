using System.Threading;
using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Owns the in-memory memory cache, the lock, and ID allocation. Dispatches mutations
/// to the configured <see cref="IMemoryBackend"/>. Refreshes from disk on tool-call
/// entry when the backend reports <see cref="IMemoryBackend.ShouldReload"/>.
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
    /// In vault mode, <see cref="IMemoryBackend.LoadAll"/> is called first (gated by
    /// <see cref="IMemoryBackend.ShouldReload"/>) and <c>_nextId</c> is refreshed; in JSON
    /// mode, the cache is used as-is.
    /// <para>
    /// If <paramref name="action"/> throws <see cref="MemoryPersistenceException"/> (e.g. a
    /// backend write failed mid-mutation), the cache is reloaded from disk before the exception
    /// is rethrown so the in-memory list mirrors the actual durable state. A multi-step
    /// mutation that partially succeeded on disk will therefore leave the cache pointing at
    /// the real disk contents, not the pre-mutation snapshot.
    /// </para>
    /// </summary>
    public T WithFreshState<T>(Func<List<MemoryRecord>, T> action)
    {
        lock (_lock)
        {
            if (_backend.ShouldReload())
            {
                _memories = _backend.LoadAll();
                RecomputeNextId();
            }
            try
            {
                return action(_memories);
            }
            catch (MemoryPersistenceException)
            {
                // Persistence failure mid-mutation: cache and disk may have diverged. Reload
                // from disk so the cache mirrors the durable state. If even the reload fails
                // (e.g. vault dir deleted concurrently), keep the existing cache rather than
                // crash — the rethrow still surfaces the original failure.
                try
                {
                    _memories = _backend.LoadAll();
                    RecomputeNextId();
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException
                        or System.Security.SecurityException)
                {
                    Console.Error.WriteLine(
                        $"[koshi] Cache reload after persistence failure also failed: {ex.Message}. " +
                        $"In-memory cache may be ahead of disk until next reload.");
                }
                throw;
            }
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
            try
            {
                _backend.ReplaceAll(records);
                _memories = [.. records];
                RecomputeNextId();
            }
            catch (MemoryPersistenceException)
            {
                // A backend ReplaceAll can fail mid-way (e.g. VaultBackend deleted some
                // owned files before write threw). Reload from disk so the cache reflects
                // whatever survived rather than the stale pre-call state.
                try
                {
                    _memories = _backend.LoadAll();
                    RecomputeNextId();
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException
                        or System.Security.SecurityException)
                {
                    Console.Error.WriteLine(
                        $"[koshi] Cache reload after ReplaceAll failure also failed: {ex.Message}.");
                }
                throw;
            }
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
