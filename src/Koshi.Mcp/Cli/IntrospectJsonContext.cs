using System.Text.Json;
using System.Text.Json.Serialization;

namespace Koshi.Mcp.Cli;

/// <summary>
/// AOT-safe source-generated JSON serialization context for the
/// <c>koshi-mcp --list-tools</c> / <c>--describe --json</c> outputs (#67).
/// Kept separate from <c>KoshiJsonContext</c> so the introspection types
/// can use <see cref="JsonKnownNamingPolicy.SnakeCaseLower"/> without
/// disturbing the rest of the MCP server's JSON contracts (memory /
/// teams / index envelopes), which are PascalCase on disk by design.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ToolInfo))]
[JsonSerializable(typeof(List<ToolInfo>))]
[JsonSerializable(typeof(ToolParameter))]
[JsonSerializable(typeof(List<ToolParameter>))]
[JsonSerializable(typeof(ToolListEntry))]
[JsonSerializable(typeof(List<ToolListEntry>))]
internal sealed partial class IntrospectJsonContext : JsonSerializerContext;
