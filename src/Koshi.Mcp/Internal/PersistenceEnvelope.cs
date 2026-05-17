using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// On-disk envelope for the memory file (KOSHI_MEMORY_FILE).
/// Promoted to a top-level internal type so the AOT-safe
/// <see cref="KoshiJsonContext"/> source generator can target it.
/// </summary>
internal sealed class PersistenceEnvelope
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    public List<MemoryRecord> Memories { get; set; } = [];
}
