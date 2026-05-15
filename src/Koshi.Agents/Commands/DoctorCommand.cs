using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
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
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var clients = ClientParser.Parse(settings.Client);
        var scope = ClientParser.ParseScope(settings.Scope);
        var personas = PersonaCatalog.Discover();
        var failures = 0;

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
        }

        // 2. Per-client checks
        foreach (var client in clients)
        {
            AnsiConsole.Write(new Rule($"[cyan]{client.ToString().ToLowerInvariant()}[/]")
                .LeftJustified());

            // 2a. MCP config file
            var cfg = ClientResolver.McpConfigFile(client);
            if (!File.Exists(cfg))
            {
                AnsiConsole.MarkupLine($"  [yellow]⚠[/] MCP config not found at [grey]{Markup.Escape(cfg)}[/]");
                AnsiConsole.MarkupLine("    [grey]The client may not be installed, or hasn't been launched yet.[/]");
            }
            else
            {
                var hasKoshi = TryDetectKoshiEntry(cfg, out var detail);
                if (hasKoshi)
                {
                    AnsiConsole.MarkupLine($"  [green]✓[/] [bold]koshi[/] entry present in [grey]{Markup.Escape(cfg)}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] no [bold]koshi[/] entry in [grey]{Markup.Escape(cfg)}[/]");
                    AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(detail)}[/]");
                    AnsiConsole.MarkupLine("    [grey]Add this to your mcpServers block:[/]");
                    AnsiConsole.Write(new Panel(Markup.Escape(ClientResolver.SuggestedMcpEntry()))
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

    private static bool TryDetectKoshiEntry(string configFile, out string detail)
    {
        try
        {
            using var stream = File.OpenRead(configFile);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            // Both Claude Desktop and Copilot CLI use an "mcpServers" object.
            if (doc.RootElement.TryGetProperty("mcpServers", out var servers) &&
                servers.ValueKind == JsonValueKind.Object &&
                servers.TryGetProperty("koshi", out _))
            {
                detail = "Found mcpServers.koshi entry.";
                return true;
            }

            detail = "mcpServers.koshi entry missing.";
            return false;
        }
        catch (JsonException ex)
        {
            detail = $"Config file is not valid JSON: {ex.Message}";
            return false;
        }
        catch (IOException ex)
        {
            detail = $"Could not read config file: {ex.Message}";
            return false;
        }
    }
}
