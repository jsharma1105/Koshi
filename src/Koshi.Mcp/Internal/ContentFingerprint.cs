using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Computes a stable fingerprint of a directory's indexable contents so the
/// persisted BM25 snapshot can detect drift between save and load. The hash
/// covers every file that <see cref="SafeFileEnumerator"/> would surface
/// (same exclusion rules + same enumeration parameters) plus each file's
/// size and UTC modification time — strong enough to catch edits, renames,
/// additions, and deletions without paying for a full content hash on
/// every restart.
/// </summary>
internal static class ContentFingerprint
{
    /// <summary>Sentinel used for the <c>koshi_index</c> in-memory corpus.</summary>
    public const string InMemorySource = "in-memory";

    /// <summary>
    /// Returns a hex-encoded SHA-256 fingerprint, or <c>null</c> for the
    /// in-memory sentinel or any path that cannot be enumerated.
    /// </summary>
    public static string? Compute(string? rootPath, IndexEnumerationParams? enumeration)
    {
        if (string.IsNullOrEmpty(rootPath)
            || rootPath == InMemorySource
            || enumeration is null
            || !Directory.Exists(rootPath))
        {
            return null;
        }

        try
        {
            IEnumerable<string> files = SafeFileEnumerator.EnumerateIndexableFiles(
                rootPath,
                enumeration.Pattern,
                enumeration.MaxFileSizeBytes,
                enumeration.MaxFiles);

            // Sort by relative path so independent enumerations of the same
            // directory produce identical fingerprints regardless of FS order.
            var sorted = files
                .Select(f => new
                {
                    Rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/'),
                    Info = new FileInfo(f),
                })
                .OrderBy(x => x.Rel, StringComparer.Ordinal)
                .ToList();

            using var sha = SHA256.Create();
            var buffer = new StringBuilder(capacity: sorted.Count * 80);
            foreach (var entry in sorted)
            {
                buffer.Append(entry.Rel);
                buffer.Append('\0');
                buffer.Append(entry.Info.Length.ToString(CultureInfo.InvariantCulture));
                buffer.Append('\0');
                buffer.Append(entry.Info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture));
                buffer.Append('\n');
            }

            byte[] bytes = Encoding.UTF8.GetBytes(buffer.ToString());
            byte[] hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // If we cannot fingerprint, behave as if the path changed —
            // the caller will fall through to a fresh re-index.
            return null;
        }
    }
}
