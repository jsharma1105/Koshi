using System.ComponentModel;
using Koshi.Agents.Internal;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Koshi.Agents.Commands;

internal sealed class DoctorCommand : Command<DoctorCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-c|--client <CLIENT>")]
        [Description("Which client to inspect: claude, copilot, both. Default: both.")]
        public string Client { get; init; } = "both";

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("Scope: user or repo. Default: user.")]
        public string Scope { get; init; } = "user";

        [CommandOption("--quick")]
        [Description("Skip the live-ping handshake — static file/PATH/config checks only.")]
        public bool Quick { get; init; }

        [CommandOption("--verbose")]
        [Description("Print JSON-RPC handshake details, tool list, and stderr tail for failed pings.")]
        public bool Verbose { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var clients = ClientParser.Parse(settings.Client);
        var scope = ClientParser.ParseScope(settings.Scope);
        var personas = PersonaCatalog.Discover();
        var failures = 0;
        var handshake = new StdioMcpHandshake();

        // 1. koshi-mcp on PATH?
        var mcpPath = FindOnPath(OperatingSystem.IsWindows() ? "koshi-mcp.exe" : "koshi-mcp");
        if (mcpPath is null)
        {
            AnsiConsole.MarkupLine("[red]✗[/] [bold]koshi-mcp[/] not found on PATH.");
            AnsiConsole.MarkupLine("  [grey]Install it: [bold]dotnet tool install --global Koshi.Mcp[/][/]");
            failures++;
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]✓[/] koshi-mcp found at [grey]{Markup.Escape(mcpPath)}[/]");

            // 1a. Live-ping the PATH binary directly. This proves the binary on
            // PATH actually starts and handshakes, independent of any client
            // config. Per-client live pings (2c) re-run the probe with each
            // client's configured command/args/env so we also catch the case
            // where the registered command points elsewhere.
            if (!settings.Quick)
            {
                var probe = RunLivePing(handshake, mcpPath, args: Array.Empty<string>(),
                    env: EmptyEnv, cwd: null, cancellationToken);
                RenderLivePing(probe, indent: "  ", verbose: settings.Verbose);
                if (!probe.IsAllGreen) failures++;
            }
        }

        // 2. Per-client checks
        foreach (var client in clients)
        {
            AnsiConsole.Write(new Rule($"[cyan]{client.ToString().ToLowerInvariant()}[/]")
                .LeftJustified());

            // 2a. MCP config file
            var cfg = ClientResolver.McpConfigFile(client);
            ClientKoshiEntry? clientEntry = null;
            if (!File.Exists(cfg))
            {
                AnsiConsole.MarkupLine($"  [yellow]⚠[/] MCP config not found at [grey]{Markup.Escape(cfg)}[/]");
                AnsiConsole.MarkupLine("    [grey]The client may not be installed, or hasn't been launched yet.[/]");
            }
            else
            {
                clientEntry = ClientKoshiEntryReader.TryRead(cfg, out var readErr);
                if (clientEntry is not null)
                {
                    AnsiConsole.MarkupLine($"  [green]✓[/] [bold]koshi[/] entry present in [grey]{Markup.Escape(cfg)}[/]");
                    if (settings.Verbose)
                    {
                        AnsiConsole.MarkupLine($"    [grey]command:[/] [grey]{Markup.Escape(clientEntry.Command)}[/]");
                        if (clientEntry.Args.Count > 0)
                            AnsiConsole.MarkupLine($"    [grey]args:[/] [grey]{Markup.Escape(string.Join(' ', clientEntry.Args))}[/]");
                    }
                }
                else if (readErr is not null)
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] [bold]koshi[/] entry unreadable in [grey]{Markup.Escape(cfg)}[/]");
                    AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(readErr)}[/]");
                    failures++;
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] no [bold]koshi[/] entry in [grey]{Markup.Escape(cfg)}[/]");
                    AnsiConsole.MarkupLine("    [grey]Add this to your mcpServers block:[/]");
                    AnsiConsole.Write(new Panel(Markup.Escape(ClientResolver.SuggestedMcpEntry(client)))
                        .Border(BoxBorder.Rounded)
                        .Header("snippet"));
                    failures++;
                }
            }

            // 2b. Persona files on disk
            var dir = ClientResolver.AgentsDir(client, scope);
            var expected = personas.Where(p => p.Client == client).ToList();
            var presentCount = expected.Count(p => File.Exists(Path.Join(dir, p.FileName)));

            if (presentCount == 0)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]⚠[/] 0/{expected.Count} personas installed at [grey]{Markup.Escape(dir)}[/]");
                AnsiConsole.MarkupLine("    [grey]Run [bold]koshi-agents install[/] to install them.[/]");
            }
            else if (presentCount == expected.Count)
            {
                AnsiConsole.MarkupLine(
                    $"  [green]✓[/] {presentCount}/{expected.Count} personas installed at [grey]{Markup.Escape(dir)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]⚠[/] {presentCount}/{expected.Count} personas installed at [grey]{Markup.Escape(dir)}[/]");
                AnsiConsole.MarkupLine("    [grey]Run [bold]koshi-agents install --force[/] to top-up.[/]");
            }

            // 2c. Per-client env-var resolution + live-ping (only when we got a parsed entry).
            if (clientEntry is not null)
            {
                var envResults = KoshiEnvCheck.CheckAll(clientEntry.Env, Directory.GetCurrentDirectory());
                RenderEnvChecks(envResults, ref failures, indent: "  ");

                if (!settings.Quick)
                {
                    var probe = RunLivePing(handshake, clientEntry.Command, clientEntry.Args,
                        clientEntry.Env, cwd: null, cancellationToken);
                    RenderLivePing(probe, indent: "  ", verbose: settings.Verbose);
                    if (!probe.IsAllGreen) failures++;
                }
            }
        }

        AnsiConsole.WriteLine();
        if (failures == 0)
        {
            AnsiConsole.MarkupLine("[green bold]All clear.[/]");
            return 0;
        }
        AnsiConsole.MarkupLine($"[red bold]{failures} issue(s) found.[/]");
        return 1;
    }

    private static readonly IReadOnlyDictionary<string, string?> EmptyEnv =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private static McpLivePingResult RunLivePing(
        IMcpHandshake handshake,
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env,
        string? cwd,
        CancellationToken cancellationToken)
    {
        var opts = new McpLivePingOptions
        {
            Command = command,
            Args = args,
            Env = env,
            Cwd = cwd,
        };
        // Run synchronously inside the Spectre command — DoctorCommand is sync.
        // Catch absolutely everything so the doctor itself never crashes.
        try
        {
            return McpLivePing.RunAsync(opts, handshake, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return new McpLivePingResult { BinaryPath = command, Args = args.ToArray() };
        }
        catch (Exception ex)
        {
            var r = new McpLivePingResult { BinaryPath = command, Args = args.ToArray() };
            r.Errors.Add($"live ping crashed: {ex.GetType().Name}: {ex.Message}");
            return r;
        }
    }

    private static void RenderLivePing(McpLivePingResult r, string indent, bool verbose)
    {
        // Header: spawn evidence.
        if (!r.SpawnSucceeded)
        {
            AnsiConsole.MarkupLine($"{indent}[red]✗[/] live-ping spawn failed");
            foreach (var e in r.Errors)
                AnsiConsole.MarkupLine($"{indent}  [grey]{Markup.Escape(e)}[/]");
            return;
        }

        var versionLabel = r.BinaryVersion is null
            ? "[yellow](version unknown)[/]"
            : $"version [bold]{Markup.Escape(r.BinaryVersion)}[/]";
        AnsiConsole.MarkupLine(
            $"{indent}[green]✓[/] spawned PID {r.Pid} in {r.SpawnLatencyMs}ms, {versionLabel}");

        // Phase: initialize.
        if (r.InitializeSucceeded)
        {
            var proto = r.InitializeProtocol is null ? "(unknown protocol)" : $"protocol {r.InitializeProtocol}";
            AnsiConsole.MarkupLine($"{indent}[green]✓[/] initialize OK ({Markup.Escape(proto)})");
        }
        else
        {
            AnsiConsole.MarkupLine($"{indent}[red]✗[/] initialize failed");
        }

        // Phase: tools/list.
        if (r.AdvertisedTools.Count > 0)
        {
            if (r.MissingRequiredTools.Count == 0)
                AnsiConsole.MarkupLine($"{indent}[green]✓[/] tools advertised: {r.AdvertisedTools.Count} (required tools present)");
            else
                AnsiConsole.MarkupLine(
                    $"{indent}[red]✗[/] tools advertised: {r.AdvertisedTools.Count}, missing required: " +
                    Markup.Escape(string.Join(", ", r.MissingRequiredTools)));

            if (verbose)
            {
                AnsiConsole.MarkupLine($"{indent}  [grey]{Markup.Escape(string.Join(", ", r.AdvertisedTools))}[/]");
            }
        }
        else if (r.InitializeSucceeded)
        {
            AnsiConsole.MarkupLine($"{indent}[red]✗[/] tools/list returned no tools");
        }

        // Phase: health.
        if (r.HealthCallSucceeded)
        {
            var line = r.HealthFirstLine is null
                ? "responded"
                : $"responded: {Markup.Escape(r.HealthFirstLine)}";
            AnsiConsole.MarkupLine($"{indent}[green]✓[/] koshi_health {line}");
        }
        else if (r.AdvertisedTools.Contains("koshi_health"))
        {
            AnsiConsole.MarkupLine($"{indent}[red]✗[/] koshi_health call failed");
        }

        // Shutdown.
        if (r.StoppedCleanly)
            AnsiConsole.MarkupLine($"{indent}[green]✓[/] stopped cleanly");
        else
            AnsiConsole.MarkupLine($"{indent}[yellow]⚠[/] did not exit cleanly (force-killed)");

        // Errors (unless already explained above).
        foreach (var e in r.Errors)
            AnsiConsole.MarkupLine($"{indent}  [grey]{Markup.Escape(e)}[/]");

        // Stderr tail in verbose mode or on any failure.
        if ((verbose || !r.IsAllGreen) && r.StderrTail.Count > 0)
        {
            AnsiConsole.MarkupLine($"{indent}  [grey]stderr tail:[/]");
            foreach (var line in r.StderrTail.TakeLast(10))
                AnsiConsole.MarkupLine($"{indent}    [grey]{Markup.Escape(line)}[/]");
        }
    }

    private static void RenderEnvChecks(
        IReadOnlyList<KoshiEnvCheck.CheckResult> results,
        ref int failures,
        string indent)
    {
        if (results.Count == 0) return;

        AnsiConsole.MarkupLine($"{indent}env vars:");
        foreach (var r in results)
        {
            var glyph = r.Outcome switch
            {
                KoshiEnvCheck.CheckOutcome.Ok => "[green]✓[/]",
                KoshiEnvCheck.CheckOutcome.OkNotYetCreated => "[green]✓[/]",
                _ => "[red]✗[/]",
            };
            var note = r.Outcome switch
            {
                KoshiEnvCheck.CheckOutcome.Ok when r.Kind == KoshiEnvCheck.PathKind.Directory => "(dir exists)",
                KoshiEnvCheck.CheckOutcome.Ok when r.Kind == KoshiEnvCheck.PathKind.File => "(file exists)",
                KoshiEnvCheck.CheckOutcome.OkNotYetCreated => $"({r.Detail})",
                KoshiEnvCheck.CheckOutcome.MissingDirectory => "(directory does NOT exist)",
                KoshiEnvCheck.CheckOutcome.MissingFileParent => $"({r.Detail})",
                KoshiEnvCheck.CheckOutcome.InvalidPath => $"(invalid path: {r.Detail})",
                _ => string.Empty,
            };
            AnsiConsole.MarkupLine(
                $"{indent}  {glyph} {r.Name} = [grey]{Markup.Escape(r.ResolvedPath)}[/] [grey]{Markup.Escape(note)}[/]");
            if (r.IsProblem) failures++;
        }
    }

    private static string? FindOnPath(string executable)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Join(dir, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // ignore malformed PATH entries
            }
        }
        return null;
    }
}
