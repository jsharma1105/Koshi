using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// Regressions for the <see cref="SafeFileEnumerator"/> filter rules.
/// Most of these are guard-rails introduced during the multi-model deep
/// review (finding O9): when a project lives <em>under</em> a hidden
/// ancestor directory (e.g. <c>~/.dotfiles/koshi</c>), files inside the
/// project still need to index — only hidden segments BELOW the supplied
/// root should be excluded.
/// </summary>
public sealed class SafeFileEnumeratorTests
{
    [Fact]
    public void IsPathLikelyIndexed_excludes_dot_segment_below_root()
    {
        // /tmp/proj/.cache/foo.cs MUST be excluded relative to /tmp/proj.
        var root = "/tmp/proj";
        var path = "/tmp/proj/.cache/foo.cs";
        Assert.False(SafeFileEnumerator.IsPathLikelyIndexed(path, globPattern: null, rootPath: root));
    }

    [Fact]
    public void IsPathLikelyIndexed_keeps_files_when_dot_segment_is_an_ancestor_of_root()
    {
        // Project lives under ~/.dotfiles/repo. Files under /repo/src/...
        // MUST still be indexable — the dot segment is the user's home
        // structure, not a project choice.
        var root = "/home/u/.dotfiles/repo";
        var path = "/home/u/.dotfiles/repo/src/Program.cs";
        Assert.True(SafeFileEnumerator.IsPathLikelyIndexed(path, globPattern: null, rootPath: root));
    }

    [Fact]
    public void IsPathLikelyIndexed_without_root_still_excludes_dot_segments()
    {
        // Backwards-compat: when no root is passed, fall back to the old
        // any-segment-starts-with-dot rule so old callers don't regress.
        var path = "/home/u/.dotfiles/repo/src/Program.cs";
        Assert.False(SafeFileEnumerator.IsPathLikelyIndexed(path, globPattern: null));
    }

    [Fact]
    public void IsPathLikelyIndexed_keeps_well_known_dot_directories_even_below_root()
    {
        // The conventional allow-list (currently includes .github) is
        // preserved when the segment is below the root.
        var root = "/tmp/proj";
        var path = "/tmp/proj/.github/workflows/ci.yml";
        Assert.True(SafeFileEnumerator.IsPathLikelyIndexed(path, globPattern: null, rootPath: root));
    }
}
