using System.Runtime.InteropServices;

namespace Koshi.Mcp.Cli.Setup;

/// <summary>
/// Outcome of a single <see cref="GitTemplateInstaller.Install"/> call.
/// </summary>
internal enum GitTemplateOutcome
{
    /// <summary>Template directory + hook + git config were all newly created or updated end-to-end.</summary>
    Installed,
    /// <summary>Template directory already pointed at by <c>init.templatedir</c>; only content was refreshed.</summary>
    Updated,
    /// <summary>Nothing changed: every file already matched the canonical body and <c>init.templatedir</c> already pointed at our dir.</summary>
    AlreadyConfigured,
    /// <summary><c>init.templatedir</c> is set to a path other than ours and <c>--force-git-template</c> was not passed.</summary>
    TemplatedirConflict,
    /// <summary><c>git</c> is not on <c>PATH</c>.</summary>
    GitMissing,
    /// <summary>An <see cref="IOException"/>, permission failure, or non-zero <c>git config</c> exit.</summary>
    Error,
}

internal sealed record GitTemplateResult(
    GitTemplateOutcome Outcome,
    string TemplateDir,
    IReadOnlyList<string> WrittenFiles,
    bool ConfigChanged,
    string? ConflictingTemplatedir = null,
    string? ErrorMessage = null,
    string? Warning = null);

/// <summary>
/// #78 Gap C — opt-in per-machine git template that auto-installs the Koshi
/// Layer-3 steering rule files into every freshly-cloned repo via a
/// <c>post-checkout</c> hook.
///
/// <para>
/// The installer is fully testable: every <c>git</c> call goes through the
/// injected <see cref="IGitRunner"/> and every filesystem path is rooted at
/// the caller-supplied <c>homeDir</c>, so tests never touch the real user's
/// <c>~/.gitconfig</c> or <c>$HOME</c>.
/// </para>
///
/// <para>
/// Mechanism — <c>git config --global init.templatedir &lt;dir&gt;</c> causes
/// every subsequent <c>git init</c> / <c>git clone</c> to seed <c>.git/</c>
/// from <paramref name="DefaultDirName"/>. We ship a single
/// <c>hooks/post-checkout</c> hook that fires on clone (prev-ref all-zeros,
/// branch-flag <c>1</c>) and mirrors the steering files from a peer
/// <c>koshi-templates/</c> directory into the new repo root. The hook
/// self-resolves the template path via <c>$0</c> so a relocated
/// <c>.git-template-koshi</c> directory still works.
/// </para>
///
/// <para>
/// Limits intentionally documented in <see cref="PrintHelp"/> rather than
/// papered over: <c>post-checkout</c> does not fire for <c>git init</c>
/// (no init hook exists in git), bare clones, or <c>git clone --no-checkout</c>;
/// a globally-set <c>core.hooksPath</c> bypasses per-repo hooks.
/// </para>
/// </summary>
internal static class GitTemplateInstaller
{
    /// <summary>Per-user directory name under <c>$HOME</c>.</summary>
    public const string DefaultDirName = ".git-template-koshi";

    /// <summary>Sub-directory inside the template dir that holds the rule-file bodies.</summary>
    public const string ContentDirName = "koshi-templates";

    /// <summary>Hook filename — must be exactly <c>post-checkout</c> for git to honour it.</summary>
    public const string HookFileName = "post-checkout";

    /// <summary>Marker embedded in the generated hook so future <c>koshi-mcp</c> versions can detect-and-upgrade.</summary>
    public const string HookMarker = "koshi-mcp:git-template:v1";

