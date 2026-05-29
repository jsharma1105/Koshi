using System.Text.Json;

namespace Koshi.Agents.Internal;

/// <summary>
/// Parsed view of one client's <c>mcpServers.koshi</c> entry. Used by
/// <see cref="Commands.DoctorCommand"/> to drive a per-client live ping using
/// the exact command/args/env the client would invoke.
/// </summary>
internal sealed record ClientKoshiEntry(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string?> Env);

/// <summary>
/// Pulls the <c>mcpServers.koshi</c> entry out of a client config file the
/// same way Copilot CLI / Claude Desktop will. Lenient: trailing commas and
/// JSON comments are accepted because both clients allow them.
/// </summary>
internal static class ClientKoshiEntryReader
{
    /// <summary>
    /// Read <paramref name="configPath"/> and extract the <c>mcpServers.koshi</c>
    /// entry. Returns null when the file does not exist, isn't valid JSON, or
    /// doesn't contain a koshi entry.
    /// </summary>
    public static ClientKoshiEntry? TryRead(string configPath, out string? error)
    {
        error = null;
        if (!File.Exists(configPath)) return null;

        JsonDocument doc;
        try
        {
            using var stream = File.OpenRead(configPath);
            doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            error = $"config is not valid JSON: {ex.Message}";
            return null;
        }
        catch (IOException ex)
        {
            error = $"could not read config: {ex.Message}";
            return null;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) ||
                servers.ValueKind != JsonValueKind.Object)
                return null;
            if (!servers.TryGetProperty("koshi", out var entry) || entry.ValueKind != JsonValueKind.Object)
                return null;

            var command = entry.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String
                ? cmd.GetString() ?? string.Empty
                : string.Empty;

            var args = new List<string>();
            if (entry.TryGetProperty("args", out var argsElem) && argsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in argsElem.EnumerateArray())
                {
                    if (a.ValueKind == JsonValueKind.String)
                    {
                        var s = a.GetString();
                        if (s is not null) args.Add(s);
                    }
                }
            }

            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (entry.TryGetProperty("env", out var envElem) && envElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in envElem.EnumerateObject())
                {
                    env[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText(),
                    };
                }
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                error = "mcpServers.koshi.command is missing or empty";
                return null;
            }

            return new ClientKoshiEntry(command, args, env);
        }
    }
}
