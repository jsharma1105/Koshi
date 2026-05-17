namespace Koshi.Mcp.Internal;

/// <summary>
/// Argument shape for koshi_index: a single document to chunk and index.
/// Promoted to a top-level internal type so the AOT-safe
/// <see cref="KoshiJsonContext"/> source generator can target it.
/// </summary>
internal sealed record DocInput
{
    public string Content { get; init; } = "";
    public string? Source { get; init; }
    public string? Type { get; init; }
}
