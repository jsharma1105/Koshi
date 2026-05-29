namespace Koshi.Mcp.Cli;

/// <summary>
/// Resolves the on-disk config file each MCP-compatible client reads at launch.
/// Pure path math — never touches disk.
/// <para>
/// Mirrors <c>Koshi.Agents.Internal.ClientResolver.McpConfigFile</c>. The
/// duplication is intentional: <c>koshi-mcp</c> intentionally does NOT take a
/// dependency on the (soon-to-be-deprecated) <c>Koshi.Agents</c> assembly. A
/// parity test in <c>Koshi.Core.Tests</c> asserts the two implementations
/// resolve to the same paths until Wave 3 collapses them.
/// </para>
/// </summary>
internal static class McpClientPaths
{
    /// <summary>The clients this CLI knows how to edit configs for.</summary>
    public static readonly IReadOnlyList<string> KnownClients = ["claude", "copilot"];

    /// <summary>
    /// Returns the absolute path of the MCP config file for <paramref name="client"/>.
    /// </summary>
    /// <param name="client">Case-insensitive name: <c>claude</c> | <c>copilot</c>.</param>
    /// <param name="homeDir">
    /// Optional override for the user-profile directory. Production callers should
    /// pass <c>null</c> (resolves <see cref="Environment.SpecialFolder.UserProfile"/>);
    /// tests inject a temp directory.
    /// </param>
    /// <param name="appDataDir">
    /// Optional override for <see cref="Environment.SpecialFolder.ApplicationData"/>.
    /// Only used for Claude on Windows. Tests inject a temp directory.
    /// </param>
    public static string Resolve(string client, string? homeDir = null, string? appDataDir = null)
    {
        var home = homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = appDataDir ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return Normalize(client) switch
        {
            "claude" when OperatingSystem.IsWindows() =>
                Path.Join(appData, "Claude", "claude_desktop_config.json"),
            "claude" when OperatingSystem.IsMacOS() =>
                Path.Join(home, "Library", "Application Support", "Claude",
                    "claude_desktop_config.json"),
            "claude" =>
                Path.Join(home, ".config", "Claude", "claude_desktop_config.json"),

            "copilot" =>
                Path.Join(home, ".copilot", "mcp-config.json"),

            _ => throw new ArgumentException(
                $"Unknown MCP client '{client}'. Known clients: {string.Join(", ", KnownClients)}.",
                nameof(client)),
        };
    }

    /// <summary>
    /// Returns the canonical lowercase form of a client name, throwing if unknown.
    /// </summary>
    public static string Normalize(string client)
    {
        if (string.IsNullOrWhiteSpace(client))
            throw new ArgumentException("Client name must not be empty.", nameof(client));

        var trimmed = client.Trim().ToLowerInvariant();
        if (!KnownClients.Contains(trimmed))
        {
            throw new ArgumentException(
                $"Unknown MCP client '{client}'. Known clients: {string.Join(", ", KnownClients)}.",
                nameof(client));
        }
        return trimmed;
    }
}
