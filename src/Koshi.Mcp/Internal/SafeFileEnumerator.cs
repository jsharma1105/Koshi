namespace Koshi.Mcp.Internal;

/// <summary>
/// Safely enumerates indexable files for retrieval. Excludes secrets, binaries,
/// build output, hidden directories, and respects size/count limits.
/// </summary>
internal static class SafeFileEnumerator
{
    private static readonly string[] SupportedExtensions =
    [
        ".md", ".markdown", ".txt", ".rst", ".adoc",
        ".cs", ".fs", ".vb",
        ".py", ".pyi",
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
        ".java", ".kt", ".scala", ".groovy",
        ".go", ".rs", ".rb", ".php", ".swift", ".m", ".mm",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".hh",
        ".json", ".jsonc", ".yaml", ".yml", ".xml", ".toml", ".ini", ".cfg", ".conf",
        ".html", ".htm", ".css", ".scss", ".less",
        ".sql", ".graphql", ".gql", ".proto",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd",
        ".dockerfile", ".tf", ".tfvars", ".bicep",
    ];

    private static readonly string[] ExcludedDirectorySegments =
    [
        "/bin/", "/obj/", "/node_modules/", "/.git/", "/.hg/", "/.svn/",
        "/.vs/", "/.idea/", "/.vscode/", "/packages/", "/dist/", "/build/",
        "/__pycache__/", "/.pytest_cache/", "/.mypy_cache/", "/.ruff_cache/",
        "/target/", "/.next/", "/.nuxt/", "/.cache/", "/coverage/",
        "/.terraform/", "/.aws/", "/.azure/", "/.ssh/", "/.gnupg/",
        "/vendor/",
    ];

    private static readonly string[] ExcludedFilePatterns =
    [
        ".env", ".env.local", ".env.production", ".env.development",
        "secrets.json", "secrets.yaml", "secrets.yml",
        "credentials.json", "credentials",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
    ];

    private static readonly string[] ExcludedFileExtensions =
    [
        ".pem", ".key", ".pfx", ".p12", ".crt", ".cer", ".der",
        ".pkcs12", ".jks", ".keystore",
    ];

    /// <summary>
    /// Path-based indexability check. Returns true when the path *would* be
    /// considered for indexing based on its name/extension/parent segments
    /// alone — without touching the filesystem.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="IndexWatcher"/> to decide whether a watcher event
    /// is for a path that might have been (or might become) part of the index.
    /// Importantly this works for paths whose file no longer exists (deletes /
    /// rename-olds), so the watcher can correctly remove stale chunks for a
    /// file that was indexed at session start but has since been deleted.
    /// File-state checks (size / readability) belong to
    /// <see cref="IsCurrentlyIndexable"/>.
    ///
    /// <para>Pass <paramref name="rootPath"/> so the hidden-directory exclusion
    /// only considers ancestors *beneath* the indexed root. Otherwise a project
    /// living under <c>~/.dotfiles/work/repo</c> would have every file dropped
    /// because <c>.dotfiles</c> appears in the ancestry. (Opus multi-model
    /// review #9.)</para>
    /// </remarks>
    public static bool IsPathLikelyIndexed(string fullPath, string? globPattern, string? rootPath = null)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;

        var normalized = fullPath.Replace('\\', '/');
        var fileName = Path.GetFileName(fullPath);
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();

        if (IsExcludedDirectory(normalized, NormalizeRoot(rootPath))) return false;
        if (IsExcludedFile(fileName, ext)) return false;

