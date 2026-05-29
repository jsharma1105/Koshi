using System.Security;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Centralised resolution for every <c>KOSHI_*</c> path environment variable.
///
/// <para>
/// Koshi resolves a single <b>project root</b> at startup — either from
/// <c>KOSHI_PROJECT_ROOT</c> (when set), or from <see cref="Environment.CurrentDirectory"/>
/// (the directory the MCP server was launched in). Every other path env var
/// then derives a default from this root when unset, and resolves against
/// this root when set to a relative value.
/// </para>
///
/// <para>
/// Behaviour summary (env var → resolved value):
/// </para>
///
/// <list type="table">
///   <listheader>
///     <term>Env var</term>
///     <description>Unset / empty</description>
///   </listheader>
///   <item>
///     <term><c>KOSHI_PROJECT_ROOT</c></term>
///     <description><see cref="Environment.CurrentDirectory"/> (absolute).</description>
///   </item>
///   <item>
///     <term><c>KOSHI_INDEX_PATH</c></term>
///     <description><c>&lt;root&gt;</c> — the directory that <c>koshi_search</c>
///     <em>would</em> auto-index, AND the default target for
///     <c>koshi_index_directory()</c> when no <c>path</c> argument is supplied.
///     Auto-index on first <c>koshi_search</c> remains <em>opt-in</em> — it only
///     fires when <c>KOSHI_INDEX_PATH</c> is set explicitly by the user.</description>
///   </item>
///   <item>
///     <term><c>KOSHI_INDEX_FILE</c></term>
///     <description><c>&lt;root&gt;/.koshi/index.json</c></description>
///   </item>
///   <item>
///     <term><c>KOSHI_MEMORY_FILE</c></term>
///     <description><c>&lt;root&gt;/.koshi/memory.json</c></description>
///   </item>
///   <item>
///     <term><c>KOSHI_MEMORY_VAULT</c></term>
///     <description><c>null</c> — vault remains opt-in.</description>
///   </item>
/// </list>
///
/// <para>
/// When any of these env vars is set:
/// </para>
/// <list type="bullet">
///   <item>An absolute path is used as-is (normalised through <see cref="Path.GetFullPath(string)"/>).</item>
///   <item>A relative path is resolved against the project root, so
///         <c>KOSHI_MEMORY_FILE=.koshi/team-memory.json</c> just works inside
///         the project regardless of the actual cwd.</item>
///   <item>An empty or whitespace value is treated as unset (legacy parity).</item>
/// </list>
///
/// <para>
/// Instances are immutable and cheap; the production singleton is
/// <see cref="Default"/>. Tests construct their own with an injected
/// <see cref="Func{T, TResult}"/> reader to avoid mutating process env vars.
/// </para>
/// </summary>
internal sealed class PathConfig
{
    /// <summary>The single instance used by the running MCP server.</summary>
    ///
    /// <remarks>
    /// Built once at type-init from the live process environment. If a path
    /// env var holds a value that <see cref="Path.GetFullPath(string)"/>
    /// rejects (e.g. invalid characters on Windows), the constructor emits
    /// a stderr diagnostic and falls back to the cwd-derived default for
    /// that single var so the server can still start.
    /// </remarks>
    public static PathConfig Default { get; } = BuildSafeDefault();

