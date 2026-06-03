using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Koshi.Mcp.Cli;
using Koshi.Mcp.Prompts;
using Koshi.Mcp.Tools;
using ModelContextProtocol.Server;

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "0.0.0";

// Handle `--version` / `--help` / `config ...` / `init ...` BEFORE starting the MCP host.
// The server's normal mode is to speak JSON-RPC over stdio forever, so
// without this dispatch `koshi-mcp --version` would hang waiting for a client
// to send an `initialize` request. Keep the parser deliberately tiny and
// AOT-safe: string comparison only, no reflection, no third-party arg library.
if (args.Length > 0 && string.Equals(args[0], "config", StringComparison.Ordinal))
{
    return ConfigCommand.Run(args[1..], Console.Out, Console.Error);
}

if (args.Length > 0 && string.Equals(args[0], "init", StringComparison.Ordinal))
{
    return InitCommand.Run(args[1..], Console.Out, Console.Error, Console.In);
}

// `doctor` is provided by the separate Koshi.Agents tool. Without this
// stub, `koshi-mcp doctor` would silently fall through to the stdio host
// — looking like success but actually starting the server and hanging
// (waiting for a client `initialize` request). Catch it explicitly and
// point users at the real options.
if (args.Length > 0 && string.Equals(args[0], "doctor", StringComparison.Ordinal))
{
    Console.Error.WriteLine("koshi-mcp: 'doctor' is not a subcommand of koshi-mcp.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("To list the MCP tools this server exposes, run:");
    Console.Error.WriteLine("  koshi-mcp --list-tools");
    Console.Error.WriteLine();
    Console.Error.WriteLine("For a full client-wiring health check (probes Claude/Copilot configs),");
    Console.Error.WriteLine("install the separate Koshi.Agents tool:");
    Console.Error.WriteLine("  dotnet tool install --global Koshi.Agents");
    Console.Error.WriteLine("  koshi-agents doctor");
    return 2;
}

// Reject any unknown leading positional argument (e.g. typos like `help`,
// `status`, `start`). These would otherwise fall through to the stdio host
// and silently start the server. MCP clients invoke the configured command
// with the configured args; `McpConfigWriter.BuildKoshiEntry()` uses bare
// `koshi-mcp` with no positional args, so a positional here is user error.
// Flags (starting with `-`) are left alone so `--version`, `--help`,
// `--list-tools`, `--describe`, etc. flow through the existing handlers.
if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"koshi-mcp: unknown command '{args[0]}'.");
    Console.Error.WriteLine("Run 'koshi-mcp --help' for the list of supported commands.");
    return 2;
}

// Offline tool introspection (#67). Handled before the MCP host so the
// process exits cleanly instead of hanging on stdin awaiting an
// `initialize` request.
if (ToolIntrospectCommand.ShouldHandle(args))
{
    return ToolIntrospectCommand.Run(args);
}

foreach (var arg in args)
{
    switch (arg)
    {
        case "--version":
        case "-v":
            Console.WriteLine($"koshi-mcp {version}");
            return 0;
        case "--help":
        case "-h":
        case "-?":
            Console.WriteLine($"koshi-mcp {version}");
            Console.WriteLine();
            Console.WriteLine("Koshi MCP Server — local-first retrieval, memory, context, and quality");
            Console.WriteLine("tools for any LLM workflow. Speaks JSON-RPC over stdio.");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  koshi-mcp                    Run the MCP server on stdio (default).");
            Console.WriteLine("  koshi-mcp init               Wire a fresh project for Koshi in one command.");
            Console.WriteLine("                               Run 'koshi-mcp init --help' for flags.");
            Console.WriteLine("  koshi-mcp config <op>        Inspect/edit mcpServers.koshi.env per client.");
            Console.WriteLine("                               Run 'koshi-mcp config --help' for details.");
            Console.WriteLine("  koshi-mcp --list-tools       List every MCP tool this server exposes.");
            Console.WriteLine("  koshi-mcp --describe <tool>  Show full description + parameters for one tool.");
            Console.WriteLine("                               Add --json to either flag for machine-readable output.");
            Console.WriteLine("  koshi-mcp --version, -v      Print the version and exit.");
            Console.WriteLine("  koshi-mcp --help, -h         Print this help and exit.");
            Console.WriteLine();
            Console.WriteLine("Documentation: https://github.com/jsharma1105/Koshi#readme");
            return 0;
    }
}

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio reserves stdout for JSON-RPC traffic. Strip any default
// providers and only emit logs to stderr so we never corrupt the protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);

// Explicit, AOT-safe tool registration. WithToolsFromAssembly() relies on
// reflection over the calling assembly and is not Native AOT compatible
// (it produces IL2026 / IL3050 warnings). The generic WithTools<T>() API
// gives the trimmer/AOT compiler the type metadata it needs at build time.
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "koshi",
            Version = version,
        };
    })
    .WithStdioServerTransport()
    .WithTools<RetrievalTools>()
    .WithTools<MemoryTools>()
    .WithTools<ContextTools>()
    .WithTools<TeamTools>()
    .WithTools<DiagnosticTools>()
    .WithPrompts<SteeringPrompts>();

await builder.Build().RunAsync();
return 0;
