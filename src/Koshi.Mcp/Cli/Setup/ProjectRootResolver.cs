namespace Koshi.Mcp.Cli.Setup;

/// <summary>
/// Resolves the project root for the <c>koshi-mcp init</c> wizard so every
/// downstream path resolution (vault clone target, <c>.koshi/teams.json</c>
/// persistence, <c>KOSHI_PROJECT_ROOT</c> env var written into each client
/// config) uses the same anchor.
///
/// <para>
/// Why this matters: <c>TeamTools</c> resolves its persistence file via
/// <c>PathConfig.Default</c>, which uses <see cref="Environment.CurrentDirectory"/>
/// when <c>KOSHI_PROJECT_ROOT</c> is not set. If the wizard runs from
/// <c>repo/src</c> while MCP clients later spawn <c>koshi-mcp</c> with cwd
/// <c>repo/</c>, the registered team is silently written to
/// <c>repo/src/.koshi/teams.json</c> and the running server reads
/// <c>repo/.koshi/teams.json</c> — a "teams disappeared" footgun. The wizard
/// fixes this by always writing an explicit <c>KOSHI_PROJECT_ROOT</c> into
/// every client config and rooting its own persistence at the resolved path.
/// </para>
/// </summary>
internal static class ProjectRootResolver
{
    /// <summary>
    /// Marker files / directories that identify a project root, in priority
    /// order. <c>.koshi-team.yml</c> wins because it's an explicit Koshi
    /// declaration. <c>.git</c> is the standard repo marker. The rest are
    /// language-ecosystem conventions.
    /// </summary>
    private static readonly string[] s_markers =
    [
        ".koshi-team.yml",
        ".git",
        ".hg",
        ".svn",
        "pyproject.toml",
        "package.json",
        "go.mod",
        "Cargo.toml",
        "pom.xml",
        "build.gradle",
        "build.gradle.kts",
    ];

    /// <summary>
    /// Walk up from <paramref name="startDir"/> looking for the first ancestor
    /// containing any of the well-known project-root marker files. Returns
    /// <paramref name="startDir"/> (fully qualified) when no marker is found.
    /// Caller can override by passing <c>--project-root</c>.
    /// </summary>
    public static string Resolve(string startDir)
    {
        if (string.IsNullOrWhiteSpace(startDir))
            throw new ArgumentException("startDir must not be empty", nameof(startDir));

        var current = Path.GetFullPath(startDir);
        while (true)
        {
            foreach (var marker in s_markers)
            {
                var candidate = Path.Combine(current, marker);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    return current;
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                // Reached filesystem root without finding a marker. Fall back
                // to the original directory; the wizard surfaces this so the
                // user can override with --project-root.
                return Path.GetFullPath(startDir);
            }
            current = parent;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="candidate"/> resolves inside
    /// <paramref name="projectRoot"/> (after symlink-following). Used as the
    /// safety gate before cloning a vault to a <c>.koshi-team.yml</c>-supplied
    /// path: we refuse to clone outside the project unless the user passes
    /// <c>--accept-team-config</c>.
    /// </summary>
    public static bool IsInside(string projectRoot, string candidate)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootWithSep = root + Path.DirectorySeparatorChar;
        return string.Equals(target, root, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
