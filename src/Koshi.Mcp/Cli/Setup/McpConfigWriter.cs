using System.Text.Json;
using System.Text.Json.Nodes;

namespace Koshi.Mcp.Cli.Setup;

/// <summary>Outcome of a single MCP-config registration attempt for one client.</summary>
internal enum McpRegisterOutcome
{
    /// <summary>Config file did not exist; created it with the koshi entry.</summary>
    Created,

    /// <summary>Config file existed without a koshi entry; added it.</summary>
    Added,

    /// <summary>Config file already had a koshi entry; left it strictly alone (merge-safe).</summary>
    AlreadyPresent,

    /// <summary>--dry-run: described the would-be change without writing.</summary>
    DryRun,

    /// <summary>An IO or parse error occurred; the file was not modified.</summary>
    Error,
}

internal sealed record McpRegisterResult(
    McpRegisterOutcome Outcome,
    string ConfigPath,
    string? ErrorMessage = null,
    string? BackupPath = null);

/// <summary>
/// Idempotent JSON-surgery for the per-client MCP config file.
///
/// Adds the canonical <c>mcpServers.koshi</c> entry when missing. If a
/// koshi entry already exists in any shape, it is left strictly alone —
/// the user may have customized <c>env</c>, <c>tools</c>, a custom binary
/// path, etc., and silently rewriting those is the exact "config editor
/// footgun" we want to avoid. Other top-level keys in the file are never
/// touched. The previous file is snapshotted to <c>&lt;file&gt;.bak</c>
/// before any write.
///
/// This helper is intentionally self-contained so the upcoming
/// <c>koshi-mcp config get/set/apply</c> subcommand (#78 Gap B) can build
/// on the same primitive.
/// </summary>
internal static class McpConfigWriter
{
    private const string ServerKey = "koshi";

    public static McpRegisterResult RegisterKoshi(
        PersonaClient client,
        bool dryRun,
        string? homeDir = null,
        string? appDataDir = null)
    {
        var configPath = ClientResolver.McpConfigFile(client, homeDir, appDataDir);

        try
        {
            bool fileExists = File.Exists(configPath);

            if (!fileExists)
            {
                if (dryRun)
                {
                    return new McpRegisterResult(McpRegisterOutcome.DryRun, configPath,
                        ErrorMessage: "would create new config with koshi entry");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                var freshRoot = new JsonObject
                {
                    ["mcpServers"] = new JsonObject
                    {
                        [ServerKey] = BuildKoshiEntry(client),
                    },
                };
                WriteAtomic(configPath, freshRoot);
                return new McpRegisterResult(McpRegisterOutcome.Created, configPath);
            }

            // File exists — parse and patch.
            var rawText = File.ReadAllText(configPath);
            JsonNode? parsed;
            try
            {
                parsed = string.IsNullOrWhiteSpace(rawText)
                    ? new JsonObject()
                    : JsonNode.Parse(rawText, documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
            }
            catch (JsonException ex)
            {
                return new McpRegisterResult(McpRegisterOutcome.Error, configPath,
                    ErrorMessage: $"existing config is not valid JSON: {ex.Message}");
            }

            if (parsed is not JsonObject existingRoot)
            {
                return new McpRegisterResult(McpRegisterOutcome.Error, configPath,
                    ErrorMessage: "existing config root is not a JSON object");
            }

            // Locate or create the mcpServers map (idempotent).
            if (existingRoot["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                existingRoot["mcpServers"] = servers;
            }

            var existingEntry = servers[ServerKey];

            if (existingEntry is not null)
            {
                if (existingEntry is JsonObject)
                {
                    // Merge-safe: any koshi entry is treated as "already present"
                    // even if its shape differs from BuildKoshiEntry(). Users
                    // routinely customise env, tools, args, or point command at
                    // a non-PATH binary, and silently rewriting that is the
                    // exact footgun we want to avoid. `koshi-agents doctor`
                    // can flag missing canonical fields without us mutating.
                    return new McpRegisterResult(McpRegisterOutcome.AlreadyPresent, configPath);
                }

                return new McpRegisterResult(McpRegisterOutcome.Error, configPath,
                    ErrorMessage:
                        $"mcpServers.koshi exists but is not a JSON object " +
                        $"({existingEntry.GetValueKind()}); fix it by hand");
            }

            if (dryRun)
            {
                return new McpRegisterResult(McpRegisterOutcome.DryRun, configPath,
                    ErrorMessage: "would add koshi entry to existing mcpServers map");
            }

            string? backupPath = SnapshotBackup(configPath);
            servers[ServerKey] = BuildKoshiEntry(client);
            WriteAtomic(configPath, existingRoot);
            return new McpRegisterResult(McpRegisterOutcome.Added, configPath, BackupPath: backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new McpRegisterResult(McpRegisterOutcome.Error, configPath,
                ErrorMessage: ex.Message);
        }
    }

    private static JsonObject BuildKoshiEntry(PersonaClient client)
    {
        // Copilot CLI's MCP config uses "type": "local" for stdio (subprocess)
        // servers — verified against the working entry that `copilot mcp add`
        // produces. Claude Desktop's mcpServers schema does not use a `type`
        // field; we omit it there.
        var entry = new JsonObject
        {
            ["command"] = "koshi-mcp",
            ["args"] = new JsonArray(),
        };
        if (client == PersonaClient.Copilot)
        {
            entry["type"] = "local";
        }
        return entry;
    }

    private static void WriteAtomic(string path, JsonNode contents)
    {
        var json = contents.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        // Append a trailing newline to keep editors and `git diff` happy.
        var payload = json + Environment.NewLine;

        var tmp = path + ".koshi-tmp";
        File.WriteAllText(tmp, payload);
        File.Move(tmp, path, overwrite: true);
    }

    private static string SnapshotBackup(string path)
    {
        var backupPath = path + ".bak";
        // Always overwrite the latest backup; users only need the previous state.
        File.Copy(path, backupPath, overwrite: true);
        return backupPath;
    }
}
