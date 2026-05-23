namespace Koshi.Mcp.Internal;

/// <summary>
/// Thrown by an <see cref="IMemoryBackend"/> when a durable write or delete fails after the
/// in-memory cache has already been mutated. Callers (the <see cref="MemoryStore"/>) catch this
/// to reload the cache from disk so the cache mirrors the actual durable state, then rethrow so
/// the tool layer can surface a ❌ response to the user instead of pretending the mutation
/// succeeded.
/// </summary>
/// <remarks>
/// <para>
/// The exception is intentionally narrow: only persistence failures — not validation, not lock
/// contention, not transient retries — should map to this type. Backends widen their typed
/// catch filters (IOException, UnauthorizedAccessException, SecurityException, JsonException,
/// ArgumentException, NotSupportedException, PathTooLongException, DirectoryNotFoundException)
/// and rethrow as this type, attaching the originating exception as <see cref="Exception.InnerException"/>.
/// </para>
/// <para>
/// <see cref="BackendKind"/> matches <see cref="IMemoryBackend.BackendKind"/> ("json" or "vault");
/// <see cref="Location"/> matches <see cref="IMemoryBackend.Location"/> (file path or vault root).
/// </para>
/// </remarks>
internal sealed class MemoryPersistenceException : Exception
{
    /// <summary>Stable identifier for the failing backend kind. Currently "json" or "vault".</summary>
    public string BackendKind { get; }

    /// <summary>File path (json) or vault root (vault). May be null if the backend was misconfigured.</summary>
    public string? Location { get; }

    public MemoryPersistenceException(string backendKind, string? location, string message, Exception inner)
        : base(message, inner)
    {
        BackendKind = backendKind;
        Location = location;
    }
}
