using System.Text.Json;
using System.Text.Json.Nodes;
using Koshi.Mcp.Cli;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for the <c>koshi-mcp config</c> CLI surface added in #78 Gap B —
/// covering <see cref="McpClientPaths"/>, <see cref="DotEnvParser"/>,
/// <see cref="EnvConfigEditor"/>, and <see cref="ConfigCommand"/>.
/// All tests use temp <c>homeDir</c>/<c>appDataDir</c> overrides so they
/// never touch a real user's MCP config.
/// </summary>
public sealed class ConfigCommandTests
{
    // ───────────────────────────────── McpClientPaths ──────────────────────────────────

    [Fact]
    public void Resolve_normalizes_client_name_case_insensitively()
    {
        var home = NewTempDir();
        try
        {
            var lower = McpClientPaths.Resolve("copilot", home);
            var upper = McpClientPaths.Resolve("COPILOT", home);
            var mixed = McpClientPaths.Resolve(" Copilot ", home);
            Assert.Equal(lower, upper);
            Assert.Equal(lower, mixed);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Resolve_throws_for_unknown_client()
    {
        Assert.Throws<ArgumentException>(() => McpClientPaths.Resolve("vim"));
        Assert.Throws<ArgumentException>(() => McpClientPaths.Resolve(""));
    }

    [Fact]
    public void Resolve_copilot_uses_dot_copilot_under_home_on_every_os()
    {
        var home = NewTempDir();
        try
        {
            var path = McpClientPaths.Resolve("copilot", home);
            Assert.Equal(Path.Join(home, ".copilot", "mcp-config.json"), path);
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void Resolve_claude_uses_os_specific_path()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        try
        {
            var path = McpClientPaths.Resolve("claude", home, appData);
            string expected;
            if (OperatingSystem.IsWindows())
                expected = Path.Join(appData, "Claude", "claude_desktop_config.json");
            else if (OperatingSystem.IsMacOS())
                expected = Path.Join(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
            else
                expected = Path.Join(home, ".config", "Claude", "claude_desktop_config.json");
            Assert.Equal(expected, path);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
        }
    }

    /// <summary>
    /// Path-math parity test: <see cref="McpClientPaths.Resolve(string,string?,string?)"/>
    /// must agree with the canonical layout documented in
    /// <c>Koshi.Agents.Internal.ClientResolver.McpConfigFile</c>. The expected
    /// values are reproduced here verbatim (rather than referencing
    /// <c>Koshi.Agents</c>) because <c>Koshi.Mcp</c> intentionally has no
    /// dependency on the soon-to-be-deprecated agents assembly. If you change
    /// one resolver, change the other and update this test.
    /// </summary>
    [Fact]
    public void Resolve_matches_ClientResolver_canonical_layout()
    {
        var home = NewTempDir();
        var appData = NewTempDir();
        try
        {
            var copilot = McpClientPaths.Resolve("copilot", home, appData);
            Assert.Equal(Path.Join(home, ".copilot", "mcp-config.json"), copilot);

            var claude = McpClientPaths.Resolve("claude", home, appData);
            if (OperatingSystem.IsWindows())
                Assert.Equal(Path.Join(appData, "Claude", "claude_desktop_config.json"), claude);
            else if (OperatingSystem.IsMacOS())
                Assert.Equal(Path.Join(home, "Library", "Application Support", "Claude", "claude_desktop_config.json"), claude);
            else
                Assert.Equal(Path.Join(home, ".config", "Claude", "claude_desktop_config.json"), claude);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public void KnownClients_only_contains_claude_and_copilot()
    {
        Assert.Equal(new[] { "claude", "copilot" }, McpClientPaths.KnownClients.ToArray());
    }

    // ───────────────────────────────── DotEnvParser ──────────────────────────────────

    [Fact]
    public void DotEnvParser_parses_simple_pairs()
    {
        var ok = DotEnvParser.TryParse(new[] { "KEY=value", "OTHER=other" }, out var values, out var err);
        Assert.True(ok, err);
        Assert.Equal("value", values!["KEY"]);
        Assert.Equal("other", values["OTHER"]);
    }

    [Fact]
    public void DotEnvParser_skips_blank_and_comment_lines()
    {
        var ok = DotEnvParser.TryParse(new[] { "", "# a comment", "   # indented comment", "FOO=bar" },
            out var values, out var err);
        Assert.True(ok, err);
        Assert.Single(values!);
        Assert.Equal("bar", values!["FOO"]);
    }

    [Fact]
    public void DotEnvParser_strips_double_and_single_quotes()
    {
        var ok = DotEnvParser.TryParse(new[]
        {
            "DOUBLE=\"value with spaces\"",
            "SINGLE='also spaces'",
        }, out var values, out var err);
        Assert.True(ok, err);
        Assert.Equal("value with spaces", values!["DOUBLE"]);
        Assert.Equal("also spaces", values["SINGLE"]);
    }

    [Fact]
    public void DotEnvParser_allows_empty_value()
    {
        var ok = DotEnvParser.TryParse(new[] { "EMPTY=" }, out var values, out var err);
        Assert.True(ok, err);
        Assert.Equal("", values!["EMPTY"]);
    }

    [Fact]
    public void DotEnvParser_strips_trailing_inline_comment_on_unquoted_value()
    {
        var ok = DotEnvParser.TryParse(new[] { "FOO=bar # ignored" }, out var values, out var err);
        Assert.True(ok, err);
        Assert.Equal("bar", values!["FOO"]);
    }

    [Fact]
    public void DotEnvParser_rejects_unterminated_quote_with_line_number()
    {
        var ok = DotEnvParser.TryParse(new[] { "GOOD=ok", "BAD=\"unterminated" }, out _, out var err);
        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Contains("line 2", err!);
    }

    [Fact]
    public void DotEnvParser_rejects_invalid_key()
    {
        var ok = DotEnvParser.TryParse(new[] { "1BAD=x" }, out _, out var err);
        Assert.False(ok);
        Assert.Contains("line 1", err!);
        Assert.Contains("invalid key", err);
    }

    [Fact]
    public void DotEnvParser_rejects_missing_equals()
    {
        var ok = DotEnvParser.TryParse(new[] { "NO_EQUALS_HERE" }, out _, out var err);
        Assert.False(ok);
        Assert.Contains("line 1", err!);
    }

    // ───────────────────────────────── EnvConfigEditor ──────────────────────────────────

    [Fact]
    public void EnvConfigEditor_get_returns_value_when_present()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": { ""FOO"": ""bar"" } } } }");

        var r = EnvConfigEditor.Get("copilot", "FOO", sandbox.Home);
        Assert.Equal(EnvEditOutcome.Read, r.Outcome);
        Assert.Equal("bar", r.Value);
    }

    [Fact]
    public void EnvConfigEditor_get_returns_null_value_when_key_absent()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");

        var r = EnvConfigEditor.Get("copilot", "MISSING", sandbox.Home);
        Assert.Equal(EnvEditOutcome.Read, r.Outcome);
        Assert.Null(r.Value);
    }

    [Fact]
    public void EnvConfigEditor_getall_returns_every_env_var()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": { ""A"": ""1"", ""B"": ""2"" } } } }");

        var r = EnvConfigEditor.GetAll("copilot", sandbox.Home);
        Assert.Equal(EnvEditOutcome.Read, r.Outcome);
        Assert.Equal(2, r.AllValues!.Count);
        Assert.Equal("1", r.AllValues["A"]);
        Assert.Equal("2", r.AllValues["B"]);
    }

    [Fact]
    public void EnvConfigEditor_set_creates_env_object_if_missing_and_writes_backup()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"" } } }");

        var r = EnvConfigEditor.Set("copilot", "FOO", "bar", sandbox.Home);
        Assert.Equal(EnvEditOutcome.Changed, r.Outcome);
        Assert.True(File.Exists(r.BackupPath));
        Assert.Equal(path + ".bak", r.BackupPath);

        // Re-read to confirm persisted
        var get = EnvConfigEditor.Get("copilot", "FOO", sandbox.Home);
        Assert.Equal("bar", get.Value);
    }

    [Fact]
    public void EnvConfigEditor_set_is_unchanged_when_value_already_matches()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": { ""FOO"": ""bar"" } } } }");

        var r = EnvConfigEditor.Set("copilot", "FOO", "bar", sandbox.Home);
        Assert.Equal(EnvEditOutcome.Unchanged, r.Outcome);
    }

    [Fact]
    public void EnvConfigEditor_unset_removes_key()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": { ""FOO"": ""bar"", ""KEEP"": ""me"" } } } }");

        var r = EnvConfigEditor.Unset("copilot", "FOO", sandbox.Home);
        Assert.Equal(EnvEditOutcome.Changed, r.Outcome);

        var all = EnvConfigEditor.GetAll("copilot", sandbox.Home);
        Assert.False(all.AllValues!.ContainsKey("FOO"));
        Assert.True(all.AllValues.ContainsKey("KEEP"));
    }

    [Fact]
    public void EnvConfigEditor_unset_is_unchanged_when_key_absent()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");

        var r = EnvConfigEditor.Unset("copilot", "MISSING", sandbox.Home);
        Assert.Equal(EnvEditOutcome.Unchanged, r.Outcome);
    }

    [Fact]
    public void EnvConfigEditor_apply_merges_multiple_keys()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": { ""A"": ""old"" } } } }");

        var r = EnvConfigEditor.Apply("copilot", new Dictionary<string, string>
        {
            ["A"] = "new",
            ["B"] = "added",
        }, sandbox.Home);
        Assert.Equal(EnvEditOutcome.Changed, r.Outcome);

        var all = EnvConfigEditor.GetAll("copilot", sandbox.Home);
        Assert.Equal("new", all.AllValues!["A"]);
        Assert.Equal("added", all.AllValues["B"]);
    }

    [Fact]
    public void EnvConfigEditor_apply_is_unchanged_when_every_value_already_matches()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": { ""A"": ""1"" } } } }");

        var r = EnvConfigEditor.Apply("copilot", new Dictionary<string, string> { ["A"] = "1" }, sandbox.Home);
        Assert.Equal(EnvEditOutcome.Unchanged, r.Outcome);
    }

    [Fact]
    public void EnvConfigEditor_preserves_other_mcpServers_entries()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} }, ""other"": { ""command"": ""other-mcp"" } } }");

        EnvConfigEditor.Set("copilot", "X", "1", sandbox.Home);

        var path = McpClientPaths.Resolve("copilot", sandbox.Home);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.NotNull(json["mcpServers"]!["other"]);
        Assert.Equal("other-mcp", json["mcpServers"]!["other"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void EnvConfigEditor_returns_ConfigMissing_when_file_absent()
    {
        using var sandbox = new Sandbox();
        // intentionally do not write a config

        var r = EnvConfigEditor.Get("copilot", "X", sandbox.Home);
        Assert.Equal(EnvEditOutcome.ConfigMissing, r.Outcome);
    }

    [Fact]
    public void EnvConfigEditor_returns_KoshiEntryMissing_when_no_koshi_server()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""other"": { ""command"": ""other"" } } }");

        var r = EnvConfigEditor.Set("copilot", "X", "1", sandbox.Home);
        Assert.Equal(EnvEditOutcome.KoshiEntryMissing, r.Outcome);
        Assert.Contains("koshi-agents install", r.ErrorMessage);
    }

    [Fact]
    public void EnvConfigEditor_returns_InvalidJson_when_file_corrupt()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig("{ this is not json");

        var r = EnvConfigEditor.Get("copilot", "X", sandbox.Home);
        Assert.Equal(EnvEditOutcome.InvalidJson, r.Outcome);
    }

    [Fact]
    public void EnvConfigEditor_does_not_leave_temp_files_behind()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");

        EnvConfigEditor.Set("copilot", "X", "1", sandbox.Home);

        var dir = Path.GetDirectoryName(path)!;
        var leftover = Directory.GetFiles(dir, "*.tmp");
        Assert.Empty(leftover);
    }

    // ───────────────────────────────── ConfigCommand ──────────────────────────────────

    [Fact]
    public void ConfigCommand_get_prints_value_to_stdout_and_returns_0()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": { ""FOO"": ""bar"" } } } }");

        var (stdout, _, code) = RunInSandbox(sandbox, "get", "FOO", "--client", "copilot");
        Assert.Equal(0, code);
        Assert.Equal($"bar{Environment.NewLine}", stdout);
    }

    [Fact]
    public void ConfigCommand_get_missing_key_returns_1_and_no_stdout()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");

        var (stdout, stderr, code) = RunInSandbox(sandbox, "get", "MISSING", "--client", "copilot");
        Assert.Equal(1, code);
        Assert.Equal("", stdout);
        Assert.Contains("not set", stderr);
    }

    [Fact]
    public void ConfigCommand_get_all_prints_sorted_key_value_pairs()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": { ""Z"": ""last"", ""A"": ""first"" } } } }");

        var (stdout, _, code) = RunInSandbox(sandbox, "get", "--all", "--client", "copilot");
        Assert.Equal(0, code);
        var lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "A=first", "Z=last" }, lines);
    }

    [Fact]
    public void ConfigCommand_set_writes_value()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");

        var (_, _, code) = RunInSandbox(sandbox, "set", "FOO=bar", "--client", "copilot");
        Assert.Equal(0, code);

        var r = EnvConfigEditor.Get("copilot", "FOO", sandbox.Home);
        Assert.Equal("bar", r.Value);
    }

    [Fact]
    public void ConfigCommand_set_with_value_stdin_reads_from_stdin()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");

        var (_, _, code) = RunInSandboxWithStdin(sandbox, "super-secret-token\n",
            "set", "TOKEN", "--value-stdin", "--client", "copilot");
        Assert.Equal(0, code);

        var r = EnvConfigEditor.Get("copilot", "TOKEN", sandbox.Home, sandbox.AppData);
        Assert.Equal("super-secret-token", r.Value);
    }

    [Fact]
    public void ConfigCommand_set_rejects_bare_key_without_value_stdin()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");

        var (_, stderr, code) = RunInSandbox(sandbox, "set", "FOO", "--client", "copilot");
        Assert.Equal(2, code);
        Assert.Contains("KEY=VALUE", stderr);
    }

    [Fact]
    public void ConfigCommand_unset_removes_key()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": { ""FOO"": ""bar"" } } } }");

        var (_, _, code) = RunInSandbox(sandbox, "unset", "FOO", "--client", "copilot");
        Assert.Equal(0, code);

        var r = EnvConfigEditor.Get("copilot", "FOO", sandbox.Home);
        Assert.Null(r.Value);
    }

    [Fact]
    public void ConfigCommand_apply_reads_dotenv_file()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");

        var envFile = Path.Join(sandbox.Home, ".env");
        File.WriteAllText(envFile, "FOO=bar\nBAZ=\"quoted value\"\n# comment\n");

        var (_, _, code) = RunInSandbox(sandbox, "apply", envFile, "--client", "copilot");
        Assert.Equal(0, code);

        var all = EnvConfigEditor.GetAll("copilot", sandbox.Home);
        Assert.Equal("bar", all.AllValues!["FOO"]);
        Assert.Equal("quoted value", all.AllValues["BAZ"]);
    }

    [Fact]
    public void ConfigCommand_apply_rejects_invalid_dotenv_atomically()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");

        var envFile = Path.Join(sandbox.Home, ".env");
        File.WriteAllText(envFile, "GOOD=ok\nBAD line without equals\n");

        var (_, stderr, code) = RunInSandbox(sandbox, "apply", envFile, "--client", "copilot");
        Assert.Equal(1, code);
        Assert.Contains("line 2", stderr);

        // Crucially, nothing was applied
        var all = EnvConfigEditor.GetAll("copilot", sandbox.Home);
        Assert.False(all.AllValues!.ContainsKey("GOOD"));
    }

    [Fact]
    public void ConfigCommand_unknown_op_returns_2()
    {
        using var sandbox = new Sandbox();
        var (_, stderr, code) = RunInSandbox(sandbox, "frobnicate");
        Assert.Equal(2, code);
        Assert.Contains("unknown config operation", stderr);
    }

    [Fact]
    public void ConfigCommand_get_without_client_returns_2()
    {
        using var sandbox = new Sandbox();
        var (_, stderr, code) = RunInSandbox(sandbox, "get", "FOO");
        Assert.Equal(2, code);
        Assert.Contains("--client is required", stderr);
    }

    [Fact]
    public void ConfigCommand_get_with_invalid_client_returns_2()
    {
        using var sandbox = new Sandbox();
        var (_, stderr, code) = RunInSandbox(sandbox, "get", "FOO", "--client", "vim");
        Assert.Equal(2, code);
        Assert.Contains("Unknown MCP client", stderr);
    }

    [Fact]
    public void ConfigCommand_client_all_returns_2_when_no_clients_installed()
    {
        using var sandbox = new Sandbox();
        // No config files written. Both clients absent.

        var (_, stderr, code) = RunInSandbox(sandbox, "get", "--all", "--client", "all");
        Assert.Equal(2, code);
        Assert.Contains("no Koshi-aware MCP clients", stderr);
    }

    [Fact]
    public void ConfigCommand_client_all_set_writes_to_each_installed_client()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");
        sandbox.WriteClaudeConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");

        var (_, _, code) = RunInSandbox(sandbox, "set", "FOO=bar", "--client", "all");
        Assert.Equal(0, code);

        Assert.Equal("bar", EnvConfigEditor.Get("copilot", "FOO", sandbox.Home, sandbox.AppData).Value);
        Assert.Equal("bar", EnvConfigEditor.Get("claude", "FOO", sandbox.Home, sandbox.AppData).Value);
    }

    [Fact]
    public void ConfigCommand_client_all_skips_clients_missing_koshi_entry()
    {
        using var sandbox = new Sandbox();
        sandbox.WriteCopilotConfig(@"{ ""mcpServers"": { ""koshi"": { ""command"": ""koshi-mcp"", ""env"": {} } } }");
        sandbox.WriteClaudeConfig(@"{ ""mcpServers"": { ""other"": { ""command"": ""other"" } } }");

        var (_, stderr, code) = RunInSandbox(sandbox, "set", "FOO=bar", "--client", "all");
        // copilot succeeded but claude is missing koshi entry — overall exit is 0 because
        // we only require at least one installed-koshi client to succeed; missing entries
        // are warnings, not failures. (Per Gap B spec: skip-with-warning, do not auto-create.)
        Assert.Equal(0, code);
        Assert.Contains("claude", stderr);
        Assert.Equal("bar", EnvConfigEditor.Get("copilot", "FOO", sandbox.Home).Value);
    }

    // ───────────────────────────────── helpers ──────────────────────────────────

    private static string NewTempDir()
    {
        var dir = Path.Join(Path.GetTempPath(), $"koshi-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private (string Stdout, string Stderr, int ExitCode) RunInSandbox(Sandbox sandbox, params string[] args)
    {
        using var outBuf = new StringWriter();
        using var errBuf = new StringWriter();
        var code = ConfigCommand.Run(args, outBuf, errBuf, sandbox.Home, sandbox.AppData, stdinReader: null);
        return (outBuf.ToString(), errBuf.ToString(), code);
    }

    private (string Stdout, string Stderr, int ExitCode) RunInSandboxWithStdin(Sandbox sandbox, string stdinContent, params string[] args)
    {
        using var outBuf = new StringWriter();
        using var errBuf = new StringWriter();
        using var inBuf = new StringReader(stdinContent);
        var code = ConfigCommand.Run(args, outBuf, errBuf, sandbox.Home, sandbox.AppData, inBuf);
        return (outBuf.ToString(), errBuf.ToString(), code);
    }

    private sealed class Sandbox : IDisposable
    {
        public string Home { get; }
        public string AppData { get; }

        public Sandbox()
        {
            Home = NewTempDir();
            AppData = NewTempDir();
        }

        public string WriteCopilotConfig(string json)
        {
            var path = Path.Join(Home, ".copilot", "mcp-config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            return path;
        }

        public string WriteClaudeConfig(string json)
        {
            string path;
            if (OperatingSystem.IsWindows())
                path = Path.Join(AppData, "Claude", "claude_desktop_config.json");
            else if (OperatingSystem.IsMacOS())
                path = Path.Join(Home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
            else
                path = Path.Join(Home, ".config", "Claude", "claude_desktop_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            return path;
        }

        public void Dispose()
        {
            TryDeleteDirectory(Home);
            TryDeleteDirectory(AppData);
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup: temp-dir teardown failures in tests are
                // diagnostic noise (typically a stray file handle on Windows),
                // not real failures. Trace so it shows up under debug listeners.
                System.Diagnostics.Trace.WriteLine($"Sandbox cleanup failed for '{path}': {ex.Message}");
            }
        }
    }
}
