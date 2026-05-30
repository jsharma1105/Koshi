using System.Diagnostics;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Centralised helper for the "write text to a sibling temp file then rename
/// over the target" pattern. Concentrating it here means every caller picks
/// up the same hardened conventions:
///
/// <list type="bullet">
///   <item>
///     Temp suffix includes the current PID and a fresh <see cref="Guid"/> so
///     two cooperating processes (or two threads in the same process) writing
///     the same target never collide on the temp path. The old <c>"path.tmp"</c>
///     pattern caused both writers to fight over a single temp file and could
///     leave the target empty or half-written. (Opus multi-model review #4.)
///   </item>
///   <item>
///     Target directory is created on demand so callers don't have to repeat
///     the <c>Directory.CreateDirectory</c> dance.
///   </item>
///   <item>
///     If <see cref="File.Move(string, string, bool)"/> fails the temp file is
///     best-effort deleted so we don't leak <c>*.koshi-…tmp</c> droppings into
///     user directories. The original exception is re-thrown unchanged so
///     callers can keep their existing catch-filter behaviour.
///   </item>
/// </list>
///
/// <para>The temp suffix is intentionally a fixed pattern (<c>.koshi-{pid}-{guid}.tmp</c>)
/// so other Koshi cleanup paths can recognise and reap stale temp files left
/// behind by crashed processes. Callers SHOULD NOT pre-compute or rely on the
/// exact temp filename — treat it as opaque.</para>
/// </summary>
internal static class AtomicFileWriter
{
    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="path"/> atomically
    /// by serialising to <c>{path}.koshi-{pid}-{guid}.tmp</c> then renaming
    /// over the target with <c>overwrite: true</c>. Re-throws any exception
    /// from the underlying file operations after attempting to clean up the
    /// temp file.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path must be non-empty", nameof(path));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = MakeTempPath(path);
        try
        {
            File.WriteAllText(tempPath, contents);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryCleanup(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Atomic equivalent of <see cref="File.AppendAllText(string, string?)"/>:
    /// reads the existing file (if any), concatenates <paramref name="addition"/>,
    /// and writes the result back atomically. Useful when an append into a
    /// shared file must be all-or-nothing — a half-written append leaves the
    /// original content intact. New file is created if missing.
    /// </summary>
    public static void AppendAllText(string path, string addition)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        WriteAllText(path, existing + addition);
    }

    /// <summary>
    /// Build a unique temp path for <paramref name="path"/>. Exposed for tests
    /// that need to assert temp-file cleanup behaviour.
    /// </summary>
    internal static string MakeTempPath(string path)
        => $"{path}.koshi-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";

    private static void TryCleanup(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Debug.WriteLine($"[koshi] AtomicFileWriter cleanup failed for '{tempPath}': {ex.Message}");
        }
    }
}
