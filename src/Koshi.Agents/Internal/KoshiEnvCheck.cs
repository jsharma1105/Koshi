namespace Koshi.Agents.Internal;

/// <summary>
/// Static knowledge about the <c>KOSHI_*</c> path env vars and a doctor-friendly
/// resolver. Mirrors the path-resolution semantics in <c>Koshi.Mcp/Internal/PathConfig.cs</c>
/// (this assembly does not reference Koshi.Mcp internals; the rules are simple enough
/// to duplicate here with a citation).
///
/// <para>
/// Why a whitelist rather than a suffix rule: real Koshi env vars include
/// <c>KOSHI_PROJECT_ROOT</c>, <c>KOSHI_INDEX_PATH</c> (directory), <c>KOSHI_INDEX_FILE</c>
/// (file), <c>KOSHI_MEMORY_FILE</c> (file), <c>KOSHI_MEMORY_VAULT</c> (directory),
/// and <c>KOSHI_TEAMS_FILE</c> (file). A <c>_PATH</c> suffix rule misses three of these.
/// </para>
/// </summary>
internal static class KoshiEnvCheck
{
    public enum PathKind
    {
        Directory,
        File,
    }

    public sealed record KnownVar(string Name, PathKind Kind, bool RequireExists);

    /// <summary>
    /// The full list of <c>KOSHI_*</c> path env vars the doctor knows how to validate.
    /// </summary>
    /// <remarks>
    /// File-typed vars set <see cref="KnownVar.RequireExists"/> = false because
    /// they are written on first use; the doctor reports "ok (parent exists, not yet created)"
    /// in that case rather than flagging a benign missing file.
    /// </remarks>
    public static readonly IReadOnlyList<KnownVar> Known = new[]
    {
        new KnownVar("KOSHI_PROJECT_ROOT", PathKind.Directory, RequireExists: true),
        new KnownVar("KOSHI_INDEX_PATH",   PathKind.Directory, RequireExists: true),
        new KnownVar("KOSHI_MEMORY_VAULT", PathKind.Directory, RequireExists: true),
        new KnownVar("KOSHI_INDEX_FILE",   PathKind.File,      RequireExists: false),
        new KnownVar("KOSHI_MEMORY_FILE",  PathKind.File,      RequireExists: false),
        new KnownVar("KOSHI_TEAMS_FILE",   PathKind.File,      RequireExists: false),
    };

    public enum CheckOutcome
    {
        Ok,
        OkNotYetCreated,
        MissingDirectory,
        MissingFileParent,
        InvalidPath,
    }

    public sealed record CheckResult(
        string Name,
        string RawValue,
        string ResolvedPath,
        PathKind Kind,
        CheckOutcome Outcome,
        string? Detail = null)
    {
        public bool IsProblem => Outcome is CheckOutcome.MissingDirectory
            or CheckOutcome.MissingFileParent
            or CheckOutcome.InvalidPath;
    }

    /// <summary>
    /// Validate every known Koshi env var present in <paramref name="env"/>. Relative
    /// paths are resolved against <c>KOSHI_PROJECT_ROOT</c> when set in the same env block,
    /// else against <paramref name="fallbackRoot"/> (typically the parent process's cwd —
    /// matching how MCP clients spawn the server).
    /// </summary>
    public static IReadOnlyList<CheckResult> CheckAll(
        IReadOnlyDictionary<string, string?> env,
        string fallbackRoot)
    {
        var results = new List<CheckResult>();
        var projectRoot = ResolveProjectRoot(env, fallbackRoot);

        foreach (var known in Known)
        {
            if (!env.TryGetValue(known.Name, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;

            string resolved;
            try
            {
                resolved = ResolveAgainstRoot(raw!, projectRoot);
            }
            catch (Exception ex) when (
                ex is ArgumentException or System.Security.SecurityException or
                NotSupportedException or PathTooLongException)
            {
                results.Add(new CheckResult(known.Name, raw!, raw!, known.Kind,
                    CheckOutcome.InvalidPath, $"{ex.GetType().Name}: {ex.Message}"));
                continue;
            }

            CheckOutcome outcome;
            string? detail = null;
            if (known.Kind == PathKind.Directory)
            {
                outcome = Directory.Exists(resolved) ? CheckOutcome.Ok : CheckOutcome.MissingDirectory;
            }
            else
            {
                if (File.Exists(resolved))
                {
                    outcome = CheckOutcome.Ok;
                }
                else
                {
                    var parent = Path.GetDirectoryName(resolved);
                    if (parent is not null && Directory.Exists(parent))
                    {
                        outcome = CheckOutcome.OkNotYetCreated;
                        detail = "parent dir exists; file will be created on first use";
                    }
                    else
                    {
                        outcome = CheckOutcome.MissingFileParent;
                        detail = $"parent dir does not exist: {parent ?? "(null)"}";
                    }
                }
            }

            results.Add(new CheckResult(known.Name, raw!, resolved, known.Kind, outcome, detail));
        }

        return results;
    }

    private static string ResolveProjectRoot(IReadOnlyDictionary<string, string?> env, string fallbackRoot)
    {
        if (env.TryGetValue("KOSHI_PROJECT_ROOT", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try { return Path.GetFullPath(raw!); }
            catch { /* fall through to fallback */ }
        }
        return Path.GetFullPath(fallbackRoot);
    }

    private static string ResolveAgainstRoot(string raw, string projectRoot)
    {
        // Matches PathConfig.ResolveAgainstRoot semantics:
        // fully-qualified → use as-is; everything else → joined under projectRoot.
        if (Path.IsPathFullyQualified(raw))
            return Path.GetFullPath(raw);
        return Path.GetFullPath(Path.Join(projectRoot, raw));
    }
}
