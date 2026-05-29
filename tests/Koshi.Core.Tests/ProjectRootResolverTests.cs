using Koshi.Mcp.Cli.Setup;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for <see cref="ProjectRootResolver"/> — the walk-up search used by
/// <c>koshi-mcp init</c> to anchor every downstream path (#B2).
/// </summary>
public sealed class ProjectRootResolverTests
{
    [Fact]
    public void Resolve_returns_start_dir_when_marker_lives_there()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Assert.Equal(NormPath(root), NormPath(ProjectRootResolver.Resolve(root)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Resolve_walks_up_to_nearest_ancestor_with_marker()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var nested = Path.Combine(root, "src", "Koshi.Mcp", "Cli");
            Directory.CreateDirectory(nested);
            Assert.Equal(NormPath(root), NormPath(ProjectRootResolver.Resolve(nested)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Resolve_falls_back_to_start_dir_when_no_marker_found()
    {
        // tmp/no-markers/sub - walk up will hit OS temp root before any marker.
        var bare = NewTempDir();
        var sub = Path.Combine(bare, "sub");
        Directory.CreateDirectory(sub);
        try
        {
            var resolved = ProjectRootResolver.Resolve(sub);
            // We can't make a hard assertion that no marker exists higher on the
            // dev machine (CI tmp dirs are usually clean; some dev machines may
            // have ancestral .git). The contract we assert is "Resolve returns
            // a valid absolute path that contains the input as either itself
            // or an ancestor".
            Assert.True(Path.IsPathRooted(resolved));
            Assert.StartsWith(NormPath(resolved), NormPath(sub), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(bare, recursive: true); }
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".koshi-team.yml")]
    [InlineData("package.json")]
    [InlineData("pyproject.toml")]
    [InlineData("go.mod")]
    [InlineData("Cargo.toml")]
    public void Resolve_recognises_each_well_known_marker(string marker)
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, marker);
            if (marker == ".git")
                Directory.CreateDirectory(path);
            else
                File.WriteAllText(path, "stub");

            var nested = Path.Combine(root, "deep", "nested", "dir");
            Directory.CreateDirectory(nested);
            Assert.Equal(NormPath(root), NormPath(ProjectRootResolver.Resolve(nested)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IsInside_accepts_subdirs_and_the_root_itself()
    {
        var root = NewTempDir();
        try
        {
            Assert.True(ProjectRootResolver.IsInside(root, root));
            Assert.True(ProjectRootResolver.IsInside(root, Path.Combine(root, "a")));
            Assert.True(ProjectRootResolver.IsInside(root, Path.Combine(root, "a", "b", "c")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IsInside_rejects_paths_that_escape_the_root()
    {
        var root = NewTempDir();
        try
        {
            var sibling = NewTempDir();
            try
            {
                Assert.False(ProjectRootResolver.IsInside(root, sibling));
                // Same prefix string but different directory (e.g. "/koshi" vs "/koshi-vault").
                var lookalike = root + "-suffix";
                Assert.False(ProjectRootResolver.IsInside(root, lookalike));
            }
            finally { Directory.Delete(sibling, recursive: true); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "koshi-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    // Normalise away trailing separators / casing to make path equality robust
    // across Windows + Linux + nested temp-dir nuances.
    private static string NormPath(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
}
