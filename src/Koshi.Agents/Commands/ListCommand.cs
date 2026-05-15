using System.ComponentModel;
using Koshi.Agents.Internal;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Koshi.Agents.Commands;

internal sealed class ListCommand : Command<ListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-c|--client <CLIENT>")]
        [Description("Filter by client: claude, copilot, both. Default: both.")]
        public string Client { get; init; } = "both";
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var clients = ClientParser.Parse(settings.Client);
        var personas = PersonaCatalog.Discover()
            .Where(p => clients.Contains(p.Client))
            .ToArray();

        if (personas.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No personas embedded in this build.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Koshi personas[/]")
            .AddColumn("Persona")
            .AddColumn("Client")
            .AddColumn("File");

        foreach (var p in personas)
        {
            table.AddRow(
                $"[cyan]{Markup.Escape(p.Name)}[/]",
                p.Client.ToString().ToLowerInvariant(),
                $"[grey]{Markup.Escape(p.FileName)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]Use [bold]koshi-agents show <persona>[/] to see contents.[/]");
        return 0;
    }
}
