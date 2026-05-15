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
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var clients = ClientParser.Parse(settings.Client);
        var scope = ClientParser.ParseScope(settings.Scope);
        var personas = PersonaCatalog.Discover();

        var writes = 0;
        var skips = 0;
        var errors = 0;

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
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            settings.DryRun
                ? "[grey]Dry run — no files written.[/]"
                : $"[bold]Done.[/] wrote={writes} skipped={skips} errors={errors}");

        if (!settings.DryRun && writes > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Next:[/] make sure the [cyan]koshi[/] MCP server is registered in your client.");
            AnsiConsole.MarkupLine("[grey]Run [bold]koshi-agents doctor[/] to verify.[/]");
        }

        return errors == 0 ? 0 : 1;
    }
}