    /// <summary>
    /// Installs (or refreshes) the per-user git template at <c>&lt;homeDir&gt;/.git-template-koshi</c>
    /// and registers it via <c>git config --global init.templatedir</c>.
    /// </summary>
    /// <param name="homeDir">User profile directory. Production callers should pass <c>Environment.GetFolderPath(SpecialFolder.UserProfile)</c>; tests inject a temp dir.</param>
    /// <param name="force">When <c>true</c>, overrides an existing <c>init.templatedir</c> setting that points at a different path; otherwise such a state returns <see cref="GitTemplateOutcome.TemplatedirConflict"/>.</param>
    /// <param name="gitRunner">Abstraction over the <c>git</c> binary so the install path can be exercised without spawning real processes in tests.</param>
    public static GitTemplateResult Install(string homeDir, bool force, IGitRunner gitRunner)
    {
        if (!gitRunner.IsAvailable())
        {
            return new GitTemplateResult(
                GitTemplateOutcome.GitMissing, "", Array.Empty<string>(), ConfigChanged: false,
                ErrorMessage: "git is not on PATH; install git first.");
        }

        var templateDir = Path.GetFullPath(Path.Combine(homeDir, DefaultDirName));
        var hooksDir = Path.Combine(templateDir, "hooks");
        var contentDir = Path.Combine(templateDir, ContentDirName);

        // ── 1. Detect a conflicting pre-existing templatedir ───────────────
        // `git config --global --get` exits 1 when the key is unset (not an
        // error). Any OTHER non-zero exit is a real problem (corrupt config,
        // permissions, bad include) — surface it instead of silently
        // proceeding and overwriting.
        var probe = gitRunner.Run(homeDir, "config", "--global", "--get", "init.templatedir");
        if (!probe.Ok && probe.ExitCode != 1)
        {
            return new GitTemplateResult(
                GitTemplateOutcome.Error, templateDir, Array.Empty<string>(),
                ConfigChanged: false,
                ErrorMessage: $"git config --global --get init.templatedir failed (exit {probe.ExitCode}): {probe.Stderr.Trim()}");
        }
        var currentRaw = probe.Ok ? probe.Stdout.Trim() : string.Empty;
        var currentExpanded = ExpandHome(currentRaw, homeDir);
        var pointsAtUs = !string.IsNullOrEmpty(currentRaw)
            && PathsEqual(currentExpanded, templateDir);
        var conflicts = !string.IsNullOrEmpty(currentRaw) && !pointsAtUs;

        if (conflicts && !force)
        {
            return new GitTemplateResult(
                GitTemplateOutcome.TemplatedirConflict, templateDir,
                Array.Empty<string>(), ConfigChanged: false,
                ConflictingTemplatedir: currentRaw);
        }

        // ── 1b. Warn if core.hooksPath is set globally ─────────────────────
        // A global core.hooksPath overrides per-repo hooks, so our
        // post-checkout will never fire even though install succeeds. We
        // still install (because the user may have an outer hook script
        // that includes us, and they can fix the config later), but we
        // surface a Warning so they're not silently disappointed.
        string? warning = null;
        var hooksProbe = gitRunner.Run(homeDir, "config", "--global", "--get", "core.hooksPath");
        if (hooksProbe.Ok && !string.IsNullOrWhiteSpace(hooksProbe.Stdout))
        {
            warning = $"core.hooksPath is set globally to '{hooksProbe.Stdout.Trim()}'; "
                + "git will use that instead of our per-template hook, so the auto-drop "
                + "on clone will NOT fire. Unset it or include our hook from yours.";
        }

        // ── 2. Mirror the 4 steering templates into <templateDir>/koshi-templates/
        //      and write the hook. WriteIfChanged keeps re-runs noiseless.
        var written = new List<string>();
        bool anyContentChange = false;

        try
        {
            Directory.CreateDirectory(hooksDir);
            Directory.CreateDirectory(contentDir);

            foreach (var t in SteeringTemplateCatalog.Discover())
            {
                var dest = Path.Combine(contentDir, t.DestinationRelative);
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                anyContentChange |= WriteIfChanged(dest, t.Read());
                written.Add(dest);
            }

            var hookPath = Path.Combine(hooksDir, HookFileName);
            // POSIX sh chokes on a CRLF shebang. Always write the hook with
            // LF endings regardless of platform.
            var hookScript = BuildHookScript().Replace("\r\n", "\n");
            anyContentChange |= WriteIfChanged(hookPath, hookScript);
            TrySetExecutable(hookPath);
            written.Add(hookPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GitTemplateResult(
                GitTemplateOutcome.Error, templateDir, written, ConfigChanged: false,
                ErrorMessage: ex.Message);
        }

        // ── 3. Set / update init.templatedir if needed ─────────────────────
        bool configChanged = false;
        if (!pointsAtUs)
        {
            var set = gitRunner.Run(homeDir, "config", "--global",
                "init.templatedir", ToPosix(templateDir));
            if (!set.Ok)
            {
                return new GitTemplateResult(
                    GitTemplateOutcome.Error, templateDir, written, ConfigChanged: false,
                    ErrorMessage: $"git config --global init.templatedir failed (exit {set.ExitCode}): {set.Stderr.Trim()}");
            }
            configChanged = true;
        }

        var outcome = configChanged
            ? GitTemplateOutcome.Installed
            : anyContentChange
                ? GitTemplateOutcome.Updated
                : GitTemplateOutcome.AlreadyConfigured;

        return new GitTemplateResult(outcome, templateDir, written, configChanged, Warning: warning);
    }

    /// <summary>
    /// Build the POSIX-sh <c>post-checkout</c> hook. The hook is intentionally
    /// self-locating (resolves the templates dir via <c>$0</c>) so that even
    /// when git copies the hook into a freshly-cloned repo (as
    /// <c>.git/hooks/post-checkout</c> alongside the seeded
    /// <c>.git/koshi-templates/</c> snapshot), <c>"$HOOK_DIR/../koshi-templates"</c>
    /// still resolves correctly.
    ///
    /// <para>
    /// Security: the hook runs automatically after cloning arbitrary repos,
    /// so we have to assume the cloned working tree is hostile. We refuse
    /// to follow any symlink along the destination path — both the leaf
    /// (<c>[ -L "$dest" ]</c>) and any parent directory under
    /// <c>$REPO_ROOT</c>. Otherwise a malicious repo could ship
    /// <c>AGENTS.md -> /etc/passwd</c> (dangling) or <c>.github -> ..</c>
    /// and trick the hook into writing outside the working tree.
    /// </para>
    /// </summary>
    internal static string BuildHookScript() =>
        "#!/bin/sh\n" +
        "# " + HookMarker + " post-checkout hook\n" +
        "# Fires after `git clone` (prev-ref=all-zeros, branch flag=1) and\n" +
        "# drops the per-ecosystem Koshi steering rules into the new repo\n" +
        "# root when absent. Never overwrites existing files, never follows\n" +
        "# symlinks. Also fires for `git worktree add` — that is intentional\n" +
        "# so linked worktrees get the same steering surface.\n" +
        "# https://github.com/jsharma1105/Koshi — issue #78 Gap C.\n" +
        "if [ \"$1\" != \"0000000000000000000000000000000000000000\" ]; then exit 0; fi\n" +
        "if [ \"$3\" != \"1\" ]; then exit 0; fi\n" +
        "REPO_ROOT=\"$(git rev-parse --show-toplevel 2>/dev/null)\"\n" +
        "[ -z \"$REPO_ROOT\" ] && exit 0\n" +
        "# Skip submodules — a parent repo already owns the steering layout.\n" +
        "SUPER=\"$(git rev-parse --show-superproject-working-tree 2>/dev/null)\"\n" +
        "[ -n \"$SUPER\" ] && exit 0\n" +
        "HOOK_DIR=\"$(CDPATH= cd -- \"$(dirname -- \"$0\")\" && pwd -P)\" || exit 0\n" +
        "SOURCE=\"$HOOK_DIR/../" + ContentDirName + "\"\n" +
        "[ -d \"$SOURCE\" ] || exit 0\n" +
        "( cd \"$SOURCE\" && find . -type f ) | while IFS= read -r rel; do\n" +
        "  rel=\"${rel#./}\"\n" +
        "  dest=\"$REPO_ROOT/$rel\"\n" +
        "  # Skip if dest exists (regular file OR symlink — dangling counts).\n" +
        "  if [ -e \"$dest\" ] || [ -L \"$dest\" ]; then continue; fi\n" +
        "  # Refuse to follow a symlinked parent: a hostile repo could point\n" +
        "  # `.github` at an arbitrary directory outside REPO_ROOT.\n" +
        "  unsafe=0\n" +
        "  check=\"$rel\"\n" +
        "  while [ \"$check\" != \".\" ] && [ \"$check\" != \"/\" ]; do\n" +
        "    if [ -L \"$REPO_ROOT/$check\" ]; then unsafe=1; break; fi\n" +
        "    parent=\"$(dirname \"$check\")\"\n" +
        "    [ \"$parent\" = \"$check\" ] && break\n" +
        "    check=\"$parent\"\n" +
        "  done\n" +
        "  if [ \"$unsafe\" = \"1\" ]; then\n" +
        "    echo \"koshi: skipping $rel (symlinked path under repo root)\" >&2\n" +
        "    continue\n" +
        "  fi\n" +
        "  destdir=\"$(dirname \"$dest\")\"\n" +
        "  mkdir -p \"$destdir\" || { echo \"koshi: failed mkdir $destdir\" >&2; continue; }\n" +
        "  cp \"$SOURCE/$rel\" \"$dest\" || { echo \"koshi: failed cp $rel\" >&2; continue; }\n" +
        "done\n" +
        "exit 0\n";

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            if (string.Equals(existing, content, StringComparison.Ordinal))
                return false;
        }
        File.WriteAllText(path, content);
        return true;
    }

    private static void TrySetExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Best-effort: a file that exists but cannot be chmodded is still
            // usable on a system where the user fixes perms manually.
        }
    }

    internal static string ExpandHome(string value, string homeDir)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value == "~") return homeDir;
        if (value.StartsWith("~/", StringComparison.Ordinal) ||
            value.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(homeDir, value[2..]);
        return value;
    }

    internal static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            var na = Path.GetFullPath(a).TrimEnd('/', '\\');
            var nb = Path.GetFullPath(b).TrimEnd('/', '\\');
            var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(na, nb, cmp);
        }
        catch
        {
            return false;
        }
    }

    private static string ToPosix(string path) => path.Replace('\\', '/');
}
