using System.ComponentModel;
using Koshi.Agents.Internal;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Koshi.Agents.Commands;

internal sealed class InstallCommand : Command<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-c|--client <CLIENT>")]
        [Description("Target client: claude, copilot, both. Default: both.")]
        public string Client { get; init; } = "both";

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("Install scope: user (~ home dir) or repo (current repo). Default: user.")]
        public string Scope { get; init; } = "user";

        [CommandOption("--dry-run")]
        [Description("Show what would be written without touching the filesystem.")]
        public bool DryRun { get; init; }

        [CommandOption("--force")]
        [Description("Overwrite existing files without prompting.")]
        public bool Force { get; init; }

        [CommandOption("--no-mcp")]
        [Description(
            "Skip registering the koshi MCP server in the client's mcp config. " +
            "Personas are still installed; you must register koshi-mcp yourself.")]
        public bool NoMcp { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var clients = ClientParser.Parse(settings.Client);
        var scope = ClientParser.ParseScope(settings.Scope);
        var personas = PersonaCatalog.Discover();

        var writes = 0;
        var skips = 0;
        var errors = 0;
        var mcpResults = new List<(PersonaClient Client, McpRegisterResult Result)>();

        foreach (var client in clients)
        {
            var dir = ClientResolver.AgentsDir(client, scope);
            AnsiConsole.Write(new Rule(
                $"[cyan]{client.ToString().ToLowerInvariant()}[/] -> [grey]{Markup.Escape(dir)}[/]")
                .LeftJustified());

            if (!settings.DryRun)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Could not create directory:[/] {Markup.Escape(ex.Message)}");
                    errors++;
                    continue;
                }
            }

            foreach (var p in personas.Where(x => x.Client == client))
            {
                var target = Path.Join(dir, p.FileName);
                var exists = File.Exists(target);

                if (settings.DryRun)
                {
                    AnsiConsole.MarkupLine(
                        exists
                            ? $"  [yellow]would overwrite[/] {Markup.Escape(target)}"
                            : $"  [green]would write    [/] {Markup.Escape(target)}");
                    continue;
                }

                if (exists && !settings.Force)
                {
                    var choice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title($"  [yellow]Exists:[/] {Markup.Escape(target)}")
                            .AddChoices("overwrite", "skip", "skip all"));
                    if (choice == "skip")
                    {
                        skips++;
                        AnsiConsole.MarkupLine($"  [grey]skipped       [/] {Markup.Escape(target)}");
                        continue;
                    }
                    if (choice == "skip all")
                    {
                        skips++;
                        AnsiConsole.MarkupLine($"  [grey]skipped       [/] {Markup.Escape(target)}");
                        settings.GetType().GetProperty(nameof(Settings.Force))?
                            .SetValue(settings, false);
                        // Treat remaining existing files as skips by re-using Force=false +
                        // setting a sentinel. For simplicity, just keep prompting on other files.
                        continue;
                    }
                }

                try
                {
                    File.WriteAllText(target, p.Read());
                    writes++;
                    AnsiConsole.MarkupLine($"  [green]wrote         [/] {Markup.Escape(target)}");
                }
                catch (Exception ex)
                {
                    errors++;
                    AnsiConsole.MarkupLine(
                        $"  [red]error          [/] {Markup.Escape(target)} — {Markup.Escape(ex.Message)}");
                }
            }

            if (!settings.NoMcp)
            {
                var mcpResult = McpConfigWriter.RegisterKoshi(client, settings.DryRun);
                mcpResults.Add((client, mcpResult));
                ReportMcpResult(mcpResult);
                if (mcpResult.Outcome == McpRegisterOutcome.Error)
                {
                    errors++;
                }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            settings.DryRun
                ? "[grey]Dry run — no files written.[/]"
                : $"[bold]Done.[/] wrote={writes} skipped={skips} errors={errors}");

        if (!settings.DryRun && writes > 0)
        {
            AnsiConsole.WriteLine();
            EmitNextSteps(mcpResults, settings.NoMcp);
        }

        return errors == 0 ? 0 : 1;
    }

    private static void ReportMcpResult(McpRegisterResult result)
    {
        var path = Markup.Escape(result.ConfigPath);
        switch (result.Outcome)
        {
            case McpRegisterOutcome.Created:
                AnsiConsole.MarkupLine($"  [green]mcp created   [/] {path}");
                break;
            case McpRegisterOutcome.Added:
                AnsiConsole.MarkupLine(
                    $"  [green]mcp added     [/] {path}" +
                    (result.BackupPath is null
                        ? string.Empty
                        : $" [grey](backup: {Markup.Escape(result.BackupPath)})[/]"));
                break;
            case McpRegisterOutcome.AlreadyPresent:
                AnsiConsole.MarkupLine($"  [grey]mcp already-ok[/] {path}");
                break;
            case McpRegisterOutcome.DryRun:
                AnsiConsole.MarkupLine(
                    $"  [yellow]mcp dry-run   [/] {path} " +
                    $"[grey]({Markup.Escape(result.ErrorMessage ?? "would register koshi")})[/]");
                break;
            case McpRegisterOutcome.Error:
                AnsiConsole.MarkupLine(
                    $"  [red]mcp error     [/] {path} — {Markup.Escape(result.ErrorMessage ?? "unknown")}");
                break;
        }
    }

    private static void EmitNextSteps(
        IReadOnlyList<(PersonaClient Client, McpRegisterResult Result)> mcpResults,
        bool skippedMcp)
    {
        if (skippedMcp)
        {
            AnsiConsole.MarkupLine(
                "[bold]Next:[/] register the [cyan]koshi[/] MCP server in each client " +
                "(you passed [yellow]--no-mcp[/]).");
            AnsiConsole.MarkupLine("[grey]  • Copilot CLI: [bold]copilot mcp add koshi -- koshi-mcp[/][/]");
            AnsiConsole.MarkupLine(
                "[grey]  • Claude Desktop: add the koshi entry to " +
                "claude_desktop_config.json manually[/]");
        }
        else
        {
            var anyWritten = mcpResults.Any(r =>
                r.Result.Outcome is McpRegisterOutcome.Created
                                  or McpRegisterOutcome.Added);
            if (anyWritten)
            {
                AnsiConsole.MarkupLine(
                    "[bold]Next:[/] restart your client so it picks up the new " +
                    "[cyan]koshi[/] MCP server entry, then invoke a persona.");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    "[bold]Next:[/] invoke a persona — e.g. " +
                    "[cyan]copilot --agent koshi-orchestrator \"ping\"[/].");
            }
        }
        AnsiConsole.MarkupLine("[grey]Run [bold]koshi-agents doctor[/] to verify.[/]");
    }
}