        if (!string.IsNullOrEmpty(globPattern))
        {
            if (!MatchesSimpleGlob(fileName, globPattern)) return false;
        }
        else if (!IsSupportedExtension(ext))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Full file-state indexability check. Combines
    /// <see cref="IsPathLikelyIndexed"/> with a current-state stat: the file
    /// exists, is non-empty, and within the size budget. Returns false when
    /// the file has been deleted or the stat call fails — callers MUST treat
    /// a false here as "do not re-read", not as "do not touch the index"
    /// (a previously-indexed deleted file still needs its chunks removed,
    /// which the path-based predicate above is for).
    /// </summary>
    public static bool IsCurrentlyIndexable(string fullPath, string? globPattern, long maxBytes, string? rootPath = null)
    {
        if (!IsPathLikelyIndexed(fullPath, globPattern, rootPath)) return false;

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return false;
            if (info.Length == 0) return false;
            if (info.Length > maxBytes) return false;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or NotSupportedException or PathTooLongException or ArgumentException)
        {
            return false;
        }
    }

    public static IEnumerable<string> EnumerateIndexableFiles(
        string rootPath,
        string? globPattern,
        long maxBytes,
        int maxFiles)
    {
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple,
            ReturnSpecialDirectories = false,
        };

        int yielded = 0;
        var normalizedRoot = NormalizeRoot(rootPath);
        foreach (var file in Directory.EnumerateFiles(rootPath, "*", enumerationOptions))
        {
            if (yielded >= maxFiles) yield break;

            var normalized = file.Replace('\\', '/');
            var fileName = Path.GetFileName(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();

            if (IsExcludedDirectory(normalized, normalizedRoot)) continue;
            if (IsExcludedFile(fileName, ext)) continue;

            if (!string.IsNullOrEmpty(globPattern))
            {
                if (!MatchesSimpleGlob(fileName, globPattern)) continue;
            }
            else if (!IsSupportedExtension(ext))
            {
                continue;
            }

            long length;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch (Exception ex) when (
                ex is IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException or
                NotSupportedException or
                PathTooLongException)
            {
                continue;
            }

            if (length == 0 || length > maxBytes) continue;

            yielded++;
            yield return file;
        }
    }

    /// <summary>
    /// Path-based check for whether a directory itself is in an excluded
    /// segment (e.g., <c>.git/</c>, <c>node_modules/</c>). Used by the
    /// <c>IndexWatcher</c> to decide whether to drop directory events before
    /// they hit the drain. Operates on a normalized full path; appends a
    /// trailing <c>/</c> so the segment-contains check is reliable for the
    /// directory leaf itself. Pass <paramref name="rootPath"/> so dot-prefix
    /// ancestor segments above the indexed root are ignored — otherwise a
    /// project living under <c>~/.dotfiles/work/repo</c> would have every
    /// directory event dropped. (Opus multi-model review #9.)
    /// </summary>
    public static bool IsExcludedDirectoryPath(string fullPath, string? rootPath = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return true;
        var normalized = fullPath.Replace('\\', '/').TrimEnd('/') + "/";
        return IsExcludedDirectory(normalized, NormalizeRoot(rootPath));
    }

    private static string? NormalizeRoot(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return null;
        var n = rootPath.Replace('\\', '/').TrimEnd('/');
        return n.Length == 0 ? null : n + "/";
    }

    private static bool IsExcludedDirectory(string normalizedPath, string? normalizedRoot = null)
    {
        if (ExcludedDirectorySegments.Any(seg => normalizedPath.Contains(seg, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Exclude any path segment that starts with '.' (hidden dir convention)
        // except for a small allow-list of conventional, safe directories.
        // When a root is supplied, only consider segments BELOW the root so a
        // project living under e.g. ~/.dotfiles/repo isn't wholesale excluded
        // because of an ancestor that the user has no control over. The check
        // is OS-aware: case-insensitive on Windows/macOS, case-sensitive on
        // Linux (matches the filesystem semantics that produced the path).
        var pathToScan = normalizedPath;
        if (normalizedRoot is not null)
        {
            var rootCmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (normalizedPath.StartsWith(normalizedRoot, rootCmp))
                pathToScan = normalizedPath[normalizedRoot.Length..];
            else
            {
                var trimmedRoot = normalizedRoot.TrimEnd('/');
                if (string.Equals(normalizedPath.TrimEnd('/'), trimmedRoot, rootCmp))
                    pathToScan = string.Empty;
            }
        }

        if (pathToScan.Length == 0) return false;

        return pathToScan.Split('/').Any(part =>
            part.Length > 1 && part[0] == '.' && part is not ".github" and not ".vscode-test");
    }

    private static bool IsExcludedFile(string fileName, string ext)
    {
        foreach (var pattern in ExcludedFilePatterns)
        {
            if (string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.StartsWith(pattern + ".", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return ExcludedFileExtensions.Any(blockedExt =>
            string.Equals(ext, blockedExt, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSupportedExtension(string ext) =>
        Array.IndexOf(SupportedExtensions, ext) >= 0;

    private static bool MatchesSimpleGlob(string fileName, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            var ext = pattern[1..];
            return fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.Contains('*'))
        {
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(fileName, regex,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return fileName.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
