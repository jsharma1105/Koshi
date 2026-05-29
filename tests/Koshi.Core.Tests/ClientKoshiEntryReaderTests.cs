using Koshi.Mcp.Cli.Setup;

namespace Koshi.Core.Tests;

public class ClientKoshiEntryReaderTests : IDisposable
{
    private readonly string _tmpDir;

    public ClientKoshiEntryReaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "koshi-doctor-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Returns_null_when_file_missing()
    {
        var entry = ClientKoshiEntryReader.TryRead(Path.Combine(_tmpDir, "nope.json"), out var err);
        Assert.Null(entry);
        Assert.Null(err);
    }

    [Fact]
    public void Returns_null_when_koshi_entry_missing()
    {
        var path = WriteCfg("""{"mcpServers": {"other": {"command": "x"}}}""");
        var entry = ClientKoshiEntryReader.TryRead(path, out var err);
        Assert.Null(entry);
        Assert.Null(err);
    }

    [Fact]
    public void Reports_error_when_file_is_not_valid_json()
    {
        var path = WriteCfg("not json");
        var entry = ClientKoshiEntryReader.TryRead(path, out var err);
        Assert.Null(entry);
        Assert.NotNull(err);
        Assert.Contains("not valid JSON", err);
    }

    [Fact]
    public void Parses_standard_copilot_entry()
    {
        var path = WriteCfg("""
            {
              "mcpServers": {
                "koshi": {
                  "command": "koshi-mcp",
                  "args": [],
                  "type": "local"
                }
              }
            }
            """);

        var entry = ClientKoshiEntryReader.TryRead(path, out var err);
        Assert.Null(err);
        Assert.NotNull(entry);
        Assert.Equal("koshi-mcp", entry!.Command);
        Assert.Empty(entry.Args);
        Assert.Empty(entry.Env);
    }

    [Fact]
    public void Parses_entry_with_custom_args_and_env()
    {
        var path = WriteCfg("""
            {
              "mcpServers": {
                "koshi": {
                  "command": "C:\\custom\\koshi-mcp.exe",
                  "args": ["--foo", "bar"],
                  "env": {
                    "KOSHI_PROJECT_ROOT": "C:\\proj",
                    "KOSHI_INDEX_PATH": "src"
                  }
                }
              }
            }
            """);

        var entry = ClientKoshiEntryReader.TryRead(path, out _);
        Assert.NotNull(entry);
        Assert.Equal(@"C:\custom\koshi-mcp.exe", entry!.Command);
        Assert.Equal(new[] { "--foo", "bar" }, entry.Args);
        Assert.Equal(@"C:\proj", entry.Env["KOSHI_PROJECT_ROOT"]);
        Assert.Equal("src", entry.Env["KOSHI_INDEX_PATH"]);
    }

    [Fact]
    public void Tolerates_json_comments_and_trailing_commas()
    {
        var path = WriteCfg("""
            {
              // Copilot-style config
              "mcpServers": {
                "koshi": { "command": "koshi-mcp", "args": [], }
              },
            }
            """);

        var entry = ClientKoshiEntryReader.TryRead(path, out var err);
        Assert.Null(err);
        Assert.NotNull(entry);
        Assert.Equal("koshi-mcp", entry!.Command);
    }

    [Fact]
    public void Reports_error_when_command_is_missing()
    {
        var path = WriteCfg("""
            {
              "mcpServers": {
                "koshi": { "args": [] }
              }
            }
            """);

        var entry = ClientKoshiEntryReader.TryRead(path, out var err);
        Assert.Null(entry);
        Assert.NotNull(err);
        Assert.Contains("command", err);
    }

    private string WriteCfg(string contents)
    {
        var path = Path.Combine(_tmpDir, "cfg.json");
        File.WriteAllText(path, contents);
        return path;
    }
}
