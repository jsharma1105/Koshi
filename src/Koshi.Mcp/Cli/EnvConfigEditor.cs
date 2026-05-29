using System.Text.Json;
using System.Text.Json.Nodes;

namespace Koshi.Mcp.Cli;

/// <summary>
/// Outcome of an <see cref="EnvConfigEditor"/> operation for a single client.
/// </summary>
internal enum EnvEditOutcome
{
    /// <summary>Read succeeded (get / get-all).</summary>
    Read,

    /// <summary>An env var was added or its value changed.</summary>
    Changed,

    /// <summary>The requested set/unset was a no-op (already had that value / already absent).</summary>
    Unchanged,

    /// <summary>Config file does not exist on disk for this client.</summary>
    ConfigMissing,

    /// <summary>Config file exists but does not have a <c>mcpServers.koshi</c> entry.</summary>
    KoshiEntryMissing,

    /// <summary>Config file exists but is not valid JSON.</summary>
    InvalidJson,

    /// <summary>I/O error reading or writing the config file.</summary>
    IoError,

    /// <summary>Generic error (caller-supplied message).</summary>
    Error,
}

internal sealed record EnvEditResult(
    EnvEditOutcome Outcome,
    string ConfigPath,
    string? Value = null,
    IReadOnlyDictionary<string, string>? AllValues = null,
    string? ErrorMessage = null,
    string? BackupPath = null);

/// <summary>
/// JSON-surgery for the <c>mcpServers.koshi.env</c> map inside a per-client MCP config file.
/// <para>
/// Idempotent. Never creates the <c>mcpServers.koshi</c> entry itself — that's
/// the install command's job (<c>koshi-agents install --client X</c>). If
/// <c>mcpServers.koshi</c> is missing, returns
/// <see cref="EnvEditOutcome.KoshiEntryMissing"/> with a friendly error.
/// </para>
/// <para>
/// All writes:
/// </para>
/// <list type="bullet">
///   <item>Snapshot the existing file to <c>&lt;file&gt;.bak</c> first (overwrites the previous backup).</item>
///   <item>Write atomically via a per-process / per-call unique temp name then <c>File.Move</c>.</item>
///   <item>Normalize JSON output (comments / trailing commas in the source are dropped — documented behaviour).</item>
/// </list>
/// </summary>
internal static class EnvConfigEditor
{
    private const string ServerKey = "koshi";

    public static EnvEditResult Get(string client, string key, string? homeDir = null, string? appDataDir = null)
    {
        var path = McpClientPaths.Resolve(client, homeDir, appDataDir);
        var (root, koshi, parseErr) = LoadAndLocateKoshi(path);
        if (parseErr is not null) return parseErr with { ConfigPath = path };

        var env = koshi!["env"] as JsonObject;
        if (env is null || env[key] is not JsonNode node)
        {
            return new EnvEditResult(EnvEditOutcome.Read, path, Value: null);
        }
        return new EnvEditResult(EnvEditOutcome.Read, path, Value: node.GetValue<string>());
    }

    public static EnvEditResult GetAll(string client, string? homeDir = null, string? appDataDir = null)
    {
        var path = McpClientPaths.Resolve(client, homeDir, appDataDir);
        var (root, koshi, parseErr) = LoadAndLocateKoshi(path);
        if (parseErr is not null) return parseErr with { ConfigPath = path };

        var env = koshi!["env"] as JsonObject;
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        if (env is not null)
        {
            foreach (var kvp in env)
            {
                if (kvp.Value is JsonNode v)
                    all[kvp.Key] = v.ToString();
            }
        }
        return new EnvEditResult(EnvEditOutcome.Read, path, AllValues: all);
    }

