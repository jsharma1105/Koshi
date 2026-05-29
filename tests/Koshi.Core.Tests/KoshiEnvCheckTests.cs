using Koshi.Mcp.Cli.Setup;

namespace Koshi.Core.Tests;

public class KoshiEnvCheckTests : IDisposable
{
    private readonly string _tmpRoot;

    public KoshiEnvCheckTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "koshi-envcheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Empty_env_returns_no_results()
    {
        var env = new Dictionary<string, string?>();
        var r = KoshiEnvCheck.CheckAll(env, _tmpRoot);
        Assert.Empty(r);
    }

    [Fact]
    public void Whitelist_ignores_unknown_KOSHI_vars()
    {
        var env = new Dictionary<string, string?>
        {
            // Not on the whitelist — must be ignored.
            ["KOSHI_DEBUG"] = "1",
            ["KOSHI_RANDOM_PATH"] = _tmpRoot,
        };
        var r = KoshiEnvCheck.CheckAll(env, _tmpRoot);
        Assert.Empty(r);
    }

    [Fact]
    public void Directory_ok_when_exists()
    {
        var env = new Dictionary<string, string?>
        {
            ["KOSHI_PROJECT_ROOT"] = _tmpRoot,
        };
        var r = KoshiEnvCheck.CheckAll(env, _tmpRoot);
        Assert.Single(r);
        Assert.Equal(KoshiEnvCheck.CheckOutcome.Ok, r[0].Outcome);
        Assert.Equal(KoshiEnvCheck.PathKind.Directory, r[0].Kind);
    }

    [Fact]
    public void Directory_missing_is_flagged()
    {
        var env = new Dictionary<string, string?>
        {
            ["KOSHI_MEMORY_VAULT"] = Path.Combine(_tmpRoot, "does-not-exist"),
        };
        var r = KoshiEnvCheck.CheckAll(env, _tmpRoot);
        Assert.Single(r);
        Assert.Equal(KoshiEnvCheck.CheckOutcome.MissingDirectory, r[0].Outcome);
        Assert.True(r[0].IsProblem);
    }

    [Fact]
    public void File_ok_when_exists()
    {
        var path = Path.Combine(_tmpRoot, "memory.json");
        File.WriteAllText(path, "{}");
        var env = new Dictionary<string, string?>
        {
            ["KOSHI_MEMORY_FILE"] = path,
        };
        var r = KoshiEnvCheck.CheckAll(env, _tmpRoot);
        Assert.Single(r);
        Assert.Equal(KoshiEnvCheck.CheckOutcome.Ok, r[0].Outcome);
    }

    [Fact]
    public void File_not_yet_created_but_parent_exists_is_green()
    {
        var env = new Dictionary<string, string?>
        {
            ["KOSHI_MEMORY_FILE"] = Path.Combine(_tmpRoot, "fresh.json"),
        };
        var r = KoshiEnvCheck.CheckAll(env, _tmpRoot);
        Assert.Single(r);
        Assert.Equal(KoshiEnvCheck.CheckOutcome.OkNotYetCreated, r[0].Outcome);
        Assert.False(r[0].IsProblem);
        Assert.NotNull(r[0].Detail);
    }

    [Fact]
    public void File_with_missing_parent_is_flagged()
    {
        var env = new Dictionary<string, string?>
        {
            ["KOSHI_TEAMS_FILE"] = Path.Combine(_tmpRoot, "nope", "deeper", "teams.json"),
        };
        var r = KoshiEnvCheck.CheckAll(env, _tmpRoot);
        Assert.Single(r);
        Assert.Equal(KoshiEnvCheck.CheckOutcome.MissingFileParent, r[0].Outcome);
        Assert.True(r[0].IsProblem);
    }

    [Fact]
    public void Relative_path_resolves_against_KOSHI_PROJECT_ROOT_in_same_env()
    {
        // Create a subdir under tmpRoot; reference it by RELATIVE path.
        // Resolution should anchor to KOSHI_PROJECT_ROOT, not the test process cwd.
        Directory.CreateDirectory(Path.Combine(_tmpRoot, "subindex"));

        var env = new Dictionary<string, string?>
        {
            ["KOSHI_PROJECT_ROOT"] = _tmpRoot,
            ["KOSHI_INDEX_PATH"] = "subindex",
        };

        var r = KoshiEnvCheck.CheckAll(env, fallbackRoot: Path.GetTempPath());
        var indexPath = r.Single(x => x.Name == "KOSHI_INDEX_PATH");
        Assert.Equal(KoshiEnvCheck.CheckOutcome.Ok, indexPath.Outcome);
        Assert.Equal(Path.GetFullPath(Path.Combine(_tmpRoot, "subindex")), indexPath.ResolvedPath);
    }

    [Fact]
    public void Relative_path_falls_back_to_fallbackRoot_when_no_KOSHI_PROJECT_ROOT()
    {
        Directory.CreateDirectory(Path.Combine(_tmpRoot, "sub"));

        var env = new Dictionary<string, string?>
        {
            ["KOSHI_INDEX_PATH"] = "sub",
        };

        var r = KoshiEnvCheck.CheckAll(env, fallbackRoot: _tmpRoot);
        var indexPath = r.Single(x => x.Name == "KOSHI_INDEX_PATH");
        Assert.Equal(KoshiEnvCheck.CheckOutcome.Ok, indexPath.Outcome);
    }

    [Fact]
    public void Absolute_path_is_used_as_is_even_with_project_root_set()
    {
        var absent = Path.Combine(Path.GetTempPath(), "koshi-absolute-" + Guid.NewGuid().ToString("N")[..8]);
        var env = new Dictionary<string, string?>
        {
            ["KOSHI_PROJECT_ROOT"] = _tmpRoot,
            // Fully-qualified — must not be joined under project root.
            ["KOSHI_MEMORY_VAULT"] = absent,
        };
        var r = KoshiEnvCheck.CheckAll(env, _tmpRoot);
        var vault = r.Single(x => x.Name == "KOSHI_MEMORY_VAULT");
        Assert.Equal(KoshiEnvCheck.CheckOutcome.MissingDirectory, vault.Outcome);
        Assert.Equal(Path.GetFullPath(absent), vault.ResolvedPath);
    }
}