    private static PathConfig BuildSafeDefault()
    {
        try { return new PathConfig(); }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or UnauthorizedAccessException
                or SecurityException or NotSupportedException or InvalidOperationException
                or PathTooLongException or FormatException)
        {
            Console.Error.WriteLine(
                $"[koshi] WARN: PathConfig defaults failed to initialise ({ex.GetType().Name}: {ex.Message}). " +
                $"Falling back to cwd-only defaults; check your KOSHI_* env vars for invalid paths.");
            // Fall back to a config that ignores all env vars except cwd —
            // guarantees the server starts even with malformed env.
            return new PathConfig(_ => null);
        }
    }

    /// <summary>Absolute, normalised project root.</summary>
    public string ProjectRoot { get; }

    /// <summary>True iff <c>KOSHI_PROJECT_ROOT</c> supplied the root explicitly.</summary>
    public bool ProjectRootFromEnv { get; }

    /// <summary>Absolute directory to auto-index on first <c>koshi_search</c>.</summary>
    public string IndexPath { get; }

    /// <summary>True iff <c>KOSHI_INDEX_PATH</c> was set; false when defaulted.</summary>
    public bool IndexPathFromEnv { get; }

    /// <summary>Absolute path of the BM25 snapshot file.</summary>
    public string IndexFile { get; }

    /// <summary>True iff <c>KOSHI_INDEX_FILE</c> was set; false when defaulted.</summary>
    public bool IndexFileFromEnv { get; }

    /// <summary>Absolute path of the JSON memory file (used when the vault is not active).</summary>
    public string MemoryFile { get; }

    /// <summary>True iff <c>KOSHI_MEMORY_FILE</c> was set; false when defaulted.</summary>
    public bool MemoryFileFromEnv { get; }

    /// <summary>
    /// Absolute path of the JSON team-registry file (teams, per-turn scores, feedback).
    /// Defaults to <c>&lt;root&gt;/.koshi/teams.json</c>.
    /// </summary>
    public string TeamsFile { get; }

    /// <summary>True iff <c>KOSHI_TEAMS_FILE</c> was set; false when defaulted.</summary>
    public bool TeamsFileFromEnv { get; }

    /// <summary>
    /// Absolute path of the Markdown memory vault root, or <c>null</c> when
    /// <c>KOSHI_MEMORY_VAULT</c> is unset. Vault mode is opt-in by design.
    /// </summary>
    public string? MemoryVault { get; }

    /// <summary>True iff <c>KOSHI_MEMORY_VAULT</c> was set.</summary>
    public bool MemoryVaultFromEnv => MemoryVault is not null;

    /// <summary>
    /// Default constructor — reads from the process environment via
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// </summary>
    public PathConfig() : this(envReader: null) { }

    /// <summary>
    /// Test-friendly constructor — pass an <paramref name="envReader"/> that
    /// returns the env-var value (or null when unset). The current working
    /// directory is only consulted via <see cref="Environment.CurrentDirectory"/>
    /// when <c>KOSHI_PROJECT_ROOT</c> is unset.
    /// </summary>
    public PathConfig(Func<string, string?>? envReader)
    {
        envReader ??= Environment.GetEnvironmentVariable;

        var explicitRoot = envReader("KOSHI_PROJECT_ROOT");
        if (IsSet(explicitRoot))
        {
            ProjectRoot = Path.GetFullPath(explicitRoot!);
            ProjectRootFromEnv = true;
        }
        else
        {
            ProjectRoot = Path.GetFullPath(Environment.CurrentDirectory);
            ProjectRootFromEnv = false;
        }

        (IndexPath, IndexPathFromEnv) = Resolve(
            envReader("KOSHI_INDEX_PATH"),
            defaultValue: ProjectRoot);

        (IndexFile, IndexFileFromEnv) = Resolve(
            envReader("KOSHI_INDEX_FILE"),
            defaultValue: Path.Join(ProjectRoot, ".koshi", "index.json"));

        (MemoryFile, MemoryFileFromEnv) = Resolve(
            envReader("KOSHI_MEMORY_FILE"),
            defaultValue: Path.Join(ProjectRoot, ".koshi", "memory.json"));

        (TeamsFile, TeamsFileFromEnv) = Resolve(
            envReader("KOSHI_TEAMS_FILE"),
            defaultValue: Path.Join(ProjectRoot, ".koshi", "teams.json"));

        // Vault stays opt-in — no default. Setting any non-empty value enables
        // vault mode; relative paths resolve against the project root.
        var rawVault = envReader("KOSHI_MEMORY_VAULT");
        MemoryVault = IsSet(rawVault) ? ResolveAgainstRoot(rawVault!) : null;
    }

    /// <summary>
    /// Convenience: report the source of a resolved path for diagnostics output.
    /// </summary>
    public static string SourceLabel(bool fromEnv) => fromEnv ? "env" : "default";

    /// <summary>
    /// Resolve a user-supplied path string (typically a tool argument like
    /// <c>vaultPath</c> or the <c>path</c> arg of <c>koshi_index_directory</c>)
    /// against the project root.
    /// <list type="bullet">
    ///   <item>Null/whitespace → null (caller decides what to do with that).</item>
    ///   <item>Absolute → normalised as-is.</item>
    ///   <item>Relative → joined to <see cref="ProjectRoot"/> and normalised.</item>
    /// </list>
    /// </summary>
    public string? ResolveUserPath(string? raw)
    {
        if (!IsSet(raw)) return null;
        return ResolveAgainstRoot(raw!);
    }

    private (string path, bool fromEnv) Resolve(string? raw, string defaultValue)
    {
        if (!IsSet(raw))
            return (Path.GetFullPath(defaultValue), false);
        return (ResolveAgainstRoot(raw!), true);
    }

    private string ResolveAgainstRoot(string raw)
    {
        // Use IsPathFullyQualified rather than IsPathRooted. On Windows,
        // "rooted" includes drive-relative ("C:foo") and root-relative
        // ("\foo") forms, both of which silently resolve against the
        // current drive/cwd — NOT against KOSHI_PROJECT_ROOT. Only a
        // fully-qualified path is safe to use as-is.
        //
        // For everything else we use Path.Join + Path.GetFullPath instead
        // of Path.Combine, because Path.Combine preserves the rooting of
        // the second arg and would let "\foo" or "C:foo" escape the
        // project root. Path.Join drops the leading "\" so "\team\idx.json"
        // becomes "<root>\team\idx.json" as the user almost certainly
        // intended; for the pathological "C:foo" case the result contains
        // an embedded colon and will fail loudly at write time — which is
        // exactly what we want for misconfigured input.
        if (Path.IsPathFullyQualified(raw))
            return Path.GetFullPath(raw);
        return Path.GetFullPath(Path.Join(ProjectRoot, raw));
    }

    private static bool IsSet(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Best-effort: when Koshi is writing into a default <c>&lt;root&gt;/.koshi/</c>
    /// state directory (i.e. the user didn't explicitly point us elsewhere),
    /// drop a self-ignoring <c>.gitignore</c> so memory + index data isn't
    /// accidentally committed.
    ///
    /// <para>
    /// The <c>.gitignore</c> we drop ignores everything inside <c>.koshi/</c>
    /// except itself, so power users who actually want to track Koshi state
    /// in Git can edit/remove it and the marker survives Koshi rewrites
    /// (we never overwrite an existing file).
    /// </para>
    ///
    /// <para>
    /// Idempotent and fully best-effort: any IO failure is swallowed with a
    /// single stderr line. Never throws.
    /// </para>
    /// </summary>
    public void EnsureStateDirGitIgnore()
    {
        // Only act when the user is relying on our defaults under .koshi/.
        // If they pointed KOSHI_MEMORY_FILE somewhere else explicitly, hands
        // off — they've made their own commit/ignore decisions.
        var defaultStateDir = Path.Join(ProjectRoot, ".koshi");
        var memoryUsesDefault = !MemoryFileFromEnv &&
            string.Equals(Path.GetDirectoryName(MemoryFile), defaultStateDir, StringComparison.OrdinalIgnoreCase);
        var indexUsesDefault = !IndexFileFromEnv &&
            string.Equals(Path.GetDirectoryName(IndexFile), defaultStateDir, StringComparison.OrdinalIgnoreCase);
        var teamsUsesDefault = !TeamsFileFromEnv &&
            string.Equals(Path.GetDirectoryName(TeamsFile), defaultStateDir, StringComparison.OrdinalIgnoreCase);
        if (!memoryUsesDefault && !indexUsesDefault && !teamsUsesDefault)
            return;

        try
        {
            Directory.CreateDirectory(defaultStateDir);
            var gitignore = Path.Join(defaultStateDir, ".gitignore");
            if (!File.Exists(gitignore))
            {
                File.WriteAllText(gitignore,
                    "# Auto-generated by Koshi. Ignores Koshi state (memory + index)\n" +
                    "# so it isn't committed accidentally. Delete this file if you\n" +
                    "# actually want to track Koshi state in Git.\n" +
                    "*\n" +
                    "!.gitignore\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine(
                $"[koshi] WARN: could not seed .koshi/.gitignore: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
