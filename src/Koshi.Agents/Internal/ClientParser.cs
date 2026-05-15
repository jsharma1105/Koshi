namespace Koshi.Agents.Internal;

internal static class ClientParser
{
    /// <summary>
    /// Parses --client argument. Accepts "claude", "copilot", or "both" (case-insensitive).
    /// </summary>
    public static IReadOnlyList<PersonaClient> Parse(string clientArg)
    {
        return clientArg?.ToLowerInvariant() switch
        {
            "claude" => new[] { PersonaClient.Claude },
            "copilot" => new[] { PersonaClient.Copilot },
            "both" or "all" => new[] { PersonaClient.Claude, PersonaClient.Copilot },
            _ => throw new ArgumentException(
                $"--client must be one of: claude, copilot, both. Got: {clientArg ?? "<null>"}"),
        };
    }

    public static ScopeKind ParseScope(string scopeArg)
    {
        return scopeArg?.ToLowerInvariant() switch
        {
            null or "" or "user" => ScopeKind.User,
            "repo" or "local" => ScopeKind.Repo,
            _ => throw new ArgumentException(
                $"--scope must be one of: user, repo. Got: {scopeArg}"),
        };
    }
}
