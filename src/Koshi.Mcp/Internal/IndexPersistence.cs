using System.Text.Json;
using Koshi.Core.Models;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Optional JSON file persistence for the BM25 retrieval index.
/// Enabled when <c>KOSHI_INDEX_FILE</c> is set; writes are atomic (temp +
/// replace) and (de)serialization goes through the AOT-safe source-generated
/// <see cref="KoshiJsonContext"/>. Mirrors <see cref="MemoryPersistence"/>.
/// </summary>
internal sealed class IndexPersistence
{
    private const int SchemaVersion = 1;

    public string? Path { get; }
    public bool IsEnabled => Path is not null;

    public IndexPersistence(string? path)
    {
        Path = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
    }

    /// <summary>
    /// Loads the snapshot from disk, or returns <c>null</c> when persistence
    /// is disabled, the file is missing, the schema is incompatible, or the
    /// file is corrupt. Corrupt-file errors are logged to stderr; they do
    /// not throw, so callers can treat "no snapshot" uniformly.
    /// </summary>
    public IndexEnvelope? LoadOrNull()
    {
        if (Path is null || !File.Exists(Path)) return null;

        try
        {
            var json = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var envelope = JsonSerializer.Deserialize(json, KoshiJsonContext.Default.IndexEnvelope);
            if (envelope is null) return null;

            if (envelope.SchemaVersion != SchemaVersion)
            {
                Console.Error.WriteLine(
                    $"[koshi] Ignoring index snapshot '{Path}': schema version " +
                    $"{envelope.SchemaVersion} (expected {SchemaVersion}).");
                return null;
            }

            return envelope;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[koshi] Failed to load index file '{Path}': {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"[koshi] Failed to load index file '{Path}': {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[koshi] Failed to load index file '{Path}': {ex.Message}");
            return null;
        }
        catch (NotSupportedException ex)
        {
            Console.Error.WriteLine($"[koshi] Failed to load index file '{Path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Persists <paramref name="chunks"/> to <see cref="Path"/>. Returns
    /// <c>true</c> when persistence is enabled and the write succeeded;
    /// <c>false</c> when persistence is disabled (no-op) or the IO failed
    /// (error logged to stderr, never throws — callers depend on Save being
    /// best-effort so an unwritable snapshot path doesn't block indexing).
    /// Callers that need to report post-condition state to users should
    /// only claim "saved" when this returns <c>true</c>.
    /// </summary>
    public bool Save(string? sourcePath, string? contentFingerprint, IndexEnumerationParams? enumeration, IReadOnlyList<Chunk> chunks)
    {
        if (Path is null) return false;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var envelope = new IndexEnvelope
            {
                SchemaVersion = SchemaVersion,
                SavedAt = DateTimeOffset.UtcNow,
                SourcePath = sourcePath,
                ContentFingerprint = contentFingerprint,
                Enumeration = enumeration,
                Chunks = [.. chunks],
                SnapshotPath = Path,
            };
            var json = JsonSerializer.Serialize(envelope, KoshiJsonContext.Default.IndexEnvelope);

            var tempPath = Path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, Path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or JsonException)
        {
            Console.Error.WriteLine($"[koshi] Failed to save index file '{Path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Deletes the snapshot file. Returns <c>true</c> when a file was
    /// actually removed; <c>false</c> when persistence is disabled, the file
    /// was already absent, or the delete failed (error logged to stderr,
    /// never throws). Callers that need to report "snapshot_deleted" to
    /// users should rely on this return value, not a pre-delete existence
    /// check (which races with concurrent deletion).
    /// </summary>
    public bool Delete()
    {
        if (Path is null || !File.Exists(Path)) return false;
        try
        {
            File.Delete(Path);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException ||
            ex is IOException ||
            ex is System.Security.SecurityException ||
            ex is NotSupportedException ||
            ex is ArgumentException)
        {
            Console.Error.WriteLine($"[koshi] Failed to delete index file '{Path}': {ex.Message}");
            return false;
        }
    }
}
