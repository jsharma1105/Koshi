using Koshi.Mcp.Cli.Setup;

namespace Koshi.Core.Tests;

/// <summary>
/// Unit tests for #78 Gap C: <see cref="GitTemplateInstaller"/>. Every test
/// runs against a fresh temp <c>homeDir</c> and a <see cref="GitClientTests.FakeGitRunner"/>
/// so the real user's git config and HOME are never touched.
/// </summary>
public sealed class GitTemplateInstallerTests
{
    [Fact]
    public void Install_fresh_writes_dir_hook_templates_and_sets_config()
    {
        var home = NewTempDir();
        try
        {
            var git = new GitClientTests.FakeGitRunner();
            // Unset → exit 1, empty stdout. This is the normal git behaviour.
            git.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(1, string.Empty, string.Empty));

            var r = GitTemplateInstaller.Install(home, force: false, git);

            Assert.Equal(GitTemplateOutcome.Installed, r.Outcome);
            Assert.True(r.ConfigChanged);
            Assert.True(Directory.Exists(r.TemplateDir));
            Assert.True(File.Exists(Path.Combine(r.TemplateDir, "hooks", "post-checkout")));
            Assert.True(File.Exists(Path.Combine(r.TemplateDir, "koshi-templates", "AGENTS.md")));
            Assert.True(File.Exists(Path.Combine(r.TemplateDir, "koshi-templates", ".cursorrules")));
            Assert.True(File.Exists(Path.Combine(r.TemplateDir, "koshi-templates", ".windsurfrules")));
            Assert.True(File.Exists(Path.Combine(r.TemplateDir, "koshi-templates", ".github", "copilot-instructions.md")));

            // The set-templatedir call should have happened with a POSIX path.
            var setCall = git.Calls.FirstOrDefault(c =>
                c.Args.Length == 4 && c.Args[0] == "config" && c.Args[1] == "--global"
                && c.Args[2] == "init.templatedir");
            Assert.NotEqual(default, setCall);
            Assert.DoesNotContain('\\', setCall.Args[3]);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Hook_script_has_marker_clone_guard_and_self_locating_source()
    {
        var script = GitTemplateInstaller.BuildHookScript();

        Assert.StartsWith("#!/bin/sh", script);
        Assert.Contains(GitTemplateInstaller.HookMarker, script);
        Assert.Contains("0000000000000000000000000000000000000000", script);
        Assert.Contains("\"$3\" != \"1\"", script);
        Assert.Contains("HOOK_DIR=\"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd -P)\"", script);
        Assert.Contains("SOURCE=\"$HOOK_DIR/../koshi-templates\"", script);
        Assert.Contains("show-superproject-working-tree", script);
        Assert.Contains("if [ -e \"$dest\" ] || [ -L \"$dest\" ]; then continue; fi", script);
    }

    [Fact]
    public void Hook_script_refuses_to_follow_symlinked_parent_directories()
    {
        // Defence against a hostile repo shipping `.github -> ..` etc.
        var script = GitTemplateInstaller.BuildHookScript();
        Assert.Contains("[ -L \"$REPO_ROOT/$check\" ]", script);
        Assert.Contains("symlinked path under repo root", script);
    }

    [Fact]
    public void Install_writes_hook_with_LF_only_endings()
    {
        var home = NewTempDir();
        try
        {
            var git = new GitClientTests.FakeGitRunner();
            git.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(1, string.Empty, string.Empty));

            var r = GitTemplateInstaller.Install(home, force: false, git);
            var hookBytes = File.ReadAllBytes(Path.Combine(r.TemplateDir, "hooks", "post-checkout"));

            // POSIX sh rejects a CRLF shebang. There must be zero \r bytes.
            Assert.DoesNotContain((byte)'\r', hookBytes);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Install_second_run_unchanged_returns_AlreadyConfigured()
    {
        var home = NewTempDir();
        try
        {
            var git1 = new GitClientTests.FakeGitRunner();
            git1.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(1, string.Empty, string.Empty));
            var first = GitTemplateInstaller.Install(home, force: false, git1);
            Assert.Equal(GitTemplateOutcome.Installed, first.Outcome);

            // Second runner: pretend the previous Install successfully set
            // init.templatedir at our exact path.
            var git2 = new GitClientTests.FakeGitRunner();
            git2.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(0, first.TemplateDir.Replace('\\', '/') + Environment.NewLine, string.Empty));

            var second = GitTemplateInstaller.Install(home, force: false, git2);

            Assert.Equal(GitTemplateOutcome.AlreadyConfigured, second.Outcome);
            Assert.False(second.ConfigChanged);
            // No second `config --global init.templatedir <path>` call.
            Assert.DoesNotContain(git2.Calls, c =>
                c.Args.Length == 4 && c.Args[0] == "config" && c.Args[1] == "--global"
                && c.Args[2] == "init.templatedir");
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Install_returns_TemplatedirConflict_when_set_to_other_path_without_force()
    {
        var home = NewTempDir();
        try
        {
            var git = new GitClientTests.FakeGitRunner();
            git.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(0, "/some/other/template" + Environment.NewLine, string.Empty));

            var r = GitTemplateInstaller.Install(home, force: false, git);

            Assert.Equal(GitTemplateOutcome.TemplatedirConflict, r.Outcome);
            Assert.Equal("/some/other/template", r.ConflictingTemplatedir);
            Assert.False(r.ConfigChanged);
            // CRITICAL: a conflict must NOT have written the template dir.
            Assert.False(Directory.Exists(r.TemplateDir));
            // And must NOT have called `config --global init.templatedir`.
            Assert.DoesNotContain(git.Calls, c =>
                c.Args.Length == 4 && c.Args[0] == "config" && c.Args[1] == "--global"
                && c.Args[2] == "init.templatedir");
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Install_force_overrides_existing_templatedir_setting()
    {
        var home = NewTempDir();
        try
        {
            var git = new GitClientTests.FakeGitRunner();
            git.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(0, "/some/other/template" + Environment.NewLine, string.Empty));

            var r = GitTemplateInstaller.Install(home, force: true, git);

            Assert.Equal(GitTemplateOutcome.Installed, r.Outcome);
            Assert.True(r.ConfigChanged);
            Assert.Contains(git.Calls, c =>
                c.Args.Length == 4 && c.Args[0] == "config" && c.Args[1] == "--global"
                && c.Args[2] == "init.templatedir");
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Install_returns_GitMissing_when_git_is_not_available()
    {
        var home = NewTempDir();
        try
        {
            var git = new GitClientTests.FakeGitRunner { Available = false };
            var r = GitTemplateInstaller.Install(home, force: false, git);

            Assert.Equal(GitTemplateOutcome.GitMissing, r.Outcome);
            Assert.False(r.ConfigChanged);
            Assert.False(Directory.Exists(Path.Combine(home, ".git-template-koshi")));
            Assert.Empty(git.Calls);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Install_returns_Error_when_git_config_set_fails()
    {
        var home = NewTempDir();
        try
        {
            var git = new GitClientTests.FakeGitRunner();
            git.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(1, string.Empty, string.Empty));
            // The actual templatedir set will be a 4-arg call with the resolved
            // path as arg 4; we cannot pre-key it by 4-tuple because we don't
            // know the path. So we extend FakeGitRunner via a real-runtime
            // shim: any call with args[2]=="init.templatedir" returns failure.
            var failing = new FailingSetGitRunner(git);

            var r = GitTemplateInstaller.Install(home, force: false, failing);

            Assert.Equal(GitTemplateOutcome.Error, r.Outcome);
            Assert.False(r.ConfigChanged);
            Assert.Contains("init.templatedir failed", r.ErrorMessage);
            // The dir + hook were written before the config call, which is
            // acceptable — re-running succeeds and is idempotent.
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Install_idempotent_does_not_rewrite_unchanged_template_files()
    {
        var home = NewTempDir();
        try
        {
            var git1 = new GitClientTests.FakeGitRunner();
            git1.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(1, string.Empty, string.Empty));
            var first = GitTemplateInstaller.Install(home, force: false, git1);

            var agentsMd = Path.Combine(first.TemplateDir, "koshi-templates", "AGENTS.md");
            var beforeTime = File.GetLastWriteTimeUtc(agentsMd);
            // Spread by a measurable delta. NTFS LastWriteTime resolution can
            // be ~100ns but VFAT is 2s — 250ms is safe across all.
            System.Threading.Thread.Sleep(250);

            var git2 = new GitClientTests.FakeGitRunner();
            git2.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(0, first.TemplateDir.Replace('\\', '/') + Environment.NewLine, string.Empty));
            GitTemplateInstaller.Install(home, force: false, git2);

            // WriteIfChanged should NOT have touched the file.
            Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(agentsMd));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void PathsEqual_handles_tilde_expanded_paths()
    {
        var home = NewTempDir();
        try
        {
            var expanded = GitTemplateInstaller.ExpandHome("~/.git-template-koshi", home);
            var direct = Path.Combine(home, ".git-template-koshi");
            Assert.True(GitTemplateInstaller.PathsEqual(expanded, direct));
            Assert.True(GitTemplateInstaller.PathsEqual(direct + "/", direct));
            Assert.False(GitTemplateInstaller.PathsEqual(direct, Path.Combine(home, "elsewhere")));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Install_recognises_existing_templatedir_pointing_at_us_via_tilde()
    {
        var home = NewTempDir();
        try
        {
            var git1 = new GitClientTests.FakeGitRunner();
            git1.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(1, string.Empty, string.Empty));
            GitTemplateInstaller.Install(home, force: false, git1);

            // Now simulate git returning the tilde-form (some users hand-edit
            // their gitconfig with `~/...`).
            var git2 = new GitClientTests.FakeGitRunner();
            git2.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(0, "~/.git-template-koshi" + Environment.NewLine, string.Empty));

            var r = GitTemplateInstaller.Install(home, force: false, git2);

            // Must NOT be flagged as a conflict — tilde-expansion resolves to us.
            Assert.NotEqual(GitTemplateOutcome.TemplatedirConflict, r.Outcome);
            Assert.False(r.ConfigChanged);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Install_returns_Error_when_get_probe_fails_with_unexpected_exit_code()
    {
        // Exit code 1 from `git config --get` means "unset". Any OTHER non-zero
        // (e.g. 128 from corrupt config / permissions) must NOT be silently
        // treated as unset; we should surface it.
        var home = NewTempDir();
        try
        {
            var git = new GitClientTests.FakeGitRunner();
            git.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(128, string.Empty, "fatal: bad config line in /home/user/.gitconfig"));

            var r = GitTemplateInstaller.Install(home, force: false, git);

            Assert.Equal(GitTemplateOutcome.Error, r.Outcome);
            Assert.Contains("exit 128", r.ErrorMessage);
            Assert.False(r.ConfigChanged);
            Assert.False(Directory.Exists(Path.Combine(home, ".git-template-koshi")));
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Install_surfaces_warning_when_core_hooksPath_is_set_globally()
    {
        var home = NewTempDir();
        try
        {
            var git = new GitClientTests.FakeGitRunner();
            git.OnArgs(("config", "--global", "--get", "init.templatedir"),
                new GitResult(1, string.Empty, string.Empty));
            git.OnArgs(("config", "--global", "--get", "core.hooksPath"),
                new GitResult(0, "/etc/git/hooks" + Environment.NewLine, string.Empty));

            var r = GitTemplateInstaller.Install(home, force: false, git);

            // The install still proceeds (the user may have a composing hook),
            // but the warning must be present so the user is not silently
            // disappointed when the hook never fires.
            Assert.Equal(GitTemplateOutcome.Installed, r.Outcome);
            Assert.NotNull(r.Warning);
            Assert.Contains("core.hooksPath", r.Warning);
            Assert.Contains("/etc/git/hooks", r.Warning);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    private static string NewTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "koshi-gittpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>
    /// Wraps a <see cref="GitClientTests.FakeGitRunner"/> but fails any
    /// <c>config --global init.templatedir &lt;path&gt;</c> set-call. Used to
    /// exercise the Error-on-config-set-failed path without baking the
    /// resolved templatedir path into a test fixture.
    /// </summary>
    private sealed class FailingSetGitRunner : IGitRunner
    {
        private readonly GitClientTests.FakeGitRunner _inner;
        public FailingSetGitRunner(GitClientTests.FakeGitRunner inner) { _inner = inner; }
        public bool IsAvailable() => _inner.IsAvailable();
        public GitResult Run(string workingDir, params string[] args)
        {
            if (args.Length == 4 && args[0] == "config" && args[1] == "--global"
                && args[2] == "init.templatedir")
            {
                return new GitResult(128, string.Empty, "could not lock gitconfig");
            }
            return _inner.Run(workingDir, args);
        }
    }
}
