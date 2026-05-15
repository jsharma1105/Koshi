using System.Text.Json;
using System.Text.Json.Serialization;
using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Optional JSON file persistence for memories.
/// When enabled (KOSHI_MEMORY_FILE is set), writes are atomic (temp + replace).
/// </summary>
internal sealed class MemoryPersistence
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string? Path { get; }
    public bool IsEnabled => Path is not null;

    public MemoryPersistence(string? path)
    {
        Path = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
    }

    public List<MemoryRecord> LoadOrEmpty()
    {
        if (Path is null || !File.Exists(Path)) return [];

        try
        {
            var json = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(json)) return [];

            var envelope = JsonSerializer.Deserialize<PersistenceEnvelope>(json, JsonOptions);
            return envelope?.Memories ?? [];
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable file — start fresh but log to stderr so users can see it.
            Console.Error.WriteLine($"[koshi] Failed to load memory file '{Path}': {ex.Message}");
            return [];
        }
    }

    public void Save(IReadOnlyList<MemoryRecord> memories)
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
            var json = JsonSerializer.Serialize(envelope, JsonOptions);

            var tempPath = Path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[koshi] Failed to save memory file '{Path}': {ex.Message}");
        }
    }

    private sealed class PersistenceEnvelope
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset SavedAt { get; set; }
        public List<MemoryRecord> Memories { get; set; } = [];
    }
}
