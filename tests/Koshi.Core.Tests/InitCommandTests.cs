using System.Text.Json.Nodes;
using Koshi.Mcp.Cli;
using Koshi.Mcp.Cli.Setup;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// End-to-end tests for <see cref="InitCommand"/>. Every test runs against
/// temp home/appData/cwd directories so the real user's MCP configs are
/// never touched. A <see cref="GitClientTests.FakeGitRunner"/> stands in for
/// the real <c>git</c> executable.
/// </summary>
public sealed class InitCommandTests
{
    [Fact]
    public void TryParseFlags_accepts_known_flags()
    {
        var ok = InitCommand.TryParseFlags(
            ["--client", "copilot", "--skip-team", "--project-root", "/tmp/a"],
            out var opts, out var err);
        Assert.True(ok);
        Assert.Null(err);
        Assert.Single(opts.Clients!, "copilot");
        Assert.True(opts.SkipTeam);
        Assert.Equal("/tmp/a", opts.ProjectRoot);
    }

    [Fact]
    public void TryParseFlags_accepts_equals_syntax()
    {
        var ok = InitCommand.TryParseFlags(
            ["--client=claude", "--project-root=/tmp/b"], out var opts, out _);
        Assert.True(ok);
        Assert.Single(opts.Clients!, "claude");
        Assert.Equal("/tmp/b", opts.ProjectRoot);
    }

    [Fact]
    public void TryParseFlags_rejects_unknown()
    {
        var ok = InitCommand.TryParseFlags(["--turbo"], out _, out var err);
        Assert.False(ok);
        Assert.Contains("--turbo", err);
    }

    [Fact]
    public void TryParseFlags_rejects_missing_value()
    {
        var ok = InitCommand.TryParseFlags(["--client"], out _, out var err);
        Assert.False(ok);
        Assert.Contains("--client requires a value", err);
    }

