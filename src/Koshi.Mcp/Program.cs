using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Koshi.Mcp.Tools;
using ModelContextProtocol.Server;

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "0.0.0";

// Handle `--version` / `--help` BEFORE starting the MCP host. The server's
// normal mode is to speak JSON-RPC over stdio forever, so without this
// dispatch `koshi-mcp --version` would hang waiting for a client to send
// an `initialize` request. Keep the parser deliberately tiny and AOT-safe:
// string comparison only, no reflection, no third-party arg library.
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
            Console.WriteLine("  koshi-mcp                Run the MCP server on stdio (default).");
            Console.WriteLine("  koshi-mcp --version, -v  Print the version and exit.");
            Console.WriteLine("  koshi-mcp --help, -h     Print this help and exit.");
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
    .WithTools<DiagnosticTools>();

await builder.Build().RunAsync();
return 0;
