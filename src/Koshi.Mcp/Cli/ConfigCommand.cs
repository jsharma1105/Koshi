namespace Koshi.Mcp.Cli;

/// <summary>
/// Parses and dispatches <c>koshi-mcp config &lt;op&gt; ...</c> subcommands.
/// <para>
/// stdout discipline: values / KEY=VALUE lines are the ONLY thing written to
/// stdout (so <c>get</c> is shell-composable). All status, warnings, summaries,
/// and errors go to stderr.
/// </para>
/// <para>
/// Exit codes:
/// </para>
/// <list type="bullet">
///   <item><c>0</c> — operation succeeded for every selected client.</item>
///   <item><c>1</c> — operation failed for at least one explicitly-selected client,
///         or read returned no value for an explicit <c>--client X</c>.</item>
///   <item><c>2</c> — usage / argument error.</item>
/// </list>
/// </summary>
internal static class ConfigCommand
{
    /// <summary>Production entry point used by <c>Program.cs</c>.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        => Run(args, stdout, stderr, homeDir: null, appDataDir: null, stdinReader: null);

    /// <summary>
    /// Testable overload. Tests pass temp <paramref name="homeDir"/> /
    /// <paramref name="appDataDir"/> so we never read or write the real user's
    /// MCP config. (Mutating process env vars is not reliable on Windows because
    /// <see cref="Environment.SpecialFolder.UserProfile"/> resolves via the
    /// Win32 known-folders API and ignores <c>%USERPROFILE%</c>.)
    /// </summary>
    internal static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        string? homeDir,
        string? appDataDir,
        TextReader? stdinReader)
    {
        if (args.Length == 0)
        {
            PrintUsage(stderr);
            return 2;
        }

        var ctx = new CliContext(homeDir, appDataDir, stdinReader ?? Console.In);
        var op = args[0].ToLowerInvariant();
        var rest = args[1..];

        return op switch
        {
            "get" => RunGet(rest, stdout, stderr, ctx),
            "set" => RunSet(rest, stdout, stderr, ctx),
            "unset" => RunUnset(rest, stdout, stderr, ctx),
            "apply" => RunApply(rest, stdout, stderr, ctx),
            "-h" or "--help" or "help" => PrintHelp(stdout),
            _ => UsageError(stderr, $"unknown config operation: '{op}'"),
        };
    }

    private readonly record struct CliContext(string? HomeDir, string? AppDataDir, TextReader StdIn);

    // ───────────────────────────────────────── get ──────────────────────────────────────────

    private static int RunGet(string[] args, TextWriter stdout, TextWriter stderr, CliContext ctx)
    {
        if (!TryParseFlags(args, out var positional, out var client, out var all, out var valueStdin, out var flagErr))
            return UsageError(stderr, flagErr!);

        if (valueStdin)
            return UsageError(stderr, "'--value-stdin' is not valid for 'get'");

        string? key = positional.Count switch
        {
            0 when !all => null,
            1 when !all => positional[0],
            _ => null,
        };

        if (!all && key is null)
            return UsageError(stderr, "usage: koshi-mcp config get KEY --client X | get --all --client X");

        if (client is null)
            return UsageError(stderr, "--client is required for 'get' (one of: claude, copilot)");

        if (string.Equals(client, "all", StringComparison.OrdinalIgnoreCase))
        {
            int exit = 0;
            int found = 0;
            foreach (var c in DiscoverInstalledClients(stderr, ctx))
            {
                found++;
                stderr.WriteLine($"# client: {c}");
                if (DoGet(c, key, all, stdout, stderr, ctx) != 0)
                    exit = 1;
            }
            if (found == 0)
            {
                stderr.WriteLine("warning: no Koshi-aware MCP clients found.");
                return 2;
            }
            return exit;
        }

        string normalized;
        try { normalized = McpClientPaths.Normalize(client); }
        catch (ArgumentException ex) { return UsageError(stderr, ex.Message); }

        return DoGet(normalized, key, all, stdout, stderr, ctx);
    }

    private static int DoGet(string client, string? key, bool all, TextWriter stdout, TextWriter stderr, CliContext ctx)
    {
        if (all)
        {
            var result = EnvConfigEditor.GetAll(client, ctx.HomeDir, ctx.AppDataDir);
            if (!HandleReadError(result, stderr)) return 1;
            foreach (var kvp in result.AllValues!.OrderBy(k => k.Key, StringComparer.Ordinal))
                stdout.WriteLine($"{kvp.Key}={kvp.Value}");
            return 0;
        }

        var single = EnvConfigEditor.Get(client, key!, ctx.HomeDir, ctx.AppDataDir);
        if (!HandleReadError(single, stderr)) return 1;
        if (single.Value is null)
        {
            stderr.WriteLine($"# {client}: '{key}' not set");
            return 1;
        }
        stdout.WriteLine(single.Value);
        return 0;
    }

    // ───────────────────────────────────────── set ──────────────────────────────────────────

    private static int RunSet(string[] args, TextWriter stdout, TextWriter stderr, CliContext ctx)
    {
        if (!TryParseFlags(args, out var positional, out var client, out var all, out var valueStdin, out var flagErr))
            return UsageError(stderr, flagErr!);
        if (all) return UsageError(stderr, "'--all' is not valid for 'set'");
        if (client is null)
            return UsageError(stderr, "--client is required for 'set' (claude | copilot | all)");

        string key, value;
        if (valueStdin)
        {
            if (positional.Count != 1)
                return UsageError(stderr, "usage: koshi-mcp config set KEY --value-stdin --client X");
            key = positional[0];
            value = ctx.StdIn.ReadToEnd().TrimEnd('\r', '\n');
        }
        else
        {
            if (positional.Count != 1)
                return UsageError(stderr, "usage: koshi-mcp config set KEY=VALUE --client X (or --value-stdin)");
            var pair = positional[0];
            int eq = pair.IndexOf('=');
            if (eq <= 0)
                return UsageError(stderr, $"expected KEY=VALUE, got '{pair}'");
            key = pair[..eq];
            value = pair[(eq + 1)..];
        }

        return ApplyToClients(client, stderr, ctx,
            c => EnvConfigEditor.Set(c, key, value, ctx.HomeDir, ctx.AppDataDir),
            describeChange: c => $"# {c}: set {key}",
            describeUnchanged: c => $"# {c}: {key} already set to that value");
    }

    // ───────────────────────────────────────── unset ──────────────────────────────────────────

    private static int RunUnset(string[] args, TextWriter stdout, TextWriter stderr, CliContext ctx)
    {
        if (!TryParseFlags(args, out var positional, out var client, out var all, out var valueStdin, out var flagErr))
            return UsageError(stderr, flagErr!);
        if (valueStdin) return UsageError(stderr, "'--value-stdin' is not valid for 'unset'");
        if (all) return UsageError(stderr, "'--all' is not valid for 'unset' (use --client all to target every installed client)");
        if (positional.Count != 1)
            return UsageError(stderr, "usage: koshi-mcp config unset KEY --client X");
        if (client is null)
            return UsageError(stderr, "--client is required for 'unset' (claude | copilot | all)");

        var key = positional[0];

        return ApplyToClients(client, stderr, ctx,
            c => EnvConfigEditor.Unset(c, key, ctx.HomeDir, ctx.AppDataDir),
            describeChange: c => $"# {c}: unset {key}",
            describeUnchanged: c => $"# {c}: {key} was not set");
    }

    // ───────────────────────────────────────── apply ──────────────────────────────────────────

    private static int RunApply(string[] args, TextWriter stdout, TextWriter stderr, CliContext ctx)
    {
        if (!TryParseFlags(args, out var positional, out var client, out var all, out var valueStdin, out var flagErr))
            return UsageError(stderr, flagErr!);
        if (all) return UsageError(stderr, "'--all' is not valid for 'apply'");
        if (valueStdin) return UsageError(stderr, "'--value-stdin' is not valid for 'apply'");
        if (positional.Count != 1)
            return UsageError(stderr, "usage: koshi-mcp config apply FILE.env --client X|all");
        if (client is null)
            return UsageError(stderr, "--client is required for 'apply' (claude | copilot | all)");

        var filePath = positional[0];
        if (!File.Exists(filePath))
        {
            stderr.WriteLine($"error: file not found: {filePath}");
            return 1;
        }

        string[] lines;
        try { lines = File.ReadAllLines(filePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: failed to read {filePath}: {ex.Message}");
            return 1;
        }

        if (!DotEnvParser.TryParse(lines, out var values, out var parseErr))
        {
            stderr.WriteLine($"error: {filePath}: {parseErr}");
            return 1;
        }

        return ApplyToClients(client, stderr, ctx,
            c => EnvConfigEditor.Apply(c, values, ctx.HomeDir, ctx.AppDataDir),
            describeChange: c => $"# {c}: applied {values.Count} key(s)",
            describeUnchanged: c => $"# {c}: nothing changed (all values already current)");
    }

    // ─────────────────────────────────── shared helpers ───────────────────────────────────

    private static int ApplyToClients(
        string client,
        TextWriter stderr,
        CliContext ctx,
        Func<string, EnvEditResult> op,
        Func<string, string> describeChange,
        Func<string, string> describeUnchanged)
    {
        if (string.Equals(client, "all", StringComparison.OrdinalIgnoreCase))
        {
            int exit = 0;
            int targets = 0;
            foreach (var c in DiscoverInstalledClients(stderr, ctx))
            {
                targets++;
                var r = op(c);
                if (!HandleWriteResult(c, r, stderr, describeChange, describeUnchanged))
                    exit = 1;
            }
            if (targets == 0)
            {
                stderr.WriteLine("warning: no Koshi-aware MCP clients found.");
                return 2;
            }
            return exit;
        }

        string normalized;
        try { normalized = McpClientPaths.Normalize(client); }
        catch (ArgumentException ex) { return UsageError(stderr, ex.Message); }

        var single = op(normalized);
        return HandleWriteResult(normalized, single, stderr, describeChange, describeUnchanged) ? 0 : 1;
    }

    private static bool HandleReadError(EnvEditResult r, TextWriter stderr)
    {
        switch (r.Outcome)
        {
            case EnvEditOutcome.Read:
                return true;
            case EnvEditOutcome.ConfigMissing:
                stderr.WriteLine($"error: config file does not exist: {r.ConfigPath}");
                return false;
            case EnvEditOutcome.KoshiEntryMissing:
                stderr.WriteLine($"error: {r.ConfigPath}: {r.ErrorMessage}");
                return false;
            case EnvEditOutcome.InvalidJson:
                stderr.WriteLine($"error: {r.ConfigPath}: invalid JSON: {r.ErrorMessage}");
                return false;
            case EnvEditOutcome.IoError:
                stderr.WriteLine($"error: {r.ConfigPath}: {r.ErrorMessage}");
                return false;
            default:
                stderr.WriteLine($"error: {r.ConfigPath}: {r.ErrorMessage ?? r.Outcome.ToString()}");
                return false;
        }
    }

    private static bool HandleWriteResult(
        string client,
        EnvEditResult r,
        TextWriter stderr,
        Func<string, string> describeChange,
        Func<string, string> describeUnchanged)
    {
        switch (r.Outcome)
        {
            case EnvEditOutcome.Changed:
                stderr.WriteLine(describeChange(client));
                if (r.BackupPath is not null)
                    stderr.WriteLine($"  backup: {r.BackupPath}");
                return true;
            case EnvEditOutcome.Unchanged:
                stderr.WriteLine(describeUnchanged(client));
                return true;
            case EnvEditOutcome.ConfigMissing:
                stderr.WriteLine($"warning: {client}: config file does not exist ({r.ConfigPath}) — skipping");
                return false;
            case EnvEditOutcome.KoshiEntryMissing:
                stderr.WriteLine($"warning: {client}: koshi entry missing — run 'koshi-agents install --client {client}' first ({r.ConfigPath})");
                return false;
            case EnvEditOutcome.InvalidJson:
                stderr.WriteLine($"error: {client}: invalid JSON in {r.ConfigPath}: {r.ErrorMessage}");
                return false;
            case EnvEditOutcome.IoError:
                stderr.WriteLine($"error: {client}: {r.ErrorMessage} ({r.ConfigPath})");
                return false;
            default:
                stderr.WriteLine($"error: {client}: {r.ErrorMessage ?? r.Outcome.ToString()} ({r.ConfigPath})");
                return false;
        }
    }

    /// <summary>
    /// Lists clients whose config file exists AND contains an <c>mcpServers.koshi</c> entry.
    /// Clients without a config file, or without a koshi entry, are skipped with a stderr note.
    /// </summary>
    private static IEnumerable<string> DiscoverInstalledClients(TextWriter stderr, CliContext ctx)
    {
        foreach (var client in McpClientPaths.KnownClients)
        {
            string path;
            try { path = McpClientPaths.Resolve(client, ctx.HomeDir, ctx.AppDataDir); }
            catch (ArgumentException) { continue; }

            if (!File.Exists(path))
            {
                stderr.WriteLine($"# {client}: skipped (config file does not exist: {path})");
                continue;
            }

            var probe = EnvConfigEditor.GetAll(client, ctx.HomeDir, ctx.AppDataDir);
            if (probe.Outcome == EnvEditOutcome.Read)
            {
                yield return client;
            }
            else
            {
                stderr.WriteLine($"# {client}: skipped ({probe.ErrorMessage ?? probe.Outcome.ToString()})");
            }
        }
    }

    // ─────────────────────────────────── flag parsing ───────────────────────────────────

    private static bool TryParseFlags(
        string[] args,
        out List<string> positional,
        out string? client,
        out bool all,
        out bool valueStdin,
        out string? error)
    {
        positional = new List<string>();
        client = null;
        all = false;
        valueStdin = false;
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--client":
                    if (i + 1 >= args.Length) { error = "--client requires a value"; return false; }
                    client = args[++i];
                    break;
                case "--all":
                    all = true;
                    break;
                case "--value-stdin":
                    valueStdin = true;
                    break;
                default:
                    if (a.StartsWith("--client=", StringComparison.Ordinal))
                        client = a["--client=".Length..];
                    else if (a.StartsWith("--", StringComparison.Ordinal))
                    { error = $"unknown flag: {a}"; return false; }
                    else
                        positional.Add(a);
                    break;
            }
        }
        return true;
    }

    private static int UsageError(TextWriter stderr, string message)
    {
        stderr.WriteLine($"error: {message}");
        PrintUsage(stderr);
        return 2;
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("usage: koshi-mcp config <op> [args] --client <claude|copilot|all>");
        w.WriteLine("  get KEY --client X            print the value of an env var");
        w.WriteLine("  get --all --client X          print every env var as KEY=VALUE");
        w.WriteLine("  set KEY=VALUE --client X|all  set an env var (and back up the file)");
        w.WriteLine("  set KEY --value-stdin --client X|all  set an env var, value from stdin (avoids shell history)");
        w.WriteLine("  unset KEY --client X|all      remove an env var");
        w.WriteLine("  apply FILE.env --client X|all merge a dotenv file (KEY=VALUE per line)");
    }

    private static int PrintHelp(TextWriter w)
    {
        PrintUsage(w);
        return 0;
    }
}
