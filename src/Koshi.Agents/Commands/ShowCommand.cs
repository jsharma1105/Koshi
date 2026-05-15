using System.ComponentModel;
using Koshi.Agents.Internal;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Koshi.Agents.Commands;

internal sealed class ShowCommand : Command<ShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PERSONA>")]
        [Description("The persona name (e.g. koshi-librarian).")]
        public string PersonaName { get; init; } = "";

        [CommandOption("-c|--client <CLIENT>")]
        [Description("Which client's variant to show: claude, copilot. Default: claude.")]
        public string Client { get; init; } = "claude";
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var clients = ClientParser.Parse(settings.Client);
        if (clients.Count != 1)
        {
            AnsiConsole.MarkupLine("[red]--client must be exactly one of: claude, copilot[/]");
            return 2;
        }

        var match = PersonaCatalog.Discover()
            .FirstOrDefault(p =>
                p.Client == clients[0] &&
                string.Equals(p.Name, settings.PersonaName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]Persona [bold]{Markup.Escape(settings.PersonaName)}[/] not found for client " +
                $"[bold]{clients[0].ToString().ToLowerInvariant()}[/].[/]");
            AnsiConsole.MarkupLine("[grey]Run [bold]koshi-agents list[/] to see what's available.[/]");
            return 1;
        }

        var content = match.Read();

        AnsiConsole.Write(new Rule($"[cyan]{Markup.Escape(match.Name)}[/] ([grey]{match.Client}[/])")
            .LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(Markup.Escape(content))
            .Border(BoxBorder.Rounded)
            .Expand());
        return 0;
    }
}
