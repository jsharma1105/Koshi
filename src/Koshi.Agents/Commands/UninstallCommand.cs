using System.ComponentModel;
using Koshi.Agents.Internal;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Koshi.Agents.Commands;

internal sealed class UninstallCommand : Command<UninstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-c|--client <CLIENT>")]
        [Description("Target client: claude, copilot, both. Default: both.")]
        public string Client { get; init; } = "both";

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("Scope: user or repo. Default: user.")]
        public string Scope { get; init; } = "user";

        [CommandOption("--dry-run")]
        [Description("Show what would be removed without touching the filesystem.")]
        public bool DryRun { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var clients = ClientParser.Parse(settings.Client);
        var scope = ClientParser.ParseScope(settings.Scope);
        var personas = PersonaCatalog.Discover();

        var deletes = 0;
        var missing = 0;
        var errors = 0;

        foreach (var client in clients)
        {
            var dir = ClientResolver.AgentsDir(client, scope);
            AnsiConsole.Write(new Rule(
                $"[cyan]{client.ToString().ToLowerInvariant()}[/] -> [grey]{Markup.Escape(dir)}[/]")
                .LeftJustified());

            foreach (var p in personas.Where(x => x.Client == client))
            {
                var target = Path.Join(dir, p.FileName);
                if (!File.Exists(target))
                {
                    missing++;
                    AnsiConsole.MarkupLine($"  [grey]not present   [/] {Markup.Escape(target)}");
                    continue;
                }

                if (settings.DryRun)
                {
                    AnsiConsole.MarkupLine($"  [yellow]would delete  [/] {Markup.Escape(target)}");
                    continue;
                }

                try
                {
                    File.Delete(target);
                    deletes++;
                    AnsiConsole.MarkupLine($"  [green]deleted       [/] {Markup.Escape(target)}");
                }
                catch (Exception ex)
                {
                    errors++;
                    AnsiConsole.MarkupLine(
                        $"  [red]error          [/] {Markup.Escape(target)} — {Markup.Escape(ex.Message)}");
                }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            settings.DryRun
                ? "[grey]Dry run — no files removed.[/]"
                : $"[bold]Done.[/] deleted={deletes} not-present={missing} errors={errors}");
        AnsiConsole.MarkupLine("[grey]Your MCP config (settings.json / mcp_config.json) was not modified.[/]");

        return errors == 0 ? 0 : 1;
    }
}
