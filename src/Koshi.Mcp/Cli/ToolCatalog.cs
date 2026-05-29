using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Koshi.Mcp.Tools;
using ModelContextProtocol.Server;

namespace Koshi.Mcp.Cli;

/// <summary>
/// Offline catalog of every MCP tool registered by <c>koshi-mcp</c> (#67).
///
/// Hardcoded type list rather than reflection-over-the-assembly because:
///   1. The five tool classes are statically referenced by
///      <c>WithTools&lt;T&gt;()</c> in <c>Program.cs</c>, so the trimmer
///      already preserves them.
///   2. Each <see cref="Extract{T}"/> invocation pins <c>T</c>'s public
///      static methods via <see cref="DynamicallyAccessedMembersAttribute"/>,
///      so <c>.GetMethods()</c> is trim-safe and the build stays IL-warning
///      clean under <c>&lt;IsAotCompatible&gt;true&lt;/IsAotCompatible&gt;</c>.
///
/// Reading attributes (<see cref="McpServerToolAttribute"/>,
/// <see cref="DescriptionAttribute"/>) does NOT trigger the tool type's
/// static constructor, so <c>koshi-mcp --list-tools</c> stays side-effect
/// free (no <c>PathConfig.Default</c> resolution, no memory backend init).
/// </summary>
internal static class ToolCatalog
{
    private static readonly Lazy<IReadOnlyList<ToolInfo>> _all = new(BuildAll);

    public static IReadOnlyList<ToolInfo> All => _all.Value;

    public static ToolInfo? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var t in All)
        {
            if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return null;
    }

    private static IReadOnlyList<ToolInfo> BuildAll()
    {
        var list = new List<ToolInfo>();
        list.AddRange(Extract<RetrievalTools>());
        list.AddRange(Extract<MemoryTools>());
        list.AddRange(Extract<ContextTools>());
        list.AddRange(Extract<TeamTools>());
        list.AddRange(Extract<DiagnosticTools>());
        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private static IEnumerable<ToolInfo> Extract<[DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicMethods)] T>()
        where T : class
    {
        var type = typeof(T);
        var typeName = type.Name;
        foreach (var method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
            if (toolAttr is null) continue;

            var name = !string.IsNullOrWhiteSpace(toolAttr.Name) ? toolAttr.Name! : method.Name;
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

            var parameters = new List<ToolParameter>();
            foreach (var p in method.GetParameters())
            {
                var pDesc = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                parameters.Add(new ToolParameter(
                    Name: p.Name ?? "(unnamed)",
                    Type: FriendlyTypeName(p.ParameterType),
                    Required: !p.HasDefaultValue,
                    // Emit JSON null both for "no default" (required) and
                    // "default IS null" (optional). Consumers disambiguate
                    // via the `required` flag rather than the value, which
                    // keeps the JSON shape unambiguous (no overloading of
                    // the literal string "null"). A literal string default
                    // of "null" still round-trips as the JSON string "null".
                    DefaultValue: p.HasDefaultValue && p.DefaultValue is not null
                        ? FormatDefault(p.DefaultValue)
                        : null,
                    Description: pDesc));
            }

            yield return new ToolInfo(
                Name: name,
                ClassName: typeName,
                Description: description,
                Parameters: parameters);
        }
    }

    /// <summary>
    /// Map common .NET parameter types onto schema-friendly aliases so CLI
    /// output reads naturally (<c>string</c> / <c>integer</c> / <c>number</c>
    /// / <c>boolean</c>). Unknown types fall back to <see cref="Type.Name"/>.
    /// </summary>
    internal static string FriendlyTypeName(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying is not null) return FriendlyTypeName(underlying) + "?";
        if (t == typeof(string)) return "string";
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)) return "integer";
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "number";
        if (t == typeof(bool)) return "boolean";
        return t.Name;
    }

    internal static string FormatDefault(object? value)
    {
        // Returns a "natural" string form used both by the JSON output
        // (so consumers get "All" instead of "\"All\"") and the human CLI
        // (which re-quotes string defaults at render time using ToolParameter.Type).
        // Trade-off: a literal default of "null" (string) is indistinguishable
        // from null at the JSON layer — accept that fringe case.
        if (value is null) return "null";
        if (value is string s) return s;
        if (value is bool b) return b ? "true" : "false";
        if (value is float f) return f.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        if (value is double d) return d.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        return value.ToString() ?? "null";
    }
}

/// <summary>
/// One MCP tool's static metadata. Records use PascalCase property names;
/// the JSON serializer applies <see cref="System.Text.Json.JsonNamingPolicy.SnakeCaseLower"/>
/// so CLI JSON output uses <c>snake_case</c> keys (matching koshi tool naming).
/// </summary>
internal sealed record ToolInfo(
    string Name,
    string ClassName,
    string Description,
    IReadOnlyList<ToolParameter> Parameters);

internal sealed record ToolParameter(
    string Name,
    string Type,
    bool Required,
    string? DefaultValue,
    string Description);
