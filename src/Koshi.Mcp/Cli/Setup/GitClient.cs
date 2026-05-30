using System.Diagnostics;

namespace Koshi.Mcp.Cli.Setup;

/// <summary>
/// Outcome of one <see cref="IGitRunner"/> invocation. Tests inject a fake
/// runner so the wizard's git contract can be verified without spawning real
/// git processes.
/// </summary>
internal sealed record GitResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Abstraction over the <c>git</c> executable so <see cref="GitClient"/> stays
/// pure and testable. The production implementation
/// (<see cref="ProcessGitRunner"/>) shells out; tests pass a fake.
/// </summary>
internal interface IGitRunner
{
    GitResult Run(string workingDir, params string[] args);
    bool IsAvailable();
}

/// <summary>
/// Real <see cref="IGitRunner"/> backed by <see cref="Process"/>. Captures
/// stdout/stderr fully (vault repos are tiny; bounded output is fine).
/// </summary>
internal sealed class ProcessGitRunner : IGitRunner
{
    public bool IsAvailable()
    {
        try
        {
            var r = Run(Environment.CurrentDirectory, "--version");
            return r.Ok;
        }
        catch (Exception ex) when (
            ex is System.ComponentModel.Win32Exception or
            InvalidOperationException or
            PlatformNotSupportedException or
            IOException)
        {
            return false;
        }
    }

    public GitResult Run(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new GitResult(p.ExitCode, stdout, stderr);
    }
}

/// <summary>
/// Outcome of <see cref="GitClient.EnsureClonedOrFastForward"/>.
/// </summary>
internal enum GitSyncOutcome
{
    Cloned,
    PulledFastForward,
    AlreadyUpToDate,
    GitMissing,
    OriginMismatch,
    NotAGitDirectory,
    NetworkError,
    Conflict,
}

internal sealed record GitSyncResult(
    GitSyncOutcome Outcome,
    string TargetPath,
    string? ExpectedRemote = null,
    string? ActualRemote = null,
    string? Detail = null)
{
    public bool IsError => Outcome is GitSyncOutcome.GitMissing
        or GitSyncOutcome.OriginMismatch
        or GitSyncOutcome.NotAGitDirectory
        or GitSyncOutcome.NetworkError
        or GitSyncOutcome.Conflict;
}

