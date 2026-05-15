// Quick smoke test for the Koshi MCP server.
// Spawns the server, sends initialize + tools/list, prints stdout responses.
using System.Diagnostics;
using System.Text;

var serverDll = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Koshi.Mcp", "bin", "Release", "net10.0", "koshi-mcp.dll"));

if (!File.Exists(serverDll))
{
    Console.Error.WriteLine($"Server DLL not found: {serverDll}");
    return 1;
}

Console.Error.WriteLine($"Launching: {serverDll}");

var psi = new ProcessStartInfo("dotnet", $"\"{serverDll}\"")
{
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    StandardInputEncoding = Encoding.UTF8,
    StandardOutputEncoding = Encoding.UTF8,
};

var proc = Process.Start(psi)!;

_ = Task.Run(async () =>
{
    string? line;
    while ((line = await proc.StandardError.ReadLineAsync()) is not null)
    {
        Console.Error.WriteLine($"[stderr] {line}");
    }
});

var responses = new List<string>();
var stdoutTask = Task.Run(async () =>
{
    string? line;
    while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
    {
        responses.Add(line);
        Console.WriteLine($"[stdout] {line}");
    }
});

async Task Send(string json)
{
    await proc.StandardInput.WriteLineAsync(json);
    await proc.StandardInput.FlushAsync();
}

await Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"0.1"}}}""");
await Task.Delay(500);
await Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
await Task.Delay(200);
await Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
await Task.Delay(1000);
await Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"koshi_version","arguments":{}}}""");
await Task.Delay(800);

var indexPath = Path.GetFullPath(AppContext.BaseDirectory).Replace("\\", "/");
var indexJson = "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"koshi_index_directory\",\"arguments\":{\"path\":\"" + indexPath + "\",\"pattern\":\"*.cs\"}}}";
await Send(indexJson);
await Task.Delay(2000);

await Send("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"koshi_search","arguments":{"query":"smoke test","topK":2}}}""");
await Task.Delay(1500);
await Send("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"koshi_health","arguments":{}}}""");
await Task.Delay(1000);

proc.StandardInput.Close();
proc.WaitForExit(5000);
if (!proc.HasExited) proc.Kill();

await Task.WhenAny(stdoutTask, Task.Delay(2000));

Console.WriteLine();
Console.WriteLine($"=== Smoke test: received {responses.Count} responses ===");
foreach (var r in responses.Take(5))
    Console.WriteLine($"  - {r[..Math.Min(140, r.Length)]}{(r.Length > 140 ? "..." : "")}");

return responses.Count >= 3 ? 0 : 2;
