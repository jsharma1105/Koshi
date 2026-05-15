using Koshi.Agents.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("koshi-agents");
    config.SetApplicationVersion(typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");

    config.AddCommand<ListCommand>("list")
        .WithDescription("List all available Koshi personas.")
        .WithExample(new[] { "list" })
        .WithExample(new[] { "list", "--client", "claude" });

    config.AddCommand<ShowCommand>("show")
        .WithDescription("Show the contents of a persona (front-matter + body).")
        .WithExample(new[] { "show", "koshi-librarian" })
        .WithExample(new[] { "show", "koshi-orchestrator", "--client", "copilot" });

    config.AddCommand<InstallCommand>("install")
        .WithDescription("Install personas into a client's agents directory.")
        .WithExample(new[] { "install", "--client", "claude" })
        .WithExample(new[] { "install", "--client", "copilot", "--scope", "repo" })
        .WithExample(new[] { "install", "--client", "both", "--dry-run" });

    config.AddCommand<UninstallCommand>("uninstall")
        .WithDescription("Remove Koshi personas from a client's agents directory.")
        .WithExample(new[] { "uninstall", "--client", "claude" });

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Diagnose Koshi installation and persona wiring. Read-only.")
        .WithExample(new[] { "doctor" });

    config.SetExceptionHandler((ex, _) =>
    {
        Spectre.Console.AnsiConsole.MarkupLine(
            $"[red]error:[/] {Spectre.Console.Markup.Escape(ex.Message)}");
        if (Environment.GetEnvironmentVariable("KOSHI_AGENTS_DEBUG") == "1")
        {
            Spectre.Console.AnsiConsole.WriteException(ex);
        }
        return -1;
    });
});

return await app.RunAsync(args);
