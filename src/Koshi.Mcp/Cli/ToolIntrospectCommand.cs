using System.Text.Json;

namespace Koshi.Mcp.Cli;

/// <summary>
/// Parses and dispatches <c>koshi-mcp --list-tools</c> and
/// <c>koshi-mcp --describe &lt;tool&gt; [--json]</c> for offline tool
/// introspection (issue #67).
/// <para>
/// Output discipline: human and JSON output both go to stdout; usage /
/// error messages go to stderr. This lets callers pipe <c>--json</c>
/// straight into <c>jq</c> without interleaved diagnostics.
/// </para>
/// <para>
/// Exit codes:
/// </para>
/// <list type="bullet">
///   <item><c>0</c> — listing or describe succeeded.</item>
///   <item><c>1</c> — tool name was not found in the catalog.</item>
///   <item><c>2</c> — usage / argument error
///                    (e.g. <c>--describe</c> with no tool name).</item>
/// </list>
/// </summary>
internal static class ToolIntrospectCommand
{
    /// <summary>Production entry point used by <c>Program.cs</c>.</summary>
    public static int Run(string[] args) => Run(args, Console.Out, Console.Error);

    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        bool listTools = false;
        bool json = false;
        string? describe = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--list-tools":
                    listTools = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--describe":
                    if (i + 1 >= args.Length ||
                        args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        stderr.WriteLine("error: --describe requires a tool name (e.g. --describe koshi_search)");
                        return 2;
                    }
                    describe = args[++i];
                    break;
                default:
                    if (a.StartsWith("--describe=", StringComparison.Ordinal))
                    {
                        describe = a["--describe=".Length..];
                        if (string.IsNullOrWhiteSpace(describe))
                        {
                            stderr.WriteLine("error: --describe= requires a tool name (e.g. --describe=koshi_search)");
                            return 2;
                        }
                    }
                    else
                    {
                        // Unknown flag or stray positional. ShouldHandle has
                        // already gated us in via --list-tools / --describe,
                        // so anything else here is a typo or wrong invocation
                        // (e.g. `--list-tools --jsno` or
                        // `--describe koshi_search extra`). Fail loud
                        // instead of silently producing surprising output.
                        stderr.WriteLine($"error: unrecognized argument '{a}'");
                        return 2;
                    }
                    break;
            }
        }

        if (listTools && describe is not null)
        {
            stderr.WriteLine("error: --list-tools and --describe are mutually exclusive");
            return 2;
        }
        if (!listTools && describe is null)
        {
            // ShouldHandle guards against this, but defend in depth.
            stderr.WriteLine("error: pass --list-tools or --describe <tool>");
            return 2;
        }

        return describe is not null
            ? RunDescribe(describe, json, stdout, stderr)
            : RunList(json, stdout);
    }

    /// <summary>
    /// True iff the args contain at least one introspection flag.
    /// Used by <c>Program.cs</c> to early-dispatch before the MCP host starts.
    /// </summary>
    public static bool ShouldHandle(string[] args)
    {
        foreach (var a in args)
        {
            if (a == "--list-tools" || a == "--describe"
                || a.StartsWith("--describe=", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static int RunList(bool json, TextWriter stdout)
    {
        var tools = ToolCatalog.All;

        if (json)
        {
            var items = new List<ToolListEntry>(tools.Count);
            foreach (var t in tools)
                items.Add(new ToolListEntry(t.Name, FirstLine(t.Description), t.Parameters.Count));
            stdout.WriteLine(JsonSerializer.Serialize(items, IntrospectJsonContext.Default.ListToolListEntry));
            return 0;
        }

        stdout.WriteLine($"Koshi MCP tools ({tools.Count}):");
        stdout.WriteLine();
        foreach (var t in tools)
            stdout.WriteLine($"  {t.Name,-32}  {FirstLine(t.Description)}");
        stdout.WriteLine();
        stdout.WriteLine("Run 'koshi-mcp --describe <tool>' for parameters and full description.");
        stdout.WriteLine("Add --json to either flag for machine-readable output.");
        return 0;
    }

    private static int RunDescribe(string toolName, bool json, TextWriter stdout, TextWriter stderr)
    {
        var tool = ToolCatalog.Find(toolName);
        if (tool is null)
        {
            stderr.WriteLine(
                $"error: unknown tool '{toolName}'. " +
                $"Run 'koshi-mcp --list-tools' to see all {ToolCatalog.All.Count} available tools.");
            return 1;
        }

        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(tool, IntrospectJsonContext.Default.ToolInfo));
            return 0;
        }

        stdout.WriteLine(tool.Name);
        stdout.WriteLine($"  Class: {tool.ClassName}");
        stdout.WriteLine();
        foreach (var line in tool.Description.Split('\n'))
            stdout.WriteLine($"  {line.TrimEnd('\r')}");
        stdout.WriteLine();
        stdout.WriteLine($"  Parameters ({tool.Parameters.Count}):");
        if (tool.Parameters.Count == 0)
        {
            stdout.WriteLine("    (none)");
            return 0;
        }
        foreach (var p in tool.Parameters)
        {
            var defaultDisplay = p.DefaultValue is null
                ? "null"
                : (p.Type.StartsWith("string", StringComparison.Ordinal) && p.DefaultValue != "null")
                    ? $"\"{p.DefaultValue}\""
                    : p.DefaultValue;
            var req = p.Required ? "required" : $"default={defaultDisplay}";
            stdout.WriteLine($"    {p.Name} : {p.Type} ({req})");
            if (!string.IsNullOrWhiteSpace(p.Description))
                stdout.WriteLine($"      {p.Description}");
        }
        return 0;
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var nl = s.IndexOf('\n');
        return (nl < 0 ? s : s[..nl]).TrimEnd('\r').Trim();
    }
}

/// <summary>
/// Compact list entry returned by <c>--list-tools --json</c>. The
/// description is truncated to the first line so the listing stays
/// scannable; full text is available via <c>--describe X --json</c>.
/// Property names are intentionally <c>snake_case</c> to match the
/// JSON output of every koshi tool itself.
/// </summary>
internal sealed record ToolListEntry(string Name, string Description, int ParameterCount);