    [Fact]
    public void TryResolveVaultPath_accepts_relative_inside_project()
    {
        var root = NewTempDir();
        try
        {
            var ok = InitCommand.TryResolveVaultPath(".koshi/vault", root, accept: false,
                out var abs, out var err);
            Assert.True(ok);
            Assert.Null(err);
            Assert.Equal(Path.Combine(root, ".koshi", "vault"),
                abs.TrimEnd(Path.DirectorySeparatorChar));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryResolveVaultPath_rejects_path_escaping_project_root()
    {
        var root = NewTempDir();
        try
        {
            var ok = InitCommand.TryResolveVaultPath("../outside", root, accept: false,
                out _, out var err);
            Assert.False(ok);
            Assert.Contains("outside project root", err);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryResolveVaultPath_rejects_absolute_without_accept_flag()
    {
        var root = NewTempDir();
        try
        {
            var absolute = OperatingSystem.IsWindows() ? "C:\\some\\where" : "/some/where";
            var ok = InitCommand.TryResolveVaultPath(absolute, root, accept: false,
                out _, out var err);
            Assert.False(ok);
            Assert.Contains("absolute", err);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TryResolveVaultPath_accepts_absolute_with_accept_flag()
    {
        var root = NewTempDir();
        try
        {
            var absolute = OperatingSystem.IsWindows() ? "C:\\some\\where" : "/some/where";
            var ok = InitCommand.TryResolveVaultPath(absolute, root, accept: true,
                out var abs, out var err);
            Assert.True(ok);
            Assert.Null(err);
            Assert.Equal(Path.GetFullPath(absolute), abs);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Run_help_prints_and_returns_zero()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var rc = InitCommand.Run(["--help"], stdout, stderr, new StringReader(""),
            homeDir: null, appDataDir: null, cwd: null, gitRunner: new NoopGitRunner());
        Assert.Equal(0, rc);
        Assert.Contains("koshi-mcp init", stdout.ToString());
        Assert.Contains("FLAGS", stdout.ToString());
    }

    [Fact]
    public void Run_bad_flag_returns_2()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var rc = InitCommand.Run(["--nope"], stdout, stderr, new StringReader(""),
            homeDir: null, appDataDir: null, cwd: null, gitRunner: new NoopGitRunner());
        Assert.Equal(2, rc);
        Assert.Contains("--nope", stderr.ToString());
    }

    [Fact]
    public void Run_full_flow_registers_writes_env_and_installs_personas()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git")); // marker

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-team"],
                stdout, stderr, new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: new NoopGitRunner());

            Assert.Equal(0, rc);

            // MCP entry was written into the per-client config (using the temp
            // home so we know we didn't pollute the real user's profile).
            var cfg = Path.Combine(home, ".copilot", "mcp-config.json");
            Assert.True(File.Exists(cfg));
            var node = JsonNode.Parse(File.ReadAllText(cfg))!;
            var koshi = node["mcpServers"]!["koshi"]!.AsObject();
            Assert.Equal("koshi-mcp", (string)koshi["command"]!);
            Assert.Equal("local", (string)koshi["type"]!);
            var env = koshi["env"]!.AsObject();
            Assert.Equal(Path.GetFullPath(cwd), (string)env["KOSHI_PROJECT_ROOT"]!);

            // Personas live under the temp home (user scope).
            var personaDir = Path.Combine(home, ".copilot", "agents");
            Assert.True(Directory.Exists(personaDir));
            Assert.NotEmpty(Directory.GetFiles(personaDir, "*.md"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Run_is_idempotent_with_existing_koshi_entry()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));

            // First run.
            var rc1 = InitCommand.Run(
                ["--client", "copilot", "--skip-team", "--skip-personas"],
                new StringWriter(), new StringWriter(), new StringReader(""),
                home, appData, cwd, new NoopGitRunner());
            Assert.Equal(0, rc1);

            // Second run — should report "already present" but still succeed.
            var stdout = new StringWriter();
            var rc2 = InitCommand.Run(
                ["--client", "copilot", "--skip-team", "--skip-personas"],
                stdout, new StringWriter(), new StringReader(""),
                home, appData, cwd, new NoopGitRunner());
            Assert.Equal(0, rc2);
            Assert.Contains("already present", stdout.ToString());
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Run_with_team_yml_clones_vault_and_registers_team()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));
            File.WriteAllText(Path.Combine(cwd, ".koshi-team.yml"), """
                team:
                  id: alpha
                  name: Alpha Team
                  token_budget: 8000
                vault:
                  repo: gh:my-org/vault
                  path: .koshi/vault
                """);

            var fake = new GitClientTests.FakeGitRunner();

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-personas"],
                stdout, stderr, new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: fake);

            Assert.Equal(0, rc);

            // KOSHI_MEMORY_VAULT was written.
            var cfg = Path.Combine(home, ".copilot", "mcp-config.json");
            var node = JsonNode.Parse(File.ReadAllText(cfg))!;
            var env = node["mcpServers"]!["koshi"]!["env"]!.AsObject();
            Assert.Equal(Path.Combine(cwd, ".koshi", "vault"),
                ((string)env["KOSHI_MEMORY_VAULT"]!).TrimEnd(Path.DirectorySeparatorChar));

            // git clone was invoked with the expanded URL.
            Assert.Contains(fake.Calls, c => c.Args[0] == "clone"
                && c.Args[1] == "https://github.com/my-org/vault.git");

            // Team file was written under <projectRoot>/.koshi/teams.json.
            var teamsFile = Path.Combine(cwd, ".koshi", "teams.json");
            Assert.True(File.Exists(teamsFile));
            var saved = JsonNode.Parse(File.ReadAllText(teamsFile))!;
            var teams = saved["Teams"]!.AsArray();
            Assert.Single(teams);
            Assert.Equal("alpha", (string)teams[0]!["TeamId"]!);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Run_rejects_team_yml_with_escaping_vault_path_unless_accepted()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));
            File.WriteAllText(Path.Combine(cwd, ".koshi-team.yml"), """
                team:
                  id: alpha
                vault:
                  repo: gh:my-org/vault
                  path: ../outside-the-project
                """);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-personas"],
                stdout, stderr, new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: new NoopGitRunner());

            Assert.Equal(4, rc);
            Assert.Contains("outside project root", stderr.ToString());
            Assert.Contains("--accept-team-config", stderr.ToString());
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Run_non_interactive_with_no_detected_client_fails_clearly()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));

            var stderr = new StringWriter();
            var rc = InitCommand.Run(
                ["--non-interactive", "--skip-team", "--skip-personas"],
                new StringWriter(), stderr, new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: new NoopGitRunner());

            Assert.Equal(2, rc);
            Assert.Contains("--non-interactive", stderr.ToString());
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Run_uses_explicit_project_root_when_supplied()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        var explicitRoot = NewTempDir();
        try
        {
            var stdout = new StringWriter();
            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-team", "--skip-personas",
                    "--project-root", explicitRoot],
                stdout, new StringWriter(), new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: new NoopGitRunner());

            Assert.Equal(0, rc);
            Assert.Contains(Path.GetFullPath(explicitRoot), stdout.ToString());
            var cfg = Path.Combine(home, ".copilot", "mcp-config.json");
            var node = JsonNode.Parse(File.ReadAllText(cfg))!;
            var env = node["mcpServers"]!["koshi"]!["env"]!.AsObject();
            Assert.Equal(Path.GetFullPath(explicitRoot), (string)env["KOSHI_PROJECT_ROOT"]!);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
            Directory.Delete(explicitRoot, recursive: true);
        }
    }

    [Fact]
    public void TryParseFlags_recognises_template_flags()
    {
        var ok = InitCommand.TryParseFlags(
            ["--skip-templates", "--force-templates", "--all-templates"], out var opts, out var err);
        Assert.True(ok);
        Assert.Null(err);
        Assert.True(opts.SkipTemplates);
        Assert.True(opts.ForceTemplates);
        Assert.True(opts.AllTemplates);
    }

    [Fact]
    public void Run_installs_steering_templates_into_project_root()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));

            var stdout = new StringWriter();
            // --all-templates forces install of all four regardless of whether
            // the per-client dotfile directories exist in cwd.
            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-team", "--skip-personas", "--all-templates"],
                stdout, new StringWriter(), new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: new NoopGitRunner());

            Assert.Equal(0, rc);
            // All four steering files dropped into the project root.
            Assert.True(File.Exists(Path.Combine(cwd, "AGENTS.md")));
            Assert.True(File.Exists(Path.Combine(cwd, ".github", "copilot-instructions.md")));
            Assert.True(File.Exists(Path.Combine(cwd, ".cursorrules")));
            Assert.True(File.Exists(Path.Combine(cwd, ".windsurfrules")));
            Assert.Contains("==> templates", stdout.ToString());
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Run_default_only_installs_AGENTS_and_detected_client_template()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));

            var stdout = new StringWriter();
            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-team", "--skip-personas"],
                stdout, new StringWriter(), new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: new NoopGitRunner());

            Assert.Equal(0, rc);
            // Universal AGENTS.md → always.
            Assert.True(File.Exists(Path.Combine(cwd, "AGENTS.md")));
            // Copilot was the selected client → its rules file is in.
            Assert.True(File.Exists(Path.Combine(cwd, ".github", "copilot-instructions.md")));
            // Cursor / Windsurf have no presence in the project → skipped.
            Assert.False(File.Exists(Path.Combine(cwd, ".cursorrules")));
            Assert.False(File.Exists(Path.Combine(cwd, ".windsurfrules")));
            Assert.Contains("--all-templates", stdout.ToString());
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Run_default_installs_cursor_template_when_cursor_dir_present()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));
            // Simulate a project that already uses Cursor.
            Directory.CreateDirectory(Path.Combine(cwd, ".cursor"));

            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-team", "--skip-personas"],
                new StringWriter(), new StringWriter(), new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: new NoopGitRunner());

            Assert.Equal(0, rc);
            Assert.True(File.Exists(Path.Combine(cwd, ".cursorrules")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Run_skip_templates_does_not_write_steering_files()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));

            var stdout = new StringWriter();
            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-team", "--skip-personas", "--skip-templates"],
                stdout, new StringWriter(), new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: new NoopGitRunner());

            Assert.Equal(0, rc);
            Assert.False(File.Exists(Path.Combine(cwd, "AGENTS.md")));
            Assert.False(File.Exists(Path.Combine(cwd, ".cursorrules")));
            Assert.False(File.Exists(Path.Combine(cwd, ".windsurfrules")));
            Assert.False(File.Exists(Path.Combine(cwd, ".github", "copilot-instructions.md")));
            Assert.Contains("skipping steering templates", stdout.ToString());
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void TryParseFlags_recognises_git_template_flags()
    {
        var ok = InitCommand.TryParseFlags(
            ["--register-git-template", "--force-git-template"], out var opts, out var err);
        Assert.True(ok);
        Assert.Null(err);
        Assert.True(opts.RegisterGitTemplate);
        Assert.True(opts.ForceGitTemplate);
    }

    [Fact]
    public void TryParseFlags_force_git_template_implies_register()
    {
        // --force-git-template alone should imply --register-git-template;
        // otherwise the flag has no effect.
        var ok = InitCommand.TryParseFlags(
            ["--force-git-template"], out var opts, out _);
        Assert.True(ok);
        Assert.True(opts.RegisterGitTemplate);
        Assert.True(opts.ForceGitTemplate);
    }

    [Fact]
    public void Run_without_register_git_template_does_not_touch_home()
    {
        // The default flow MUST be inert for the per-machine git template:
        // we mutate ~/.gitconfig only when the user opts in.
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-team", "--skip-personas", "--skip-templates"],
                stdout, stderr, new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: new NoopGitRunner());

            Assert.Equal(0, rc);
            Assert.False(Directory.Exists(Path.Combine(home, ".git-template-koshi")));
            Assert.DoesNotContain("==> git template", stdout.ToString());
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Run_register_git_template_installs_dir_hook_and_sets_config()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));
            var fakeGit = new GitClientTests.FakeGitRunner();
            // init.templatedir starts unset (`git config --get` exits 1).
            fakeGit.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(1, string.Empty, string.Empty));

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-team", "--skip-personas",
                 "--skip-templates", "--register-git-template"],
                stdout, stderr, new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: fakeGit);

            Assert.Equal(0, rc);
            var templateDir = Path.Combine(home, ".git-template-koshi");
            Assert.True(Directory.Exists(templateDir));
            Assert.True(File.Exists(Path.Combine(templateDir, "hooks", "post-checkout")));
            Assert.True(File.Exists(Path.Combine(templateDir, "koshi-templates", "AGENTS.md")));
            Assert.True(File.Exists(Path.Combine(templateDir, "koshi-templates", ".cursorrules")));
            Assert.True(File.Exists(Path.Combine(templateDir, "koshi-templates", ".windsurfrules")));
            Assert.True(File.Exists(Path.Combine(templateDir, "koshi-templates", ".github", "copilot-instructions.md")));
            Assert.Contains("git template: installed", stdout.ToString());

            // git config --global init.templatedir <path> should have been called.
            Assert.Contains(fakeGit.Calls, c =>
                c.Args.Length >= 4 && c.Args[0] == "config" && c.Args[1] == "--global"
                && c.Args[2] == "init.templatedir");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Run_register_git_template_refuses_existing_templatedir_without_force()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        var cwd = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(cwd, ".git"));
            var fakeGit = new GitClientTests.FakeGitRunner();
            // init.templatedir already set to a different path → conflict.
            fakeGit.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(0, "/some/other/template" + Environment.NewLine, string.Empty));

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var rc = InitCommand.Run(
                ["--client", "copilot", "--skip-team", "--skip-personas",
                 "--skip-templates", "--register-git-template"],
                stdout, stderr, new StringReader(""),
                homeDir: home, appDataDir: appData, cwd: cwd,
                gitRunner: fakeGit);

            Assert.Equal(1, rc);
            Assert.Contains("init.templatedir is already set", stderr.ToString());
            Assert.Contains("--force-git-template", stderr.ToString());
            // We should NOT have written the dir on a conflict abort.
            Assert.False(Directory.Exists(Path.Combine(home, ".git-template-koshi")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "koshi-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>
    /// Trivial git runner used when a test does not pass a team-yml. Returns
    /// success for every call but is never expected to be invoked.
    /// </summary>
    private sealed class NoopGitRunner : IGitRunner
    {
        public bool IsAvailable() => true;
        public GitResult Run(string workingDir, params string[] args) =>
            new(0, string.Empty, string.Empty);
    }
}
