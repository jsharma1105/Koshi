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
        "WHEN TO CALL: When the user asks what version of Koshi is running, or before reporting a bug.\n" +
        "WHAT IT DOES: Returns the Koshi MCP server version, .NET runtime, OS, process id, and start time.")]
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
        "WHEN TO CALL: When something is not working as expected — search returns nothing, memories " +
        "do not survive restart, the team dashboard shows zeros, or the user reports persistence issues. " +
        "Always preferable to guessing whether persistence is wired correctly.\n" +
        "WHAT IT DOES: Reports indexed corpus size, memory store backend + persistence path, team " +
        "registry state, snapshot-load status, and uptime. Read-only; never mutates state.")]
    public static string Health()
    {
        var indexStatus = RetrievalTools.GetStatus();
        var memStatus = MemoryTools.GetStatus();
        var teamsStatus = TeamTools.GetStatus();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ Koshi Health (v{_version.Value}) ═══\n");

        sb.AppendLine("  Retrieval:");
        sb.AppendLine($"    Indexed:     {(indexStatus.indexed ? "yes" : "no")}");
        sb.AppendLine($"    Chunks:      {indexStatus.chunkCount}");
        sb.AppendLine($"    Sources:     {indexStatus.sourceCount}");
        sb.AppendLine($"    Path:        {indexStatus.path ?? "(none)"}");
        AppendPersistenceBlock(
            sb,
            persistenceEnabled: indexStatus.persistenceEnabled,
            persistencePath: indexStatus.persistencePath,
            loadAttempted: indexStatus.persistenceEnabled,
            loadSucceeded: indexStatus.loadedFromSnapshot,
            loadDiscardReason: indexStatus.snapshotDiscardReason,
            loadedCount: indexStatus.loadedChunkCount,
            loadedAt: indexStatus.loadedAt,
            loadedNoun: "chunks",
            diskNoun: "Snapshot on disk");
        if (indexStatus.snapshotLoadWarning is not null)
            sb.AppendLine($"    Warning:     {indexStatus.snapshotLoadWarning}");
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
        AppendPersistenceBlock(
            sb,
            persistenceEnabled: memStatus.PersistenceEnabled,
            persistencePath: memStatus.Path,
            loadAttempted: memStatus.LoadAttempted,
            // JSON-backend load "succeeds" even when the file does not yet exist
            // (zero records, persistence on); surface that as load-attempted=yes,
            // loadedAt=set, loadedCount=0 so the user sees "loaded 0 records".
            loadSucceeded: memStatus.LoadAttempted && memStatus.LoadedAt is not null,
            loadDiscardReason: null,
            loadedCount: memStatus.LoadedRecordCount,
            loadedAt: memStatus.LoadedAt,
            loadedNoun: "records",
            diskNoun: "File on disk");
        if (memStatus.BackendKind == "vault")
        {
            sb.AppendLine($"    Unmanaged:   {memStatus.UnmanagedNoteCount}");
            sb.AppendLine($"    Dup-id warn: {memStatus.DuplicateIdWarningCount}");
            sb.AppendLine($"    Watcher:     {memStatus.VaultWatcherStatus ?? "(unknown)"}");
            sb.AppendLine($"    Flavor:      {memStatus.VaultFlavor ?? "(unknown)"}");
        }
        sb.AppendLine();

        sb.AppendLine("  Teams:");
        sb.AppendLine($"    Registered:  {teamsStatus.TeamCount}");
        sb.AppendLine($"    Scores:      {teamsStatus.ScoreCount}");
        sb.AppendLine($"    Feedback:    {teamsStatus.FeedbackCount}");
        sb.AppendLine($"    Backend:     json");
        AppendPersistenceBlock(
            sb,
            persistenceEnabled: teamsStatus.PersistenceEnabled,
            persistencePath: teamsStatus.Path,
            loadAttempted: teamsStatus.LoadAttempted,
            loadSucceeded: teamsStatus.LoadAttempted && teamsStatus.LoadedAt is not null && teamsStatus.LastLoadError is null,
            loadDiscardReason: teamsStatus.LastLoadError,
            loadedCount: teamsStatus.LoadedTeamCount + teamsStatus.LoadedScoreCount + teamsStatus.LoadedFeedbackCount,
            loadedAt: teamsStatus.LoadedAt,
            loadedNoun: $"items ({teamsStatus.LoadedTeamCount} teams, {teamsStatus.LoadedScoreCount} scores, {teamsStatus.LoadedFeedbackCount} feedback)",
            diskNoun: "File on disk");
        sb.AppendLine($"    Last save:   {(teamsStatus.LastSaveError is null ? "ok" : "failed: " + teamsStatus.LastSaveError)}");
        sb.AppendLine();

        sb.AppendLine("  Configuration (resolved paths):");
        var paths = PathConfig.Default;
        sb.AppendLine($"    Project root:       {paths.ProjectRoot}  [{PathConfig.SourceLabel(paths.ProjectRootFromEnv)}]");
        sb.AppendLine($"    KOSHI_INDEX_PATH:   {paths.IndexPath}  [{PathConfig.SourceLabel(paths.IndexPathFromEnv)}]");
        sb.AppendLine($"      auto-index:       {(paths.IndexPathFromEnv ? "enabled (env)" : "disabled (set KOSHI_INDEX_PATH to enable)")}");
        sb.AppendLine($"    KOSHI_INDEX_FILE:   {paths.IndexFile}  [{PathConfig.SourceLabel(paths.IndexFileFromEnv)}]");
        sb.AppendLine($"    KOSHI_MEMORY_FILE:  {paths.MemoryFile}  [{PathConfig.SourceLabel(paths.MemoryFileFromEnv)}]");
        sb.AppendLine($"    KOSHI_MEMORY_VAULT: {paths.MemoryVault ?? "(unset)"}  [{(paths.MemoryVaultFromEnv ? "env" : "default")}]");
        sb.AppendLine($"    KOSHI_TEAMS_FILE:   {paths.TeamsFile}  [{PathConfig.SourceLabel(paths.TeamsFileFromEnv)}]");
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

    /// <summary>
    /// Emit the per-section <c>Persistence</c> sub-block introduced by #70:
    /// three lines covering save-on-shutdown, load-on-startup (with count +
    /// timestamp when applicable), and on-disk file size + last-modified.
    /// Replaces the old single-line <c>Persistence: enabled / File:</c> pair
    /// that was ambiguous about whether a snapshot had actually loaded.
    /// </summary>
    internal static void AppendPersistenceBlock(
        System.Text.StringBuilder sb,
        bool persistenceEnabled,
        string? persistencePath,
        bool loadAttempted,
        bool loadSucceeded,
        string? loadDiscardReason,
        int loadedCount,
        DateTimeOffset? loadedAt,
        string loadedNoun,
        string diskNoun)
    {
        sb.AppendLine("    Persistence:");
        sb.AppendLine($"      Save on shutdown:  {(persistenceEnabled ? $"yes (→ {persistencePath})" : "disabled (in-memory only)")}");

        string loadLine;
        if (!loadAttempted)
        {
            loadLine = "disabled (no persistence path configured)";
        }
        else if (loadSucceeded && loadedAt is not null)
        {
            loadLine = $"yes — loaded {loadedCount} {loadedNoun} on startup ({loadedAt.Value:u})";
        }
        else if (loadDiscardReason is not null)
        {
            loadLine = $"no — discarded: {loadDiscardReason}";
        }
        else
        {
            loadLine = "no — no snapshot found on disk";
        }
        sb.AppendLine($"      Load on startup:   {loadLine}");
        sb.AppendLine($"      {diskNoun}: {DescribeFileOnDisk(persistencePath)}");
    }

    /// <summary>
    /// Describe a backing file as "<size>, last modified <UTC>" for the
    /// koshi_health Persistence block. Returns a placeholder when the file
    /// does not yet exist or persistence is disabled (#70).
    /// </summary>
    internal static string DescribeFileOnDisk(string? path)
    {
        if (path is null) return "(in-memory only)";
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException or PathTooLongException or NotSupportedException
                or UnauthorizedAccessException)
        {
            return $"(unreadable: {ex.GetType().Name})";
        }

        if (!info.Exists)
        {
            // Vault backends use a directory rather than a file path; show
            // the directory entry distinctly so users do not interpret
            // "no file yet" as a missing vault root.
            if (Directory.Exists(path)) return "(vault directory, see Records above)";
            return "(no file yet)";
        }
        return $"{FormatBytes(info.Length)}, last modified {info.LastWriteTimeUtc:u}";
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
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
