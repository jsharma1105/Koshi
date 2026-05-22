using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for <see cref="PathConfig"/> — the v0.6.0 project-root resolver that
/// derives sensible defaults for every <c>KOSHI_*</c> path env var.
///
/// All tests inject a fake env reader so they don't mutate process env vars
/// (parallel-test friendly). Tests that need a real filesystem use a temp
/// project root and clean up afterward.
/// </summary>
public sealed class PathConfigTests
{
    private static Func<string, string?> EmptyEnv() => _ => null;

    private static Func<string, string?> EnvFrom(IDictionary<string, string?> map)
        => name => map.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void Unset_root_falls_back_to_current_directory()
    {
        var cfg = new PathConfig(EmptyEnv());

        Assert.False(cfg.ProjectRootFromEnv);
        Assert.Equal(Path.GetFullPath(Environment.CurrentDirectory), cfg.ProjectRoot);
    }

    [Fact]
    public void Explicit_root_overrides_current_directory()
    {
        var tmp = Path.Join(Path.GetTempPath(), $"koshi-pathconfig-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmp);
            var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
                ["KOSHI_PROJECT_ROOT"] = tmp
            }));

            Assert.True(cfg.ProjectRootFromEnv);
            Assert.Equal(Path.GetFullPath(tmp), cfg.ProjectRoot);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Empty_root_env_treated_as_unset()
    {
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = "   "
        }));

        Assert.False(cfg.ProjectRootFromEnv);
        Assert.Equal(Path.GetFullPath(Environment.CurrentDirectory), cfg.ProjectRoot);
    }

    [Fact]
    public void Index_path_defaults_to_project_root()
    {
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = @"C:\demo\project"
        }));

        Assert.False(cfg.IndexPathFromEnv);
        Assert.Equal(Path.GetFullPath(@"C:\demo\project"), cfg.IndexPath);
    }

    [Fact]
    public void Index_file_defaults_to_dot_koshi_index_json()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\demo\project" : "/demo/project";
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = root
        }));

        Assert.False(cfg.IndexFileFromEnv);
        Assert.Equal(Path.GetFullPath(Path.Join(root, ".koshi", "index.json")), cfg.IndexFile);
    }

    [Fact]
    public void Memory_file_defaults_to_dot_koshi_memory_json()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\demo\project" : "/demo/project";
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = root
        }));

        Assert.False(cfg.MemoryFileFromEnv);
        Assert.Equal(Path.GetFullPath(Path.Join(root, ".koshi", "memory.json")), cfg.MemoryFile);
    }

    [Fact]
    public void Memory_vault_is_null_by_default()
    {
        // Vault stays opt-in. The default for KOSHI_MEMORY_VAULT is *null*,
        // not a derived path — Koshi must not auto-promote a JSON-backend
        // setup into vault mode silently.
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = OperatingSystem.IsWindows() ? @"C:\demo\project" : "/demo/project"
        }));

        Assert.False(cfg.MemoryVaultFromEnv);
        Assert.Null(cfg.MemoryVault);
    }

    [Fact]
    public void Memory_vault_relative_path_resolves_against_root()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\demo\project" : "/demo/project";
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = root,
            ["KOSHI_MEMORY_VAULT"] = "team-vault"
        }));

        Assert.True(cfg.MemoryVaultFromEnv);
        Assert.Equal(Path.GetFullPath(Path.Join(root, "team-vault")), cfg.MemoryVault);
    }

    [Fact]
    public void Absolute_path_in_env_overrides_default()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\demo\project" : "/demo/project";
        var memFile = OperatingSystem.IsWindows()
            ? @"D:\shared\team-memory.json"
            : "/shared/team-memory.json";

        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = root,
            ["KOSHI_MEMORY_FILE"] = memFile
        }));

        Assert.True(cfg.MemoryFileFromEnv);
        Assert.Equal(Path.GetFullPath(memFile), cfg.MemoryFile);
    }

    [Fact]
    public void Relative_path_in_env_resolves_against_root()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\demo\project" : "/demo/project";
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = root,
            ["KOSHI_INDEX_FILE"] = "build/index.json"
        }));

        Assert.True(cfg.IndexFileFromEnv);
        Assert.Equal(Path.GetFullPath(Path.Join(root, "build", "index.json")), cfg.IndexFile);
    }

    [Fact]
    public void Empty_string_env_treated_as_unset_falls_back_to_default()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\demo\project" : "/demo/project";
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = root,
            ["KOSHI_MEMORY_FILE"] = "   "
        }));

        Assert.False(cfg.MemoryFileFromEnv);
        Assert.Equal(Path.GetFullPath(Path.Join(root, ".koshi", "memory.json")), cfg.MemoryFile);
    }

    [Fact]
    public void ResolveUserPath_null_returns_null()
    {
        var cfg = new PathConfig(EmptyEnv());

        Assert.Null(cfg.ResolveUserPath(null));
        Assert.Null(cfg.ResolveUserPath(""));
        Assert.Null(cfg.ResolveUserPath("   "));
    }

    [Fact]
    public void ResolveUserPath_relative_joins_to_root()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\demo\project" : "/demo/project";
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = root
        }));

        Assert.Equal(Path.GetFullPath(Path.Join(root, "src")), cfg.ResolveUserPath("src"));
    }

    [Fact]
    public void ResolveUserPath_absolute_used_as_is()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\demo\project" : "/demo/project";
        var abs = OperatingSystem.IsWindows() ? @"E:\elsewhere" : "/elsewhere";

        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = root
        }));

        Assert.Equal(Path.GetFullPath(abs), cfg.ResolveUserPath(abs));
    }

    [Fact]
    public void Source_label_strings()
    {
        Assert.Equal("env", PathConfig.SourceLabel(fromEnv: true));
        Assert.Equal("default", PathConfig.SourceLabel(fromEnv: false));
    }

    [Fact]
    public void Windows_drive_relative_path_is_NOT_treated_as_absolute()
    {
        // Regression: Path.IsPathRooted("C:foo") returns true on Windows
        // but the path is *drive-relative* — it resolves against the
        // current directory of the C: drive, not against KOSHI_PROJECT_ROOT.
        // PathConfig must use IsPathFullyQualified to dodge this trap.
        if (!OperatingSystem.IsWindows())
            return;

        var root = @"C:\demo\project";
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = root,
            ["KOSHI_MEMORY_FILE"] = @"C:weird-relative-memory.json"
        }));

        // The result must NOT escape the project root. The pathological
        // embedded-colon form is acceptable (will fail at file-write time
        // with a clear OS error — and that's *exactly* what we want for
        // misconfigured input — way better than silently writing somewhere
        // unrelated on the C: drive).
        Assert.True(cfg.MemoryFileFromEnv);
        Assert.StartsWith(root, cfg.MemoryFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_root_relative_path_is_NOT_treated_as_absolute()
    {
        // Regression: Path.IsPathRooted(@"\foo") returns true on Windows
        // but it's root-relative — resolves against the cwd's drive root,
        // not KOSHI_PROJECT_ROOT. IsPathFullyQualified rejects it, and
        // Path.Join (not Path.Combine) drops the leading "\" so the value
        // ends up under the project root as the user expected.
        if (!OperatingSystem.IsWindows())
            return;

        var root = @"C:\demo\project";
        var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
            ["KOSHI_PROJECT_ROOT"] = root,
            ["KOSHI_INDEX_FILE"] = @"\team\index.json"
        }));

        Assert.True(cfg.IndexFileFromEnv);
        Assert.Equal(Path.GetFullPath(Path.Join(root, "team", "index.json")), cfg.IndexFile);
    }

    [Fact]
    public void Default_state_dir_gitignore_is_seeded_when_using_defaults()
    {
        var tmp = Path.Join(Path.GetTempPath(), $"koshi-pc-gitignore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmp);
            var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
                ["KOSHI_PROJECT_ROOT"] = tmp
                // No KOSHI_MEMORY_FILE / KOSHI_INDEX_FILE → defaults under .koshi/
            }));

            cfg.EnsureStateDirGitIgnore();

            var gitignore = Path.Join(tmp, ".koshi", ".gitignore");
            Assert.True(File.Exists(gitignore), $"expected {gitignore} to be created");
            var content = File.ReadAllText(gitignore);
            Assert.Contains("*", content, StringComparison.Ordinal);
            Assert.Contains("!.gitignore", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Existing_gitignore_is_preserved()
    {
        var tmp = Path.Join(Path.GetTempPath(), $"koshi-pc-gitignore-keep-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Join(tmp, ".koshi"));
            var gitignore = Path.Join(tmp, ".koshi", ".gitignore");
            File.WriteAllText(gitignore, "# user-custom\n!memory.json\n");

            var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
                ["KOSHI_PROJECT_ROOT"] = tmp
            }));
            cfg.EnsureStateDirGitIgnore();

            // Existing content untouched.
            Assert.Equal("# user-custom\n!memory.json\n", File.ReadAllText(gitignore));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Gitignore_NOT_seeded_when_user_overrode_paths()
    {
        var tmp = Path.Join(Path.GetTempPath(), $"koshi-pc-gitignore-skip-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmp);
            var externalMem = Path.Join(Path.GetTempPath(), $"koshi-extmem-{Guid.NewGuid():N}.json");
            var externalIdx = Path.Join(Path.GetTempPath(), $"koshi-extidx-{Guid.NewGuid():N}.json");

            var cfg = new PathConfig(EnvFrom(new Dictionary<string, string?> {
                ["KOSHI_PROJECT_ROOT"] = tmp,
                ["KOSHI_MEMORY_FILE"] = externalMem,
                ["KOSHI_INDEX_FILE"] = externalIdx
            }));
            cfg.EnsureStateDirGitIgnore();

            // No default state dir means no auto-gitignore (and no surprise
            // dir creation in a user-explicit setup).
            Assert.False(Directory.Exists(Path.Join(tmp, ".koshi")));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Default_singleton_does_not_throw_on_access()
    {
        // Smoke test: PathConfig.Default reads the real process env vars in
        // its static initializer. This test just verifies no exception is
        // thrown and the resolved paths are absolute.
        Assert.True(Path.IsPathRooted(PathConfig.Default.ProjectRoot));
        Assert.True(Path.IsPathRooted(PathConfig.Default.IndexPath));
        Assert.True(Path.IsPathRooted(PathConfig.Default.IndexFile));
        Assert.True(Path.IsPathRooted(PathConfig.Default.MemoryFile));
    }
}