/// <summary>
/// Idempotent vault cloner used by the wizard. When the target does not exist
/// it runs <c>git clone</c>; when it does, it verifies the existing
/// <c>origin</c> matches the expected URL before <c>git pull --ff-only</c>.
/// Refuses to touch a directory whose <c>origin</c> differs (B4 safety) — that
/// surfaces "you re-pointed a teammate's vault at the wrong repo" instead of
/// silently overwriting the user's work.
/// </summary>
internal static class GitClient
{
    /// <summary>
    /// Resolves shorthand vault repo URLs. <c>gh:owner/name</c> →
    /// <c>https://github.com/owner/name.git</c>. <c>https://</c>, <c>http://</c>,
    /// <c>git@</c>, and <c>ssh://</c> URLs pass through unchanged.
    /// </summary>
    public static string ResolveRepoUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("repo URL must not be empty", nameof(raw));

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("gh:", StringComparison.OrdinalIgnoreCase))
        {
            var slug = trimmed["gh:".Length..];
            if (slug.Length == 0 || !slug.Contains('/'))
                throw new ArgumentException(
                    $"'gh:' shorthand requires owner/name, got '{raw}'", nameof(raw));
            if (!slug.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) slug += ".git";
            return $"https://github.com/{slug}";
        }
        return trimmed;
    }

    /// <summary>
    /// Compare two git remote URLs for equivalence, tolerating trailing-slash,
    /// .git-suffix, and case-insensitive scheme/host differences.
    /// </summary>
    public static bool RemotesMatch(string a, string b)
    {
        return Normalize(a) == Normalize(b);

        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var t = s.Trim().TrimEnd('/');
            if (t.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                t = t[..^4];
            // Normalise SSH form `git@github.com:org/repo` to https form so a
            // .koshi-team.yml using gh:org/repo matches a checkout originally
            // cloned over SSH.
            if (t.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            {
                var colon = t.IndexOf(':');
                if (colon > 4)
                {
                    var host = t[4..colon];
                    var path = t[(colon + 1)..];
                    t = $"https://{host}/{path}";
                }
            }
            return t.ToLowerInvariant();
        }
    }

    /// <summary>
    /// Ensure <paramref name="targetPath"/> contains a fresh checkout of
    /// <paramref name="repoUrl"/>. Behaviour:
    /// <list type="bullet">
    ///   <item><b>Target absent</b> → <c>git clone repoUrl targetPath</c>.</item>
    ///   <item><b>Target exists, has <c>.git/</c>, origin matches</b> →
    ///         <c>git -C targetPath pull --ff-only</c>.</item>
    ///   <item><b>Target exists, has <c>.git/</c>, origin differs</b> →
    ///         <see cref="GitSyncOutcome.OriginMismatch"/> (never overwrite).</item>
    ///   <item><b>Target exists, no <c>.git/</c></b> →
    ///         <see cref="GitSyncOutcome.NotAGitDirectory"/>.</item>
    /// </list>
    /// </summary>
    public static GitSyncResult EnsureClonedOrFastForward(
        IGitRunner runner,
        string targetPath,
        string repoUrl)
    {
        if (!runner.IsAvailable())
            return new GitSyncResult(GitSyncOutcome.GitMissing, targetPath,
                Detail: "git not found on PATH; install it from https://git-scm.com/");

        var resolvedRepo = ResolveRepoUrl(repoUrl);
        var gitDir = Path.Join(targetPath, ".git");
        var targetExists = Directory.Exists(targetPath);
        var gitDirExists = Directory.Exists(gitDir) || File.Exists(gitDir); // worktree support

        if (targetExists && !gitDirExists)
        {
            // Existing non-empty directory with no .git/ — refuse rather than
            // overwrite a user's hand-curated tree.
            var entries = Directory.EnumerateFileSystemEntries(targetPath).Any();
            if (entries)
            {
                return new GitSyncResult(GitSyncOutcome.NotAGitDirectory, targetPath,
                    Detail: $"target exists and is not empty, but has no .git/ — refusing to overwrite. Remove or move {targetPath} and re-run.");
            }
            // Empty directory: safe to clone into.
            targetExists = false;
        }

        if (!targetExists)
        {
            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);

            var clone = runner.Run(parent ?? ".", "clone", resolvedRepo, targetPath);
            if (!clone.Ok)
            {
                return new GitSyncResult(GitSyncOutcome.NetworkError, targetPath,
                    ExpectedRemote: resolvedRepo,
                    Detail: TailStderr(clone.Stderr));
            }
            return new GitSyncResult(GitSyncOutcome.Cloned, targetPath, ExpectedRemote: resolvedRepo);
        }

        // Existing checkout — verify origin first.
        var remoteCheck = runner.Run(targetPath, "remote", "get-url", "origin");
        if (!remoteCheck.Ok)
        {
            return new GitSyncResult(GitSyncOutcome.NotAGitDirectory, targetPath,
                ExpectedRemote: resolvedRepo,
                Detail: $"could not read git remote 'origin': {TailStderr(remoteCheck.Stderr)}");
        }
        var actualRemote = remoteCheck.Stdout.Trim();
        if (!RemotesMatch(actualRemote, resolvedRepo))
        {
            return new GitSyncResult(GitSyncOutcome.OriginMismatch, targetPath,
                ExpectedRemote: resolvedRepo,
                ActualRemote: actualRemote,
                Detail:
                    $"existing checkout at {targetPath} points at {actualRemote}, not {resolvedRepo}. " +
                    "Refusing to pull. Move it aside or update .koshi-team.yml.");
        }

        var pull = runner.Run(targetPath, "pull", "--ff-only");
        if (!pull.Ok)
        {
            // git pull --ff-only fails on non-fast-forward; surface as conflict.
            var stderrTail = TailStderr(pull.Stderr);
            var isConflict = stderrTail.Contains("fast-forward", StringComparison.OrdinalIgnoreCase)
                || stderrTail.Contains("diverged", StringComparison.OrdinalIgnoreCase);
            return new GitSyncResult(
                isConflict ? GitSyncOutcome.Conflict : GitSyncOutcome.NetworkError,
                targetPath,
                ExpectedRemote: resolvedRepo,
                Detail: stderrTail);
        }

        return pull.Stdout.Contains("Already up to date", StringComparison.OrdinalIgnoreCase)
            ? new GitSyncResult(GitSyncOutcome.AlreadyUpToDate, targetPath, ExpectedRemote: resolvedRepo)
            : new GitSyncResult(GitSyncOutcome.PulledFastForward, targetPath, ExpectedRemote: resolvedRepo);
    }

    private static string TailStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return string.Empty;
        // Keep the last ~6 lines so the user can see useful context.
        var lines = stderr.Trim().Split('\n');
        if (lines.Length <= 6) return stderr.Trim();
        return string.Join('\n', lines[^6..]).Trim();
    }
}
