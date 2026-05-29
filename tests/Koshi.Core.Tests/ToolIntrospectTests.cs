using System.Text.Json;
using System.Text.Json.Nodes;
using Koshi.Mcp.Cli;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for the offline tool-introspection CLI added in #67 —
/// <see cref="ToolCatalog"/> + <see cref="ToolIntrospectCommand"/>.
/// These exercise the user-facing surface of <c>koshi-mcp --list-tools</c>
/// and <c>koshi-mcp --describe &lt;tool&gt; [--json]</c>.
/// All assertions go through the public/internal CLI APIs — no shell
/// process spawning so the suite stays fast on every platform.
/// </summary>
public sealed class ToolIntrospectTests
{
    // ─────────────────────────────── ToolCatalog ────────────────────────────────

    [Fact]
    public void All_returns_every_registered_mcp_tool()
    {
        var tools = ToolCatalog.All;

        // 24 tools across 5 classes is the current contract. If a new tool
        // is added (or an old one removed), update this assertion AND
        // verify the new tool appears in --list-tools output.
        Assert.Equal(24, tools.Count);

        // No duplicate names (catalog uses tool-name OrdinalIgnoreCase lookup).
        var names = tools.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Names are sorted Ordinal so output is stable across runs.
        var sorted = names.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, names);
    }

    [Fact]
    public void All_tool_names_start_with_koshi_prefix()
    {
        foreach (var t in ToolCatalog.All)
            Assert.StartsWith("koshi_", t.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void All_tools_have_nonempty_description_and_class_name()
    {
        foreach (var t in ToolCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description),
                $"tool '{t.Name}' has empty description");
            Assert.False(string.IsNullOrWhiteSpace(t.ClassName),
                $"tool '{t.Name}' has empty class name");
        }
    }

    [Fact]
    public void All_known_tool_classes_contribute_tools()
    {
        var byClass = ToolCatalog.All.GroupBy(t => t.ClassName).ToDictionary(g => g.Key, g => g.Count());

        // Every registered McpServerToolType must surface at least one tool.
        Assert.True(byClass.GetValueOrDefault("RetrievalTools") >= 1);
        Assert.True(byClass.GetValueOrDefault("MemoryTools") >= 1);
        Assert.True(byClass.GetValueOrDefault("ContextTools") >= 1);
        Assert.True(byClass.GetValueOrDefault("TeamTools") >= 1);
        Assert.True(byClass.GetValueOrDefault("DiagnosticTools") >= 1);
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_unknown()
    {
        var lower = ToolCatalog.Find("koshi_search");
        var upper = ToolCatalog.Find("KOSHI_SEARCH");
        Assert.NotNull(lower);
        Assert.NotNull(upper);
        Assert.Same(lower, upper);

        Assert.Null(ToolCatalog.Find("does_not_exist"));
        Assert.Null(ToolCatalog.Find(""));
        Assert.Null(ToolCatalog.Find("   "));
    }

    [Fact]
    public void Score_turn_parameters_match_signature_anti_regression()
    {
        // koshi_score_turn was called out in #67's demo issue — pin its
        // parameter list so future signature changes break a test rather
        // than silently confuse offline introspection consumers.
        var tool = ToolCatalog.Find("koshi_score_turn");
        Assert.NotNull(tool);

        var paramNames = tool!.Parameters.Select(p => p.Name).ToList();
        Assert.Equal(
            new[]
            {
                "teamId", "retrievedChunks", "memoriesRecalled",
                "budgetUtilization", "cacheRatio", "latencyMs",
                "userRating", "issues", "tokensUsed",
            },
            paramNames);

        // teamId is the only required parameter.
        var teamId = tool.Parameters.Single(p => p.Name == "teamId");
        Assert.True(teamId.Required);
        Assert.Null(teamId.DefaultValue);
        Assert.Equal("string", teamId.Type);

        // Numeric / string defaults round-trip in their natural form.
        var topK = tool.Parameters.Single(p => p.Name == "retrievedChunks");
        Assert.False(topK.Required);
        Assert.Equal("0", topK.DefaultValue);

        var issues = tool.Parameters.Single(p => p.Name == "issues");
        Assert.False(issues.Required);
        // Optional parameter whose default IS null → DefaultValue is null
        // (not the string "null"). Disambiguates from a literal "null"
        // string default at the JSON layer.
        Assert.Null(issues.DefaultValue);
    }

    [Fact]
    public void Friendly_type_names_map_common_clr_types_to_schema_aliases()
    {
        Assert.Equal("string", ToolCatalog.FriendlyTypeName(typeof(string)));
        Assert.Equal("integer", ToolCatalog.FriendlyTypeName(typeof(int)));
        Assert.Equal("integer", ToolCatalog.FriendlyTypeName(typeof(long)));
        Assert.Equal("number", ToolCatalog.FriendlyTypeName(typeof(float)));
        Assert.Equal("number", ToolCatalog.FriendlyTypeName(typeof(double)));
        Assert.Equal("boolean", ToolCatalog.FriendlyTypeName(typeof(bool)));
        Assert.Equal("integer?", ToolCatalog.FriendlyTypeName(typeof(int?)));
    }

    [Fact]
    public void Format_default_uses_invariant_culture_for_floats()
    {
        // Reproducer for a locale-sensitive default like 0.5f: the JSON
        // output must use '.' as the decimal separator regardless of the
        // running OS locale, otherwise CI on a de-DE / fr-FR runner would
        // emit "0,5" and break JSON parsing.
        Assert.Equal("0.5", ToolCatalog.FormatDefault(0.5f));
        Assert.Equal("3.14", ToolCatalog.FormatDefault(3.14d));
        Assert.Equal("null", ToolCatalog.FormatDefault(null));
        Assert.Equal("true", ToolCatalog.FormatDefault(true));
        Assert.Equal("false", ToolCatalog.FormatDefault(false));
        Assert.Equal("All", ToolCatalog.FormatDefault("All"));
        Assert.Equal("42", ToolCatalog.FormatDefault(42));
    }

    // ─────────────────────────── ToolIntrospectCommand ──────────────────────────

    [Fact]
    public void ShouldHandle_only_triggers_on_introspection_flags()
    {
        Assert.True(ToolIntrospectCommand.ShouldHandle(new[] { "--list-tools" }));
        Assert.True(ToolIntrospectCommand.ShouldHandle(new[] { "--describe", "koshi_search" }));
        Assert.True(ToolIntrospectCommand.ShouldHandle(new[] { "--describe=koshi_search" }));
        Assert.True(ToolIntrospectCommand.ShouldHandle(new[] { "--list-tools", "--json" }));

        Assert.False(ToolIntrospectCommand.ShouldHandle(Array.Empty<string>()));
        Assert.False(ToolIntrospectCommand.ShouldHandle(new[] { "--version" }));
        Assert.False(ToolIntrospectCommand.ShouldHandle(new[] { "--help" }));
        Assert.False(ToolIntrospectCommand.ShouldHandle(new[] { "config", "get" }));
        Assert.False(ToolIntrospectCommand.ShouldHandle(new[] { "--json" }), "--json alone is not enough");
    }

    [Fact]
    public void List_tools_human_output_lists_every_tool_and_exits_zero()
    {
        var (exit, stdout, stderr) = Run("--list-tools");
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);

        foreach (var t in ToolCatalog.All)
            Assert.Contains(t.Name, stdout);

        Assert.Contains($"Koshi MCP tools ({ToolCatalog.All.Count}):", stdout);
        Assert.Contains("--describe", stdout);
    }

    [Fact]
    public void List_tools_json_output_is_parseable_and_complete()
    {
        var (exit, stdout, stderr) = Run("--list-tools", "--json");
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);

        var arr = JsonNode.Parse(stdout)?.AsArray();
        Assert.NotNull(arr);
        Assert.Equal(ToolCatalog.All.Count, arr!.Count);

        foreach (var node in arr)
        {
            var obj = node!.AsObject();
            Assert.True(obj.ContainsKey("name"), "list entry missing 'name'");
            Assert.True(obj.ContainsKey("description"), "list entry missing 'description'");
            Assert.True(obj.ContainsKey("parameter_count"), "list entry missing 'parameter_count'");
            Assert.False(string.IsNullOrWhiteSpace(obj["name"]!.GetValue<string>()));
        }
    }

    [Fact]
    public void Describe_unknown_tool_returns_exit_1_and_writes_to_stderr()
    {
        var (exit, stdout, stderr) = Run("--describe", "not_a_real_tool");
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Contains("unknown tool 'not_a_real_tool'", stderr);
        Assert.Contains("--list-tools", stderr);
    }

    [Fact]
    public void Describe_without_argument_returns_exit_2()
    {
        var (exit, stdout, stderr) = Run("--describe");
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("--describe requires a tool name", stderr);
    }

    [Fact]
    public void Describe_with_empty_equals_arg_returns_exit_2()
    {
        var (exit, stdout, stderr) = Run("--describe=");
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("requires a tool name", stderr);
    }

    [Fact]
    public void Describe_known_tool_human_output_contains_class_and_params()
    {
        var (exit, stdout, stderr) = Run("--describe", "koshi_search");
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);

        Assert.Contains("koshi_search", stdout);
        Assert.Contains("Class: RetrievalTools", stdout);
        Assert.Contains("Parameters", stdout);
        // Quoted string defaults render with surrounding quotes.
        Assert.Contains("query : string (required)", stdout);
    }

    [Fact]
    public void Describe_equals_syntax_works()
    {
        var (exit, stdout, _) = Run("--describe=koshi_version");
        Assert.Equal(0, exit);
        Assert.Contains("koshi_version", stdout);
        Assert.Contains("Class: DiagnosticTools", stdout);
    }

    [Fact]
    public void Describe_json_output_round_trips_through_system_text_json()
    {
        var (exit, stdout, stderr) = Run("--describe", "koshi_score_turn", "--json");
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);

        var obj = JsonNode.Parse(stdout)?.AsObject();
        Assert.NotNull(obj);
        Assert.Equal("koshi_score_turn", obj!["name"]!.GetValue<string>());
        Assert.Equal("TeamTools", obj["class_name"]!.GetValue<string>());

        var parameters = obj["parameters"]!.AsArray();
        Assert.Equal(9, parameters.Count);

        var teamId = parameters[0]!.AsObject();
        Assert.Equal("teamId", teamId["name"]!.GetValue<string>());
        Assert.Equal("string", teamId["type"]!.GetValue<string>());
        Assert.True(teamId["required"]!.GetValue<bool>());
        // Required parameters carry a JSON null default (not the string "null").
        Assert.Equal(JsonValueKind.Null, teamId["default_value"]?.GetValue<JsonElement>().ValueKind ?? JsonValueKind.Null);
    }

    [Fact]
    public void List_tools_and_describe_together_return_exit_2()
    {
        var (exit, stdout, stderr) = Run("--list-tools", "--describe", "koshi_search");
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("mutually exclusive", stderr);
    }

    [Fact]
    public void Describe_followed_by_flag_returns_exit_2_not_unknown_tool()
    {
        // Regression: previously `--describe --json` consumed '--json' as
        // the tool name and exited 1 ("unknown tool '--json'"). The right
        // behaviour is a usage error (exit 2) so users get a clear message.
        var (exit, stdout, stderr) = Run("--describe", "--json");
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("--describe requires a tool name", stderr);
    }

    [Fact]
    public void Describe_followed_by_list_tools_returns_exit_2()
    {
        var (exit, _, stderr) = Run("--describe", "--list-tools");
        Assert.Equal(2, exit);
        Assert.Contains("--describe requires a tool name", stderr);
    }

    [Fact]
    public void Unrecognized_flag_after_list_tools_returns_exit_2()
    {
        // Typos like `--jsno` must fail instead of silently producing
        // human output (and tricking the user into thinking JSON was off).
        var (exit, stdout, stderr) = Run("--list-tools", "--jsno");
        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("unrecognized argument '--jsno'", stderr);
    }

    [Fact]
    public void Trailing_positional_after_describe_returns_exit_2()
    {
        var (exit, _, stderr) = Run("--describe", "koshi_search", "extra");
        Assert.Equal(2, exit);
        Assert.Contains("unrecognized argument 'extra'", stderr);
    }

    // ───────────────────────────────── helpers ──────────────────────────────────

    private static (int exit, string stdout, string stderr) Run(params string[] args)
    {
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        var exit = ToolIntrospectCommand.Run(args, outW, errW);
        return (exit, outW.ToString(), errW.ToString());
    }
}
