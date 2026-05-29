using Koshi.Mcp.Cli.Setup;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for <see cref="GitClient"/> — URL normalisation, origin matching, and
/// the clone-or-fast-forward state machine. Uses an in-memory
/// <see cref="FakeGitRunner"/> so no real git processes are spawned.
/// </summary>
public sealed class GitClientTests
{
    [Theory]
    [InlineData("gh:my-org/repo", "https://github.com/my-org/repo.git")]
    [InlineData("gh:owner/name.git", "https://github.com/owner/name.git")]
    [InlineData("https://example.com/repo.git", "https://example.com/repo.git")]
    [InlineData("git@github.com:owner/repo.git", "git@github.com:owner/repo.git")]
    public void ResolveRepoUrl_expands_gh_shorthand_only(string raw, string expected)
    {
        Assert.Equal(expected, GitClient.ResolveRepoUrl(raw));
    }

    [Fact]
    public void ResolveRepoUrl_rejects_invalid_gh_shorthand()
    {
        Assert.Throws<ArgumentException>(() => GitClient.ResolveRepoUrl("gh:not-a-slug"));
        Assert.Throws<ArgumentException>(() => GitClient.ResolveRepoUrl(""));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/owner/repo", true)]
    [InlineData("https://github.com/owner/repo.git", "https://github.com/Owner/REPO/", true)]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo", true)]
    [InlineData("https://github.com/owner/repo", "https://github.com/other/repo", false)]
    public void RemotesMatch_normalises_for_comparison(string a, string b, bool expected)
    {
        Assert.Equal(expected, GitClient.RemotesMatch(a, b));
    }

    [Fact]
    public void EnsureClonedOrFastForward_clones_when_target_missing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "koshi-vault-test-" + Guid.NewGuid().ToString("N"));
        var fake = new FakeGitRunner();
        try
        {
            var r = GitClient.EnsureClonedOrFastForward(fake, dir, "gh:owner/repo");
            Assert.Equal(GitSyncOutcome.Cloned, r.Outcome);
            Assert.Contains(fake.Calls, c => c.Args[0] == "clone"
                && c.Args[1] == "https://github.com/owner/repo.git"
                && c.Args[2] == dir);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EnsureClonedOrFastForward_pulls_when_origin_matches()
    {
        var root = NewTempDir();
        var dir = Path.Combine(root, "vault");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            var fake = new FakeGitRunner();
            fake.OnArgs(("remote", "get-url", "origin"),
                new GitResult(0, "https://github.com/owner/repo.git\n", ""));
            fake.OnArgs(("pull", "--ff-only"),
                new GitResult(0, "Updating abcd..efgh\nFast-forward\n", ""));

            var r = GitClient.EnsureClonedOrFastForward(fake, dir, "gh:owner/repo");
            Assert.Equal(GitSyncOutcome.PulledFastForward, r.Outcome);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EnsureClonedOrFastForward_reports_already_up_to_date()
    {
        var root = NewTempDir();
        var dir = Path.Combine(root, "vault");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            var fake = new FakeGitRunner();
            fake.OnArgs(("remote", "get-url", "origin"),
                new GitResult(0, "https://github.com/owner/repo.git\n", ""));
            fake.OnArgs(("pull", "--ff-only"),
                new GitResult(0, "Already up to date.\n", ""));

            var r = GitClient.EnsureClonedOrFastForward(fake, dir, "gh:owner/repo");
            Assert.Equal(GitSyncOutcome.AlreadyUpToDate, r.Outcome);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EnsureClonedOrFastForward_refuses_to_pull_when_origin_differs()
    {
        var root = NewTempDir();
        var dir = Path.Combine(root, "vault");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            var fake = new FakeGitRunner();
            fake.OnArgs(("remote", "get-url", "origin"),
                new GitResult(0, "https://github.com/SOMEONE-ELSE/different-vault.git\n", ""));

            var r = GitClient.EnsureClonedOrFastForward(fake, dir, "gh:owner/repo");
            Assert.Equal(GitSyncOutcome.OriginMismatch, r.Outcome);
            Assert.Contains("different-vault", r.ActualRemote);
            // Pull must never have been attempted.
            Assert.DoesNotContain(fake.Calls, c => c.Args[0] == "pull");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EnsureClonedOrFastForward_refuses_to_overwrite_non_git_directory()
    {
        var root = NewTempDir();
        var dir = Path.Combine(root, "vault");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "user-file.txt"), "important!");
            var fake = new FakeGitRunner();

            var r = GitClient.EnsureClonedOrFastForward(fake, dir, "gh:owner/repo");
            Assert.Equal(GitSyncOutcome.NotAGitDirectory, r.Outcome);
            Assert.DoesNotContain(fake.Calls, c => c.Args[0] == "clone");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EnsureClonedOrFastForward_clones_into_empty_directory()
    {
        var root = NewTempDir();
        var dir = Path.Combine(root, "vault");
        try
        {
            Directory.CreateDirectory(dir); // exists but empty
            var fake = new FakeGitRunner();

            var r = GitClient.EnsureClonedOrFastForward(fake, dir, "gh:owner/repo");
            Assert.Equal(GitSyncOutcome.Cloned, r.Outcome);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EnsureClonedOrFastForward_returns_GitMissing_when_runner_unavailable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "koshi-vault-" + Guid.NewGuid().ToString("N"));
        var fake = new FakeGitRunner { Available = false };
        var r = GitClient.EnsureClonedOrFastForward(fake, dir, "gh:owner/repo");
        Assert.Equal(GitSyncOutcome.GitMissing, r.Outcome);
    }

    private static string NewTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "koshi-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>
    /// Records every <see cref="IGitRunner.Run"/> call and returns the
    /// queued reply for the matching argument tuple, defaulting to success.
    /// </summary>
    internal sealed class FakeGitRunner : IGitRunner
    {
        public bool Available { get; set; } = true;
        public List<(string Cwd, string[] Args)> Calls { get; } = new();
        private readonly Dictionary<string, GitResult> _scripted = new();

        public bool IsAvailable() => Available;

        public void OnArgs((string, string, string) args, GitResult result)
            => _scripted[Key(args.Item1, args.Item2, args.Item3)] = result;
        public void OnArgs((string, string) args, GitResult result)
            => _scripted[Key(args.Item1, args.Item2)] = result;

        public GitResult Run(string workingDir, params string[] args)
        {
            Calls.Add((workingDir, args));
            var k = Key(args);
            if (_scripted.TryGetValue(k, out var scripted)) return scripted;
            return new GitResult(0, string.Empty, string.Empty);
        }

        private static string Key(params string[] args) => string.Join(' ', args);
    }
}