    public static EnvEditResult Set(string client, string key, string value, string? homeDir = null, string? appDataDir = null)
    {
        var path = McpClientPaths.Resolve(client, homeDir, appDataDir);
        var (root, koshi, parseErr) = LoadAndLocateKoshi(path);
        if (parseErr is not null) return parseErr with { ConfigPath = path };

        var env = koshi!["env"] as JsonObject;
        if (env is null)
        {
            env = new JsonObject();
            koshi["env"] = env;
        }

        var existing = env[key]?.ToString();
        if (existing == value)
            return new EnvEditResult(EnvEditOutcome.Unchanged, path);

        env[key] = value;

        try
        {
            var backup = SnapshotBackup(path);
            WriteAtomic(path, root!);
            return new EnvEditResult(EnvEditOutcome.Changed, path, BackupPath: backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EnvEditResult(EnvEditOutcome.IoError, path, ErrorMessage: ex.Message);
        }
    }

    public static EnvEditResult Unset(string client, string key, string? homeDir = null, string? appDataDir = null)
    {
        var path = McpClientPaths.Resolve(client, homeDir, appDataDir);
        var (root, koshi, parseErr) = LoadAndLocateKoshi(path);
        if (parseErr is not null) return parseErr with { ConfigPath = path };

        var env = koshi!["env"] as JsonObject;
        if (env is null || !env.ContainsKey(key))
            return new EnvEditResult(EnvEditOutcome.Unchanged, path);

        env.Remove(key);

        try
        {
            var backup = SnapshotBackup(path);
            WriteAtomic(path, root!);
            return new EnvEditResult(EnvEditOutcome.Changed, path, BackupPath: backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EnvEditResult(EnvEditOutcome.IoError, path, ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Applies a batch of <c>KEY=VALUE</c> pairs. Returns a single
    /// <see cref="EnvEditResult"/> reflecting whether anything actually changed.
    /// Either all writes succeed (atomic file write) or none do.
    /// </summary>
    public static EnvEditResult Apply(string client, IDictionary<string, string> values, string? homeDir = null, string? appDataDir = null)
    {
        var path = McpClientPaths.Resolve(client, homeDir, appDataDir);
        var (root, koshi, parseErr) = LoadAndLocateKoshi(path);
        if (parseErr is not null) return parseErr with { ConfigPath = path };

        var env = koshi!["env"] as JsonObject;
        if (env is null)
        {
            env = new JsonObject();
            koshi["env"] = env;
        }

        bool changed = false;
        foreach (var (key, value) in values)
        {
            if (env[key]?.ToString() != value)
            {
                env[key] = value;
                changed = true;
            }
        }

        if (!changed)
            return new EnvEditResult(EnvEditOutcome.Unchanged, path);

        try
        {
            var backup = SnapshotBackup(path);
            WriteAtomic(path, root!);
            return new EnvEditResult(EnvEditOutcome.Changed, path, BackupPath: backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EnvEditResult(EnvEditOutcome.IoError, path, ErrorMessage: ex.Message);
        }
    }

    private static (JsonObject? Root, JsonObject? Koshi, EnvEditResult? Error) LoadAndLocateKoshi(string path)
    {
        if (!File.Exists(path))
        {
            return (null, null, new EnvEditResult(EnvEditOutcome.ConfigMissing, path,
                ErrorMessage: $"config file does not exist: {path}"));
        }

        string rawText;
        try { rawText = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null, new EnvEditResult(EnvEditOutcome.IoError, path, ErrorMessage: ex.Message));
        }

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
            return (null, null, new EnvEditResult(EnvEditOutcome.InvalidJson, path, ErrorMessage: ex.Message));
        }

        if (parsed is not JsonObject root)
        {
            return (null, null, new EnvEditResult(EnvEditOutcome.InvalidJson, path,
                ErrorMessage: "config root is not a JSON object"));
        }

        if (root["mcpServers"] is not JsonObject servers || servers[ServerKey] is not JsonObject koshi)
        {
            return (null, null, new EnvEditResult(EnvEditOutcome.KoshiEntryMissing, path,
                ErrorMessage: $"mcpServers.{ServerKey} is missing — run 'koshi-agents install --client X' first"));
        }

        return (root, koshi, null);
    }

    private static void WriteAtomic(string path, JsonNode contents)
    {
        var json = contents.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        var payload = json + Environment.NewLine;

        var tmp = $"{path}.koshi-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, payload);
        File.Move(tmp, path, overwrite: true);
    }

    private static string SnapshotBackup(string path)
    {
        var backupPath = path + ".bak";
        File.Copy(path, backupPath, overwrite: true);
        return backupPath;
    }
}
