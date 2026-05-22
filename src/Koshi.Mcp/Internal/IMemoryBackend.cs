using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Persistence abstraction for the memory store. Two implementations:
///   - <see cref="JsonFileBackend"/>  — single-file JSON envelope (KOSHI_MEMORY_FILE)
///   - <see cref="VaultBackend"/>     — one Markdown file per memory under a vault (KOSHI_MEMORY_VAULT)
/// </summary>
/// <remarks>
/// Operations are not internally synchronized; the caller (<see cref="MemoryStore"/>) holds
/// the lock and guarantees single-threaded access.
/// </remarks>
internal interface IMemoryBackend
{
    /// <summary>True when the backend persists to disk. False = in-memory-only.</summary>
    bool IsEnabled { get; }

    /// <summary>Storage location (file path for json, directory path for vault).</summary>
    string? Location { get; }

    /// <summary>Stable identifier for the backend kind. Currently "json" or "vault".</summary>
    string BackendKind { get; }

    /// <summary>
    /// Returns true when the next read should reload from disk. JSON returns false
    /// (in-memory cache is authoritative after the initial load). Vault returns true
    /// when an external change has been observed since the last reload, OR when no
    /// filesystem watcher is attached (degraded "always reload" fallback).
    /// </summary>
    /// <remarks>
    /// Implementations may have read-and-clear semantics — call exactly once per
    /// tool entry, immediately followed by <see cref="LoadAll"/> when true is returned.
    /// </remarks>
    bool ShouldReload();

    /// <summary>Loads all memories from disk. Returns empty list if unconfigured or empty.</summary>
    List<MemoryRecord> LoadAll();

    /// <summary>
    /// Insert or update <paramref name="record"/> (identity is <see cref="MemoryRecord.Id"/>).
    /// <paramref name="snapshot"/> is the store's current cache including this mutation —
    /// backends that rewrite the whole file (json) use it; incremental backends (vault) ignore it.
    /// </summary>
    void Upsert(MemoryRecord record, IReadOnlyList<MemoryRecord> snapshot);

    /// <summary>Delete the memory with the given id, if present.</summary>
    void Delete(string id, IReadOnlyList<MemoryRecord> snapshot);

    /// <summary>
    /// Destructive: replaces all backend-managed memories with <paramref name="records"/>.
    /// Vault backend leaves unmanaged user notes alone.
    /// </summary>
    void ReplaceAll(IReadOnlyList<MemoryRecord> records);

    /// <summary>Count of files under the vault that lack a koshi.id (vault mode only).</summary>
    int UnmanagedNoteCount { get; }

    /// <summary>Paths of unmanaged notes (vault mode only). Empty for json.</summary>
    IReadOnlyList<string> UnmanagedNotePaths { get; }

    /// <summary>Count of duplicate-id warnings emitted on the last <see cref="LoadAll"/>.</summary>
    int DuplicateIdWarningCount { get; }
}
