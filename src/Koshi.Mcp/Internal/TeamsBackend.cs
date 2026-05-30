using System.Security;
using System.Text.Json;

namespace Koshi.Mcp.Internal;

/// <summary>
/// JSON-file persistence backend for the team registry (teams, per-turn
/// scores, and feedback). Mirrors <see cref="JsonFileBackend"/>'s atomic
/// write semantics and AOT-safe (de)serialisation via
/// <see cref="KoshiJsonContext"/>.
///
/// <para>The backend keeps the <see cref="LastSaveError"/> and
/// <see cref="LastLoadError"/> diagnostics so <c>koshi_health</c> can surface
/// persistence problems even when the in-memory registry continues to
/// accept mutations. Failures are also written to stderr at the point they
/// happen.</para>
/// </summary>
internal sealed class TeamsBackend
{
    /// <summary>Absolute, normalised file path, or null when persistence is disabled.</summary>
    public string? Path { get; }

    /// <summary>True iff this backend will read from / write to disk.</summary>
    public bool IsEnabled => Path is not null;

    /// <summary>Last load failure (sticky across server lifetime); null if the most recent load succeeded.</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>Last save failure (sticky across server lifetime); null if the most recent save succeeded.</summary>
    public string? LastSaveError { get; private set; }

    public TeamsBackend(string? path)
    {
        Path = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
    }

    /// <summary>
    /// Load the persisted envelope from disk. Returns null when persistence
    /// is disabled, the file does not exist yet (cold start), the file is
    /// empty, or the file cannot be parsed. Parse / IO failures populate
    /// <see cref="LastLoadError"/> and emit a stderr diagnostic; callers
    /// should treat them as "no persisted state" rather than blocking the
    /// server from starting.
    /// </summary>
    public TeamsEnvelope? Load()
    {
        if (Path is null) return null;
        if (!File.Exists(Path))
        {
            LastLoadError = null;
            return null;
        }

        try
        {
            var json = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(json))
            {
                LastLoadError = null;
                return null;
            }

            var envelope = JsonSerializer.Deserialize(json, KoshiJsonContext.Default.TeamsEnvelope);
            LastLoadError = null;
            return envelope;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException
                or JsonException or NotSupportedException)
        {
            LastLoadError = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"[koshi] Failed to load teams file '{Path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Atomically write the envelope to disk via a temp file + Move. Persistence
    /// failures populate <see cref="LastSaveError"/>, emit a stderr diagnostic,
    /// and rethrow as <see cref="TeamsPersistenceException"/> so the caller can
    /// surface a warning to the MCP user.
    /// </summary>
    public void Save(TeamsEnvelope envelope)
    {
        if (Path is null) return;

        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(envelope, KoshiJsonContext.Default.TeamsEnvelope);

            AtomicFileWriter.WriteAllText(Path, json);
            LastSaveError = null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException
                or JsonException or NotSupportedException or PathTooLongException
                or ArgumentException or DirectoryNotFoundException)
        {
            LastSaveError = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"[koshi] Failed to save teams file '{Path}': {ex.Message}");
            throw new TeamsPersistenceException(Path, ex.Message, ex);
        }
    }
}

/// <summary>
/// Thrown by <see cref="TeamsBackend.Save"/> on a persistence failure. The
/// in-memory mutation that triggered the save is still considered successful
/// — callers should surface a "score recorded in memory, persistence failed"
/// warning to the user rather than rolling back.
/// </summary>
internal sealed class TeamsPersistenceException : Exception
{
    public string Path { get; }

    public TeamsPersistenceException(string path, string message, Exception inner)
        : base(message, inner)
    {
        Path = path;
    }
}
