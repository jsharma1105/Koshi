using System.Security;
using System.Text.Json;
using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// JSON-file persistence backend — stores all memories in a single envelope file.
/// Activated by setting <c>KOSHI_MEMORY_FILE</c>. Atomic writes via temp + Move.
/// All (de)serialization goes through the AOT-safe source-generated
/// <see cref="KoshiJsonContext"/>.
/// </summary>
/// <remarks>
/// Backwards-compatible with v0.5.x — the on-disk format is unchanged.
/// </remarks>
internal sealed class JsonFileBackend : IMemoryBackend
{
    private const int SchemaVersion = 1;

    public string? Path { get; }
    public bool IsEnabled => Path is not null;
    public string? Location => Path;
    public string BackendKind => "json";
    public bool ShouldReload() => false;

    public int UnmanagedNoteCount => 0;
    public IReadOnlyList<string> UnmanagedNotePaths => [];
    public int DuplicateIdWarningCount => 0;

    public JsonFileBackend(string? path)
    {
        Path = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
    }

    public List<MemoryRecord> LoadAll()
    {
        if (Path is null || !File.Exists(Path)) return [];

        try
        {
            var json = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(json)) return [];

            var envelope = JsonSerializer.Deserialize(json, KoshiJsonContext.Default.PersistenceEnvelope);
            return envelope?.Memories ?? [];
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException
                or JsonException or NotSupportedException)
        {
            Console.Error.WriteLine($"[koshi] Failed to load memory file '{Path}': {ex.Message}");
            return [];
        }
    }

    public void Upsert(MemoryRecord record, IReadOnlyList<MemoryRecord> snapshot)
    {
        // Snapshot already includes the mutation — just rewrite the envelope.
        Save(snapshot);
    }

    public void Delete(string id, IReadOnlyList<MemoryRecord> snapshot)
    {
        Save(snapshot);
    }

    public void ReplaceAll(IReadOnlyList<MemoryRecord> records)
    {
        Save(records);
    }

    private void Save(IReadOnlyList<MemoryRecord> memories)
    {
        if (Path is null) return;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var envelope = new PersistenceEnvelope
            {
                SchemaVersion = SchemaVersion,
                SavedAt = DateTimeOffset.UtcNow,
                Memories = [.. memories],
            };
            var json = JsonSerializer.Serialize(envelope, KoshiJsonContext.Default.PersistenceEnvelope);

            var tempPath = Path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, Path, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException
                or JsonException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine($"[koshi] Failed to save memory file '{Path}': {ex.Message}");
        }
    }
}
