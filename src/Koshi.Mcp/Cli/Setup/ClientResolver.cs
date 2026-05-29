namespace Koshi.Mcp.Cli.Setup;

/// <summary>
/// Resolves filesystem locations for client agent + MCP-config files across
/// Windows, macOS, and Linux. Pure path math; never touches disk.
/// </summary>
internal static class ClientResolver
{
    public static string HomeDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string AppDataDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// Directory where Koshi personas should be written for a given client + scope.
    /// <paramref name="homeDir"/> is the test-injection seam — production callers
    /// pass <c>null</c> and we resolve <see cref="HomeDir"/>.
    /// </summary>
    public static string AgentsDir(
        PersonaClient client,
        ScopeKind scope,
        string? repoRoot = null,
        string? homeDir = null)
    {
        repoRoot ??= Directory.GetCurrentDirectory();
        var home = homeDir ?? HomeDir;

        return (client, scope) switch
        {
            (PersonaClient.Claude, ScopeKind.User) =>
                Path.Join(home, ".claude", "agents"),
            (PersonaClient.Claude, ScopeKind.Repo) =>
                Path.Join(repoRoot, ".claude", "agents"),
            (PersonaClient.Copilot, ScopeKind.User) =>
                Path.Join(home, ".copilot", "agents"),
            (PersonaClient.Copilot, ScopeKind.Repo) =>
                Path.Join(repoRoot, ".github", "copilot", "agents"),
            _ => throw new ArgumentOutOfRangeException(nameof(client)),
        };
    }

    /// <summary>
    /// MCP config file the client reads on launch. (Doctor only — we never write here.)
    /// <paramref name="homeDir"/> / <paramref name="appDataDir"/> are
    /// test-injection seams; production callers pass <c>null</c>.
    /// </summary>
    public static string McpConfigFile(
        PersonaClient client,
        string? homeDir = null,
        string? appDataDir = null)
    {
        var home = homeDir ?? HomeDir;
        var appData = appDataDir ?? AppDataDir;

        return client switch
        {
            PersonaClient.Claude when OperatingSystem.IsWindows() =>
                Path.Join(appData, "Claude", "claude_desktop_config.json"),
            PersonaClient.Claude when OperatingSystem.IsMacOS() =>
                Path.Join(home, "Library", "Application Support", "Claude",
                    "claude_desktop_config.json"),
            PersonaClient.Claude =>
                Path.Join(home, ".config", "Claude", "claude_desktop_config.json"),

            PersonaClient.Copilot =>
                Path.Join(home, ".copilot", "mcp-config.json"),

            _ => throw new ArgumentOutOfRangeException(nameof(client)),
        };
    }

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
