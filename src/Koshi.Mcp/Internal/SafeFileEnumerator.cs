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
        foreach (var file in Directory.EnumerateFiles(rootPath, "*", enumerationOptions))
        {
            if (yielded >= maxFiles) yield break;

            var normalized = file.Replace('\\', '/');
            var fileName = Path.GetFileName(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();

            if (IsExcludedDirectory(normalized)) continue;
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
            catch
            {
                continue;
            }

            if (length == 0 || length > maxBytes) continue;

            yielded++;
            yield return file;
        }
    }

    private static bool IsExcludedDirectory(string normalizedPath)
    {
        foreach (var seg in ExcludedDirectorySegments)
        {
            if (normalizedPath.Contains(seg, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // Exclude any path segment that starts with '.' (hidden dir convention)
        // except for a small allow-list of conventional, safe directories.
        foreach (var part in normalizedPath.Split('/'))
        {
            if (part.Length > 1 && part[0] == '.' && part is not ".github" and not ".vscode-test")
                return true;
        }
        return false;
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

        foreach (var blockedExt in ExcludedFileExtensions)
        {
            if (string.Equals(ext, blockedExt, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
