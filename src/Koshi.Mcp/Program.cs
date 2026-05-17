using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Koshi.Mcp.Tools;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio reserves stdout for JSON-RPC traffic. Strip any default
// providers and only emit logs to stderr so we never corrupt the protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "0.0.0";

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
