namespace Koshi.Agents.Internal;

/// <summary>
/// Resolves filesystem locations for client agent + MCP-config files across
/// Windows, macOS, and Linux. Pure path math; never touches disk.
/// </summary>
internal static class ClientResolver
{
    public static string HomeDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Directory where Koshi personas should be written for a given client + scope.
    /// </summary>
    public static string AgentsDir(PersonaClient client, ScopeKind scope, string? repoRoot = null)
    {
        repoRoot ??= Directory.GetCurrentDirectory();

        return (client, scope) switch
        {
            (PersonaClient.Claude, ScopeKind.User) =>
                Path.Join(HomeDir, ".claude", "agents"),
            (PersonaClient.Claude, ScopeKind.Repo) =>
                Path.Join(repoRoot, ".claude", "agents"),
            (PersonaClient.Copilot, ScopeKind.User) =>
                Path.Join(HomeDir, ".copilot", "agents"),
            (PersonaClient.Copilot, ScopeKind.Repo) =>
                Path.Join(repoRoot, ".github", "copilot", "agents"),
            _ => throw new ArgumentOutOfRangeException(nameof(client)),
        };
    }

    /// <summary>
    /// MCP config file the client reads on launch. (Doctor only — we never write here.)
    /// </summary>
    public static string McpConfigFile(PersonaClient client) => client switch
    {
        PersonaClient.Claude when OperatingSystem.IsWindows() =>
            Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Claude", "claude_desktop_config.json"),
        PersonaClient.Claude when OperatingSystem.IsMacOS() =>
            Path.Join(HomeDir, "Library", "Application Support", "Claude",
                "claude_desktop_config.json"),
        PersonaClient.Claude =>
            Path.Join(HomeDir, ".config", "Claude", "claude_desktop_config.json"),

        PersonaClient.Copilot =>
            Path.Join(HomeDir, ".copilot", "mcp-config.json"),

        _ => throw new ArgumentOutOfRangeException(nameof(client)),
    };

    /// <summary>
    /// The exact JSON snippet a user should add to their MCP config to register Koshi.
    /// Shape varies by client: Copilot CLI uses <c>"type": "local"</c> for stdio
    /// (subprocess) servers; Claude Desktop's mcpServers schema does not use a
    /// <c>type</c> field, so we omit it.
    /// </summary>
    public static string SuggestedMcpEntry(PersonaClient client) => client switch
    {
        PersonaClient.Copilot => """
            "koshi": {
              "command": "koshi-mcp",
              "args": [],
              "type": "local"
            }
            """,
        _ => """
            "koshi": {
              "command": "koshi-mcp",
              "args": []
            }
            """,
    };
}

internal enum ScopeKind
{
    User,
    Repo,
}
