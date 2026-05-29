using Koshi.Mcp.Cli.Setup;
using Spectre.Console;

namespace Koshi.Agents.Internal;

/// <summary>
/// Renders the per-client "Next steps" banner shown after a successful
/// <c>koshi-agents install</c>. Returns plain Spectre markup lines so the
/// command surface stays I/O-only and the renderer itself is unit-testable.
///
/// Output contract (issue #72):
///   1. Tells the user whether the MCP server was registered automatically
///      or whether they need to do it manually.
///   2. If manual, shows the exact JSON snippet to add and the config path.
///   3. Tells the user to restart the client.
///   4. Suggests running <c>koshi-agents doctor</c> to verify.
///   5. Shows a one-liner to invoke a persona for a smoke check.
/// </summary>
internal static class NextStepsBanner
{
    /// <summary>
    /// Renders the banner for a single client+install outcome. Returns a list
    /// of pre-formatted Spectre markup lines; the caller writes them out.
    /// </summary>
    /// <param name="client">Target client.</param>
    /// <param name="agentsDir">Directory personas were (or would be) installed to.</param>
    /// <param name="mcpResult">
    /// Result of the MCP registration attempt for this client, or <c>null</c>
    /// when <paramref name="skippedMcp"/> is <c>true</c>.
    /// </param>
    /// <param name="skippedMcp">
    /// <c>true</c> when the user passed <c>--no-mcp</c> (or for
    /// <c>--show-next-steps</c> with no real install context).
    /// </param>
    public static IReadOnlyList<string> Render(
        PersonaClient client,
        string agentsDir,
        McpRegisterResult? mcpResult,
        bool skippedMcp)
    {
        var lines = new List<string>();
        var clientName = client.ToString().ToLowerInvariant();

        lines.Add($"[bold]Next steps for {clientName}:[/]");
        lines.Add(string.Empty);

        int step = 1;

        if (skippedMcp)
        {
            lines.Add($"  {step}. Register the [cyan]koshi[/] MCP server with {DescribeClient(client)}:");
            lines.Add(string.Empty);
            AppendMcpRegistrationInstructions(lines, client);
            step++;
        }
        else if (mcpResult is { Outcome: McpRegisterOutcome.AlreadyPresent })
        {
            lines.Add($"  {step}. [grey]koshi MCP entry already present in[/] " +
                      $"[grey]{Markup.Escape(mcpResult.ConfigPath)}[/] [grey]— left untouched.[/]");
            step++;
        }
        else if (mcpResult is { Outcome: McpRegisterOutcome.Created or McpRegisterOutcome.Added })
        {
            lines.Add($"  {step}. [green]koshi MCP server registered[/] in " +
                      $"[grey]{Markup.Escape(mcpResult.ConfigPath)}[/]");
            step++;
        }
        else if (mcpResult is { Outcome: McpRegisterOutcome.Error })
        {
            lines.Add($"  {step}. [red]Could not auto-register koshi MCP server[/] in " +
                      $"[grey]{Markup.Escape(mcpResult.ConfigPath)}[/]:");
            lines.Add($"     [grey]{Markup.Escape(mcpResult.ErrorMessage ?? "unknown error")}[/]");
            lines.Add(string.Empty);
            lines.Add($"     Register it manually:");
            lines.Add(string.Empty);
            AppendMcpRegistrationInstructions(lines, client);
            step++;
        }
        else if (mcpResult is { Outcome: McpRegisterOutcome.DryRun })
        {
            lines.Add($"  {step}. [yellow]Dry-run only[/] — re-run without [yellow]--dry-run[/] to " +
                      $"register the MCP server.");
            step++;
        }

        lines.Add($"  {step}. Restart {DescribeClient(client)} so it picks up the new personas and MCP server.");
        step++;

        lines.Add($"  {step}. Verify the wiring:");
        lines.Add(string.Empty);
        lines.Add($"       [cyan]koshi-agents doctor[/]");
        step++;

        lines.Add(string.Empty);
        lines.Add($"  {step}. Try a persona:");
        lines.Add(string.Empty);
        lines.Add($"       [cyan]{SuggestedSmokeCommand(client)}[/]");

        lines.Add(string.Empty);
        lines.Add("[grey]Personas without an MCP server are inert — registering the [bold]koshi[/] MCP server above is required for tools to work.[/]");

        return lines;
    }

    private static string DescribeClient(PersonaClient client) => client switch
    {
        PersonaClient.Claude => "Claude Desktop",
        PersonaClient.Copilot => "Copilot CLI",
        _ => client.ToString(),
    };

    private static string SuggestedSmokeCommand(PersonaClient client) => client switch
    {
        PersonaClient.Copilot => "copilot --agent koshi-orchestrator \"ping\"",
        PersonaClient.Claude => "@koshi-orchestrator ping",
        _ => "(invoke koshi-orchestrator in your client)",
    };

    private static void AppendMcpRegistrationInstructions(List<string> lines, PersonaClient client)
    {
        if (client == PersonaClient.Copilot)
        {
            lines.Add("       [cyan]copilot mcp add koshi -- koshi-mcp[/]");
            lines.Add(string.Empty);
            lines.Add("     or add to [grey]" +
                      Markup.Escape(ClientResolver.McpConfigFile(client)) +
                      "[/]:");
        }
        else
        {
            lines.Add("     Add to [grey]" +
                      Markup.Escape(ClientResolver.McpConfigFile(client)) +
                      "[/]:");
        }

        lines.Add(string.Empty);
        foreach (var snippetLine in ClientResolver.SuggestedMcpEntry(client).Split('\n'))
        {
            var trimmed = snippetLine.TrimEnd('\r');
            lines.Add("       [grey]" + Markup.Escape(trimmed) + "[/]");
        }
    }
}
