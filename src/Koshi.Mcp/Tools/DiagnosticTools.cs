using System.ComponentModel;
using System.Reflection;
using Koshi.Mcp.Internal;
using ModelContextProtocol.Server;

namespace Koshi.Mcp.Tools;

/// <summary>
/// MCP tools for diagnostics — version, health, and runtime status.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticTools
{
    private static readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private static readonly Lazy<string> _version = new(ReadVersion);

    [McpServerTool(Name = "koshi_version"), Description(
        "Return the Koshi MCP server version, .NET runtime version, and protocol version.")]
    public static string Version()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Koshi MCP Server v{_version.Value}");
        sb.AppendLine($"  .NET runtime: {Environment.Version}");
        sb.AppendLine($"  OS: {Environment.OSVersion}");
        sb.AppendLine($"  Process: PID {Environment.ProcessId}");
        sb.AppendLine($"  Started: {_startedAt:u}");
        return sb.ToString();
    }

    [McpServerTool(Name = "koshi_health"), Description(
        "Report the runtime health and configuration of the Koshi MCP server: " +
        "indexed corpus size, memory store status, persistence configuration, and uptime.")]
    public static string Health()
    {
        var indexStatus = RetrievalTools.GetStatus();
        var memStatus = MemoryTools.GetStatus();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ Koshi Health (v{_version.Value}) ═══\n");

        sb.AppendLine("  Retrieval:");
        sb.AppendLine($"    Indexed:     {(indexStatus.indexed ? "yes" : "no")}");
        sb.AppendLine($"    Chunks:      {indexStatus.chunkCount}");
        sb.AppendLine($"    Sources:     {indexStatus.sourceCount}");
        sb.AppendLine($"    Path:        {indexStatus.path ?? "(none)"}");
        sb.AppendLine($"    Persistence: {(indexStatus.persistenceEnabled ? "enabled" : "disabled")}");
        sb.AppendLine($"    File:        {indexStatus.persistencePath ?? "(in-memory only)"}");
        if (indexStatus.loadedFromSnapshot)
            sb.AppendLine($"    Loaded:      from snapshot");
        var namedCorpora = RetrievalTools.GetNamedCorporaStatus();
        if (namedCorpora.Count > 0)
        {
            sb.AppendLine($"    Named corpora: {namedCorpora.Count}");
            foreach (var nc in namedCorpora)
                sb.AppendLine($"      • {nc.name}: {nc.chunks} chunks from {nc.sources} sources ({nc.path ?? "in-memory"})");
        }
        sb.AppendLine();

        sb.AppendLine("  Memory:");
        sb.AppendLine($"    Records:     {memStatus.Count}");
        sb.AppendLine($"    Backend:     {memStatus.BackendKind}");
        sb.AppendLine($"    Persistence: {(memStatus.PersistenceEnabled ? "enabled" : "disabled")}");
        sb.AppendLine($"    Location:    {memStatus.Path ?? "(in-memory only)"}");
        if (memStatus.BackendKind == "vault")
        {
            sb.AppendLine($"    Unmanaged:   {memStatus.UnmanagedNoteCount}");
            sb.AppendLine($"    Dup-id warn: {memStatus.DuplicateIdWarningCount}");
            sb.AppendLine($"    Watcher:     {memStatus.VaultWatcherStatus ?? "(unknown)"}");
        }
        sb.AppendLine();

        sb.AppendLine("  Configuration (resolved paths):");
        var paths = PathConfig.Default;
        sb.AppendLine($"    Project root:       {paths.ProjectRoot}  [{PathConfig.SourceLabel(paths.ProjectRootFromEnv)}]");
        sb.AppendLine($"    KOSHI_INDEX_PATH:   {paths.IndexPath}  [{PathConfig.SourceLabel(paths.IndexPathFromEnv)}]");
        sb.AppendLine($"      auto-index:       {(paths.IndexPathFromEnv ? "enabled (env)" : "disabled (set KOSHI_INDEX_PATH to enable)")}");
        sb.AppendLine($"    KOSHI_INDEX_FILE:   {paths.IndexFile}  [{PathConfig.SourceLabel(paths.IndexFileFromEnv)}]");
        sb.AppendLine($"    KOSHI_MEMORY_FILE:  {paths.MemoryFile}  [{PathConfig.SourceLabel(paths.MemoryFileFromEnv)}]");
        sb.AppendLine($"    KOSHI_MEMORY_VAULT: {paths.MemoryVault ?? "(unset)"}  [{(paths.MemoryVaultFromEnv ? "env" : "default")}]");
        sb.AppendLine($"    KOSHI_TOKENIZER_MODEL: {Koshi.Core.Tokenization.TokenCounters.ModelName}");
        var embedProvider = Koshi.Core.Retrieval.EmbeddingProviderRegistry.Current;
        if (embedProvider is null)
            sb.AppendLine("    Embedding provider:    not configured (BM25-only)");
        else
            sb.AppendLine($"    Embedding provider:    {embedProvider.ModelName} (dim={embedProvider.Dimensions})");
        sb.AppendLine();

        var uptime = DateTimeOffset.UtcNow - _startedAt;
        sb.AppendLine($"  Uptime: {uptime:hh\\:mm\\:ss}");
        sb.AppendLine($"  GC working set: {Environment.WorkingSet / (1024 * 1024)} MB");

        return sb.ToString();
    }

    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            // Strip git metadata (e.g. "0.2.0+abc123" -> "0.2.0")
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }
}
