using System.Globalization;
using System.Text.Json;
using Koshi.Core.Team;
using Koshi.Mcp.Cli.Setup;
using Koshi.Mcp.Internal;

namespace Koshi.Mcp.Cli;

/// <summary>
/// <c>koshi-mcp init</c> — one-command onboarding for a fresh checkout
/// (#68 / #74 Option B). Performs everything a new teammate would otherwise
/// stitch together by hand:
///
/// <list type="number">
///   <item>Resolve the project root (or honour <c>--project-root</c>).</item>
///   <item>Detect installed MCP clients (Claude Desktop, Copilot CLI).</item>
///   <item>For each selected client: register the <c>koshi</c> MCP entry,
///         write <c>KOSHI_PROJECT_ROOT</c> + (when team-yml supplies it)
///         <c>KOSHI_MEMORY_VAULT</c> into <c>mcpServers.koshi.env</c>,
///         install personas.</item>
///   <item>If <c>.koshi-team.yml</c> (#78 Gap A) is present: clone /
///         fast-forward-pull the vault to the declared path, register the
///         declared team in <c>&lt;projectRoot&gt;/.koshi/teams.json</c>.</item>
///   <item>Print a Next Steps banner: restart your client(s), run
///         <c>koshi-agents doctor</c> to verify, optional smoke command.</item>
/// </list>
///
/// <para>
/// Safety / non-destructive defaults:
/// </para>
/// <list type="bullet">
///   <item><b>--non-interactive</b> never overwrites a persona file unless
///         <c>--force-personas</c> is also passed (N7/N8).</item>
///   <item>Vault target paths from <c>.koshi-team.yml</c> are refused if they
///         escape the project root, unless <c>--accept-team-config</c> (B4).</item>
///   <item>An existing checkout whose <c>git remote get-url origin</c> does
///         not match the declared <c>vault.repo</c> is never overwritten —
///         the wizard fails and prints both URLs (B4).</item>
///   <item>An existing <c>mcpServers.koshi</c> entry is left strictly alone,
///         and the user is told to run <c>koshi-agents doctor</c> to verify
///         it actually launches (B3 — present ≠ valid).</item>
/// </list>
///
/// <para>
/// All I/O goes through injected <see cref="TextWriter"/> / <see cref="TextReader"/>
/// pairs so the wizard is fully unit-testable (N10).
/// </para>
///
/// <para>Exit codes:</para>
/// <list type="bullet">
///   <item><c>0</c> — every selected step succeeded.</item>
///   <item><c>1</c> — at least one selected step failed.</item>
///   <item><c>2</c> — usage / argument error.</item>
///   <item><c>3</c> — non-interactive mode required user input we couldn't infer.</item>
///   <item><c>4</c> — team-yml safety violation (vault escapes project root).</item>
/// </list>
/// </summary>
internal static class InitCommand
{
    /// <summary>
    /// Production entry point used by <c>Program.cs</c>. Reads / writes the
    /// real user's <c>$HOME</c> + per-client config files.
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, TextReader stdin)
        => Run(args, stdout, stderr, stdin,
            homeDir: null, appDataDir: null, cwd: null,
            gitRunner: new ProcessGitRunner());

    /// <summary>
    /// Testable overload. Tests inject temp <paramref name="homeDir"/> /
    /// <paramref name="appDataDir"/> / <paramref name="cwd"/> so they never
    /// touch the real user's filesystem, plus a fake <see cref="IGitRunner"/>
    /// so no real <c>git</c> processes are spawned.
    /// </summary>
    internal static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        TextReader stdin,
        string? homeDir,
        string? appDataDir,
        string? cwd,
        IGitRunner gitRunner)
    {
        if (!TryParseFlags(args, out var opts, out var flagErr))
        {
            stderr.WriteLine($"koshi-mcp init: {flagErr}");
            PrintUsage(stderr);
            return 2;
        }

        if (opts.ShowHelp)
        {
            PrintHelp(stdout);
            return 0;
        }

        cwd ??= Directory.GetCurrentDirectory();
        var projectRoot = opts.ProjectRoot is { Length: > 0 } pr
            ? Path.GetFullPath(pr)
            : ProjectRootResolver.Resolve(cwd);

        stdout.WriteLine($"koshi-mcp init");
        stdout.WriteLine($"  project root : {projectRoot}");

        // ── 1. Read .koshi-team.yml if present ──────────────────────────────
        TeamYaml? teamYml = null;
        if (!opts.SkipTeam)
        {
            var yamlPath = Path.Join(projectRoot, TeamYamlReader.FileName);
            var read = TeamYamlReader.TryRead(yamlPath);
            if (read.Error is not null)
            {
                stderr.WriteLine($"  {TeamYamlReader.FileName}:{read.LineNumber} {read.Error}");
                return 1;
            }
            teamYml = read.Parsed;
            if (teamYml is not null)
                stdout.WriteLine($"  team config  : {yamlPath}");
        }

        // ── 2. Resolve client selection ─────────────────────────────────────
        var (clients, clientErr) = ResolveClients(opts, homeDir, appDataDir);
        if (clientErr is not null)
        {
            stderr.WriteLine($"  {clientErr}");
            return 2;
        }
        if (clients.Count == 0)
        {
            stderr.WriteLine("  no MCP clients detected; install Claude Desktop or Copilot CLI, " +
                             "or pass --client claude/copilot/all.");
            return 1;
        }
        stdout.WriteLine($"  clients      : {string.Join(", ", clients.Select(c => c.ToString().ToLowerInvariant()))}");

        bool anyError = false;

        // ── 3. Per-client work ─────────────────────────────────────────────
        foreach (var client in clients)
        {
            stdout.WriteLine();
            stdout.WriteLine($"==> {client.ToString().ToLowerInvariant()}");

            // ── 3a. Register MCP entry ──
            if (!opts.SkipRegister)
            {
                var reg = McpConfigWriter.RegisterKoshi(client, dryRun: false, homeDir, appDataDir);
                anyError |= EmitRegister(stdout, stderr, reg);
                if (reg.Outcome == McpRegisterOutcome.Error)
                {
                    // Cannot set env vars without the koshi entry; skip remaining
                    // per-client work but keep going for other clients.
                    continue;
                }
            }
            else
            {
                stdout.WriteLine("  skipping mcp register (--skip-register)");
            }

            // ── 3b. Write env vars ──
            // KOSHI_PROJECT_ROOT is always set (#B2 — every consumer of the
            // teams.json / index file resolves through it). KOSHI_MEMORY_VAULT
            // is only set when .koshi-team.yml supplied a vault path.
            var envToSet = new Dictionary<string, string>
            {
                ["KOSHI_PROJECT_ROOT"] = projectRoot,
            };

            var clientName = client.ToString().ToLowerInvariant();
            if (teamYml?.Vault is not null)
            {
                if (!TryResolveVaultPath(teamYml.Vault.Path, projectRoot, opts.AcceptTeamConfig,
                        out var vaultAbs, out var vaultErr))
                {
                    stderr.WriteLine($"  vault: {vaultErr}");
                    stderr.WriteLine("         pass --accept-team-config to allow this path explicitly.");
                    return 4;
                }
                envToSet["KOSHI_MEMORY_VAULT"] = vaultAbs;
            }

            var apply = EnvConfigEditor.Apply(clientName, envToSet, homeDir, appDataDir);
            anyError |= EmitEnv(stdout, stderr, envToSet, apply);

            // ── 3c. Install personas ──
            if (!opts.SkipPersonas)
            {
                var agentsDir = ClientResolver.AgentsDir(client, ScopeKind.User, projectRoot, homeDir);
                var personaResults = PersonaInstaller.InstallAll(client, agentsDir, opts.ForcePersonas);

                int wrote = 0, overwrote = 0, skipped = 0, errors = 0;
                foreach (var r in personaResults)
                {
                    switch (r.Outcome)
                    {
                        case PersonaInstallOutcome.Written: wrote++; break;
                        case PersonaInstallOutcome.Overwrote: overwrote++; break;
                        case PersonaInstallOutcome.SkippedExists: skipped++; break;
                        case PersonaInstallOutcome.Error:
                            errors++;
                            stderr.WriteLine($"  persona '{r.PersonaName}' failed: {r.ErrorMessage}");
                            break;
                    }
                }
                anyError |= errors > 0;

                var summary = wrote + overwrote == 0 && skipped > 0 && errors == 0
                    ? $"  personas: {skipped} already present (re-run with --force-personas to overwrite)"
                    : $"  personas: wrote={wrote} overwrote={overwrote} skipped={skipped} errors={errors} @ {agentsDir}";
                stdout.WriteLine(summary);
            }
            else
            {
                stdout.WriteLine("  skipping personas (--skip-personas)");
            }
        }

        // ── 3d. Install per-ecosystem steering templates (#77 Layer 4) ─────
        // Project-level, not per-client: the same checkout may be opened by
        // multiple clients in parallel. By default we install AGENTS.md (the
        // universal fallback) plus the templates whose target is already in
        // use or whose client was detected/requested — this keeps a Cursor-
        // only repo from sprouting Copilot- and Windsurf-flavoured files.
        // Pass --all-templates to override and drop every template.
        if (!opts.SkipTemplates)
        {
            stdout.WriteLine();
            stdout.WriteLine("==> templates");

            var detectedClientSet = new HashSet<PersonaClient>(clients);
            bool wantCopilot = opts.AllTemplates
                || detectedClientSet.Contains(PersonaClient.Copilot)
                || File.Exists(Path.Join(projectRoot, ".github", "copilot-instructions.md"));
            bool wantCursor = opts.AllTemplates
                || Directory.Exists(Path.Join(projectRoot, ".cursor"))
                || File.Exists(Path.Join(projectRoot, ".cursorrules"));
            bool wantWindsurf = opts.AllTemplates
                || Directory.Exists(Path.Join(projectRoot, ".windsurf"))
                || File.Exists(Path.Join(projectRoot, ".windsurfrules"));

            bool Filter(SteeringTemplate t) => t.Name switch
            {
                "AGENTS.md" => true, // universal fallback
                ".github/copilot-instructions.md" => wantCopilot,
                ".cursorrules" => wantCursor,
                ".windsurfrules" => wantWindsurf,
                _ => false,
            };

            var templateResults = SteeringTemplateInstaller.InstallMatching(
                projectRoot, opts.ForceTemplates, Filter);

            int wrote = 0, appended = 0, present = 0, overwrote = 0, errors = 0;
            foreach (var r in templateResults)
            {
                switch (r.Outcome)
                {
                    case TemplateInstallOutcome.Written:
                        wrote++;
                        stdout.WriteLine($"  template '{r.TemplateName}' → {r.TargetPath} (new)");
                        break;
                    case TemplateInstallOutcome.Appended:
                        appended++;
                        stdout.WriteLine($"  template '{r.TemplateName}' → {r.TargetPath} (appended Koshi block)");
                        break;
                    case TemplateInstallOutcome.AlreadyPresent:
                        present++;
                        break;
                    case TemplateInstallOutcome.Overwrote:
                        overwrote++;
                        stdout.WriteLine($"  template '{r.TemplateName}' → {r.TargetPath} (overwrote)");
                        break;
                    case TemplateInstallOutcome.Error:
                        errors++;
                        stderr.WriteLine($"  template '{r.TemplateName}' failed: {r.ErrorMessage}");
                        break;
                }
            }
            anyError |= errors > 0;
            stdout.WriteLine($"  templates: wrote={wrote} appended={appended} already-present={present} overwrote={overwrote} errors={errors}");
            if (!opts.AllTemplates && (!wantCursor || !wantWindsurf || !wantCopilot))
            {
                stdout.WriteLine("  (run with --all-templates to install rule files for clients not currently detected)");
            }
        }
        else
        {
            stdout.WriteLine();
            stdout.WriteLine("==> templates");
            stdout.WriteLine("  skipping steering templates (--skip-templates)");
        }

        // ── 3e. Per-machine git template (#78 Gap C) ────────────────────────
        // Opt-in (must pass --register-git-template) because this mutates
        // global git config and a per-machine state, not project state.
        // Drops the 4 steering rule files into every freshly-cloned repo via
        // a post-checkout hook. See GitTemplateInstaller for limits.
        if (opts.RegisterGitTemplate)
        {
            stdout.WriteLine();
            stdout.WriteLine("==> git template");
            var effectiveHome = homeDir
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var gtr = GitTemplateInstaller.Install(effectiveHome, opts.ForceGitTemplate, gitRunner);
            anyError |= EmitGitTemplate(stdout, stderr, gtr);
        }

        // ── 4. Vault clone / pull + team registration ──────────────────────
        if (teamYml is not null)
        {
            stdout.WriteLine();
            stdout.WriteLine("==> team");

            if (teamYml.Vault is not null)
            {
                if (!TryResolveVaultPath(teamYml.Vault.Path, projectRoot, opts.AcceptTeamConfig,
                        out var vaultAbs, out var vaultErr))
                {
                    stderr.WriteLine($"  vault: {vaultErr}");
                    return 4;
                }

                var sync = GitClient.EnsureClonedOrFastForward(gitRunner, vaultAbs, teamYml.Vault.Repo);
                anyError |= EmitGit(stdout, stderr, sync);
            }

            if (teamYml.Team is not null)
            {
                var teamsFile = Path.Join(projectRoot, ".koshi", "teams.json");
                var regErr = RegisterTeam(teamsFile, teamYml.Team);
                if (regErr is null)
                {
                    stdout.WriteLine($"  team '{teamYml.Team.Id}' registered → {teamsFile}");
                }
                else
                {
                    stderr.WriteLine($"  team register failed: {regErr}");
                    anyError = true;
                }
            }
        }

        // ── 5. Next steps ──────────────────────────────────────────────────
        stdout.WriteLine();
        stdout.WriteLine("Next steps:");
        stdout.WriteLine("  1. Restart your MCP client(s) so they re-read the config.");
        stdout.WriteLine("  2. Verify the koshi server is reachable:");
        stdout.WriteLine("       koshi-agents doctor");
        stdout.WriteLine("  3. List Koshi's tools to confirm registration:");
        stdout.WriteLine("       koshi-mcp --list-tools");
        if (teamYml?.Team is not null)
        {
            stdout.WriteLine($"  4. Your team '{teamYml.Team.Id}' is live; teammates can run");
            stdout.WriteLine("       koshi-mcp init");
            stdout.WriteLine("     in this repo to get the same wiring.");
        }

        return anyError ? 1 : 0;
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve and safety-check a vault path from <c>.koshi-team.yml</c>.
    /// Absolute paths and paths escaping the project root are refused unless
    /// <paramref name="accept"/> is true.
    /// </summary>
    internal static bool TryResolveVaultPath(
        string declared,
        string projectRoot,
        bool accept,
        out string absolutePath,
        out string? error)
    {
        absolutePath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(declared))
        {
            error = "vault.path must not be empty";
            return false;
        }

        // Reject absolute paths up front when not explicitly accepted.
        var isAbsolute = Path.IsPathRooted(declared);
        var resolved = isAbsolute
            ? Path.GetFullPath(declared)
            : Path.GetFullPath(Path.Join(projectRoot, declared));

        if (!accept)
        {
            if (isAbsolute)
            {
                error = $"vault.path '{declared}' is absolute; expected a project-relative path";
                return false;
            }
            if (!ProjectRootResolver.IsInside(projectRoot, resolved))
            {
                error = $"vault.path '{declared}' resolves outside project root ({resolved})";
                return false;
            }
        }

        absolutePath = resolved;
        return true;
    }

    private static (IReadOnlyList<PersonaClient> Clients, string? Error) ResolveClients(
        Options opts, string? homeDir, string? appDataDir)
    {
        if (opts.Clients is { Count: > 0 } explicitList)
        {
            var resolved = new List<PersonaClient>(explicitList.Count);
            foreach (var name in explicitList)
            {
                if (name.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    return (new[] { PersonaClient.Claude, PersonaClient.Copilot }, null);
                }
                if (!TryParseClient(name, out var c))
                    return (Array.Empty<PersonaClient>(), $"unknown --client value '{name}' (expected: claude, copilot, all)");
                if (!resolved.Contains(c)) resolved.Add(c);
            }
            return (resolved, null);
        }

        // Auto-detect: pick clients whose config file already exists, or whose
        // agents directory exists, OR fall back to "all" so a brand-new
        // install still gets wired up.
        var detected = new List<PersonaClient>();
        foreach (var c in new[] { PersonaClient.Claude, PersonaClient.Copilot })
        {
            var cfgPath = McpClientPaths.Resolve(c.ToString().ToLowerInvariant(), homeDir, appDataDir);
            if (File.Exists(cfgPath) || Directory.Exists(Path.GetDirectoryName(cfgPath)!))
                detected.Add(c);
        }
        if (detected.Count == 0)
        {
            // Non-interactive with no detected client → ambiguous; fail clearly.
            if (opts.NonInteractive)
                return (Array.Empty<PersonaClient>(),
                    "no MCP client config detected; pass --client claude|copilot|all explicitly under --non-interactive");
            // Interactive default = both.
            return (new[] { PersonaClient.Claude, PersonaClient.Copilot }, null);
        }
        return (detected, null);
    }

    private static bool TryParseClient(string raw, out PersonaClient client)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "claude": client = PersonaClient.Claude; return true;
            case "copilot": client = PersonaClient.Copilot; return true;
            default: client = default; return false;
        }
    }

    private static bool EmitRegister(TextWriter stdout, TextWriter stderr, McpRegisterResult reg)
    {
        switch (reg.Outcome)
        {
            case McpRegisterOutcome.Created:
                stdout.WriteLine($"  registered koshi MCP entry → {reg.ConfigPath} (new file)");
                return false;
            case McpRegisterOutcome.Added:
                stdout.WriteLine($"  registered koshi MCP entry → {reg.ConfigPath} (backup: {reg.BackupPath})");
                return false;
            case McpRegisterOutcome.AlreadyPresent:
                // B3: present ≠ valid. Tell the user to verify.
                stdout.WriteLine($"  koshi MCP entry already present in {reg.ConfigPath}");
                stdout.WriteLine($"    (run 'koshi-agents doctor' to confirm it actually launches)");
                return false;
            case McpRegisterOutcome.DryRun:
                stdout.WriteLine($"  [dry-run] {reg.ErrorMessage}");
                return false;
            default:
                stderr.WriteLine($"  register failed: {reg.ErrorMessage}");
                return true;
        }
    }

    private static bool EmitEnv(
        TextWriter stdout, TextWriter stderr,
        IReadOnlyDictionary<string, string> requested,
        EnvEditResult result)
    {
        switch (result.Outcome)
        {
            case EnvEditOutcome.Changed:
                foreach (var (k, v) in requested)
                    stdout.WriteLine($"  env: {k}={v}");
                return false;
            case EnvEditOutcome.Unchanged:
                stdout.WriteLine("  env: already up to date");
                return false;
            case EnvEditOutcome.KoshiEntryMissing:
                stderr.WriteLine($"  env: cannot set — {result.ErrorMessage}");
                return true;
            default:
                stderr.WriteLine($"  env failed ({result.Outcome}): {result.ErrorMessage}");
                return true;
        }
    }

    private static bool EmitGitTemplate(TextWriter stdout, TextWriter stderr, GitTemplateResult r)
    {
        bool err;
        switch (r.Outcome)
        {
            case GitTemplateOutcome.Installed:
                stdout.WriteLine($"  git template: installed → {r.TemplateDir}");
                stdout.WriteLine($"                 init.templatedir set; future `git clone` will auto-drop {r.WrittenFiles.Count - 1} steering files.");
                err = false; break;
            case GitTemplateOutcome.Updated:
                stdout.WriteLine($"  git template: refreshed → {r.TemplateDir}");
                stdout.WriteLine("                 init.templatedir already pointed at us; bodies updated.");
                err = false; break;
            case GitTemplateOutcome.AlreadyConfigured:
                stdout.WriteLine($"  git template: already up to date @ {r.TemplateDir}");
                err = false; break;
            case GitTemplateOutcome.TemplatedirConflict:
                stderr.WriteLine($"  git template: init.templatedir is already set to '{r.ConflictingTemplatedir}'");
                stderr.WriteLine($"                refusing to overwrite. Pass --force-git-template to replace it,");
                stderr.WriteLine($"                or `git config --global --unset init.templatedir` first.");
                err = true; break;
            case GitTemplateOutcome.GitMissing:
                stderr.WriteLine($"  git template: {r.ErrorMessage}");
                err = true; break;
            case GitTemplateOutcome.Error:
            default:
                stderr.WriteLine($"  git template failed: {r.ErrorMessage}");
                err = true; break;
        }
        if (!string.IsNullOrEmpty(r.Warning))
        {
            stderr.WriteLine($"  warning: {r.Warning}");
        }
        return err;
    }

    private static bool EmitGit(TextWriter stdout, TextWriter stderr, GitSyncResult sync)
    {
        switch (sync.Outcome)
        {
            case GitSyncOutcome.Cloned:
                stdout.WriteLine($"  vault: cloned {sync.ExpectedRemote} → {sync.TargetPath}");
                return false;
            case GitSyncOutcome.PulledFastForward:
                stdout.WriteLine($"  vault: pulled → {sync.TargetPath}");
                return false;
            case GitSyncOutcome.AlreadyUpToDate:
                stdout.WriteLine($"  vault: already up to date @ {sync.TargetPath}");
                return false;
            case GitSyncOutcome.GitMissing:
                stderr.WriteLine($"  vault: {sync.Detail}");
                return true;
            case GitSyncOutcome.OriginMismatch:
                stderr.WriteLine($"  vault: origin mismatch at {sync.TargetPath}");
                stderr.WriteLine($"         expected: {sync.ExpectedRemote}");
                stderr.WriteLine($"         actual  : {sync.ActualRemote}");
                stderr.WriteLine("         refusing to pull; move the existing checkout aside or update .koshi-team.yml");
                return true;
            case GitSyncOutcome.NotAGitDirectory:
                stderr.WriteLine($"  vault: {sync.Detail}");
                return true;
            case GitSyncOutcome.NetworkError:
            case GitSyncOutcome.Conflict:
            default:
                stderr.WriteLine($"  vault: git failed: {sync.Detail}");
                return true;
        }
    }

    private static string? RegisterTeam(string teamsFile, TeamYamlTeam declared)
    {
        try
        {
            var backend = new TeamsBackend(teamsFile);
            var registry = new TeamRegistry();
            var loaded = backend.Load();
            if (loaded is not null)
            {
                var scoresByTeam = loaded.ScoresByTeam.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<QualityScore>)kv.Value);
                var feedbackByTeam = loaded.FeedbackByTeam.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<QualityFeedback>)kv.Value);
                registry.Restore(loaded.Teams, scoresByTeam, feedbackByTeam);
            }

            var config = new TeamConfig();
            if (declared.TokenBudget is int budget) config = config with { ContextBudgetTokens = budget };
            // QualityTarget is recorded in the team description so the wizard
            // never silently invents fields TeamConfig doesn't model.
            var profile = new TeamProfile
            {
                TeamId = declared.Id,
                Name = declared.Name ?? declared.Id,
                Description = declared.QualityTarget is double qt
                    ? $"quality_target={qt.ToString(CultureInfo.InvariantCulture)}"
                    : string.Empty,
                Config = config,
            };
            registry.Upsert(profile);
            backend.Save(TeamsEnvelope.From(registry.Snapshot()));
            if (backend.LastSaveError is not null)
                return backend.LastSaveError;
            return null;
        }
        catch (TeamsPersistenceException ex)
        {
            // TeamsBackend.Save wraps persistence failures (IO, permissions,
            // JSON, etc.) in TeamsPersistenceException. Without an explicit
            // arm in the catch filter the wizard would crash with a raw stack
            // trace instead of returning a clean error message to the user.
            // (Opus multi-model review #1.)
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or JsonException)
        {
            return ex.Message;
        }
    }

    // ─── flag parsing ───────────────────────────────────────────────────────

    internal sealed record Options(
        bool ShowHelp,
        bool NonInteractive,
        bool AssumeYes,
        IReadOnlyList<string>? Clients,
        bool SkipPersonas,
        bool SkipTeam,
        bool SkipRegister,
        bool SkipTemplates,
        bool AllTemplates,
        bool ForcePersonas,
        bool ForceTemplates,
        bool AcceptTeamConfig,
        bool RegisterGitTemplate,
        bool ForceGitTemplate,
        string? ProjectRoot);

    internal static bool TryParseFlags(string[] args, out Options opts, out string? error)
    {
        bool showHelp = false, nonInteractive = false, assumeYes = false;
        bool skipPersonas = false, skipTeam = false, skipRegister = false;
        bool skipTemplates = false, allTemplates = false;
        bool forcePersonas = false, forceTemplates = false, acceptTeamConfig = false;
        bool registerGitTemplate = false, forceGitTemplate = false;
        string? projectRoot = null;
        List<string>? clients = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-h":
                case "--help":
                    showHelp = true; break;
                case "--non-interactive":
                    nonInteractive = true; break;
                case "-y":
                case "--yes":
                    assumeYes = true; break;
                case "--skip-personas":
                    skipPersonas = true; break;
                case "--skip-team":
                    skipTeam = true; break;
                case "--skip-register":
                    skipRegister = true; break;
                case "--skip-templates":
                    skipTemplates = true; break;
                case "--all-templates":
                    allTemplates = true; break;
                case "--force-personas":
                    forcePersonas = true; break;
                case "--force-templates":
                    forceTemplates = true; break;
                case "--accept-team-config":
                    acceptTeamConfig = true; break;
                case "--register-git-template":
                    registerGitTemplate = true; break;
                case "--force-git-template":
                    forceGitTemplate = true; registerGitTemplate = true; break;

                case "--client":
                    if (i + 1 >= args.Length)
                    { opts = default!; error = "--client requires a value"; return false; }
                    clients ??= new List<string>();
                    clients.Add(args[++i]);
                    break;
                case "--project-root":
                    if (i + 1 >= args.Length)
                    { opts = default!; error = "--project-root requires a value"; return false; }
                    projectRoot = args[++i];
                    break;

                default:
                    if (a.StartsWith("--client=", StringComparison.Ordinal))
                    {
                        clients ??= new List<string>();
                        clients.Add(a["--client=".Length..]);
                        break;
                    }
                    if (a.StartsWith("--project-root=", StringComparison.Ordinal))
                    {
                        projectRoot = a["--project-root=".Length..];
                        break;
                    }
                    opts = default!;
                    error = $"unknown argument '{a}'";
                    return false;
            }
        }

        opts = new Options(
            ShowHelp: showHelp,
            NonInteractive: nonInteractive,
            AssumeYes: assumeYes,
            Clients: clients,
            SkipPersonas: skipPersonas,
            SkipTeam: skipTeam,
            SkipRegister: skipRegister,
            SkipTemplates: skipTemplates,
            AllTemplates: allTemplates,
            ForcePersonas: forcePersonas,
            ForceTemplates: forceTemplates,
            AcceptTeamConfig: acceptTeamConfig,
            RegisterGitTemplate: registerGitTemplate,
            ForceGitTemplate: forceGitTemplate,
            ProjectRoot: projectRoot);
        error = null;
        return true;
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: koshi-mcp init [--client claude|copilot|all] [flags]");
        w.WriteLine("       koshi-mcp init --help     for the full flag list.");
    }

    private static int PrintHelp(TextWriter w)
    {
        w.WriteLine("koshi-mcp init — wire a fresh project for Koshi in one command.");
        w.WriteLine();
        w.WriteLine("USAGE");
        w.WriteLine("  koshi-mcp init [flags]");
        w.WriteLine();
        w.WriteLine("WHAT IT DOES");
        w.WriteLine("  1. Detects installed MCP clients (Claude Desktop, Copilot CLI).");
        w.WriteLine("  2. Registers the koshi MCP server in each client's config (idempotent).");
        w.WriteLine("  3. Writes KOSHI_PROJECT_ROOT (and KOSHI_MEMORY_VAULT if a team-yml is");
        w.WriteLine("     present) into mcpServers.koshi.env.");
        w.WriteLine("  4. Installs personas (.claude/agents/ and ~/.copilot/agents/).");
        w.WriteLine("  5. Drops per-ecosystem steering files into the project root:");
        w.WriteLine("       AGENTS.md (always), .github/copilot-instructions.md (if Copilot),");
        w.WriteLine("       .cursorrules (if .cursor/ exists), .windsurfrules (if .windsurf/ exists).");
        w.WriteLine("     Existing files are preserved (Koshi block is appended only if missing).");
        w.WriteLine("     Pass --all-templates to install every rule file regardless of detection.");
        w.WriteLine("  6. If .koshi-team.yml is present: git-clones (or fast-forward-pulls)");
        w.WriteLine("     the declared vault, then registers the declared team.");
        w.WriteLine("  7. (Opt-in) With --register-git-template: writes ~/.git-template-koshi/");
        w.WriteLine("     and points `git config --global init.templatedir` at it so every");
        w.WriteLine("     freshly-cloned repo auto-receives the same steering files.");
        w.WriteLine("  8. Prints next-steps + verification commands.");
        w.WriteLine();
        w.WriteLine("FLAGS");
        w.WriteLine("  --client <name>          claude | copilot | all. Repeatable. Defaults to auto-detect.");
        w.WriteLine("  --project-root <PATH>    Override the project root (default: walk-up search).");
        w.WriteLine("  --non-interactive        Fail rather than prompt. Pair with --yes / --force-personas.");
        w.WriteLine("  --yes, -y                Accept the default for any prompt.");
        w.WriteLine("  --skip-personas          Don't write persona files.");
        w.WriteLine("  --skip-team              Ignore .koshi-team.yml entirely.");
        w.WriteLine("  --skip-register          Don't touch mcpServers.koshi.");
        w.WriteLine("  --skip-templates         Don't drop AGENTS.md/.cursorrules/.windsurfrules/.github/copilot-instructions.md.");
        w.WriteLine("  --all-templates          Install every steering file regardless of which clients are detected.");
        w.WriteLine("  --force-personas         Overwrite existing persona files without prompting.");
        w.WriteLine("  --force-templates        Overwrite existing steering files instead of appending.");
        w.WriteLine("  --register-git-template  Opt-in: install ~/.git-template-koshi and set init.templatedir");
        w.WriteLine("                           so every `git clone` auto-drops the steering files. Limits:");
        w.WriteLine("                           does not fire for `git init`, bare clones, or --no-checkout;");
        w.WriteLine("                           a global `core.hooksPath` bypasses per-repo hooks.");
        w.WriteLine("  --force-git-template     Overwrite an existing init.templatedir setting (implies");
        w.WriteLine("                           --register-git-template).");
        w.WriteLine("  --accept-team-config     Trust .koshi-team.yml even if vault.path is absolute or");
        w.WriteLine("                           escapes the project root (off by default for safety).");
        w.WriteLine("  -h, --help               Show this help.");
        w.WriteLine();
        w.WriteLine("EXIT CODES");
        w.WriteLine("  0  every selected step succeeded");
        w.WriteLine("  1  at least one selected step failed");
        w.WriteLine("  2  usage / argument error");
        w.WriteLine("  3  non-interactive mode required input we couldn't infer");
        w.WriteLine("  4  team-yml safety violation (vault.path escapes project root)");
        return 0;
    }
}
