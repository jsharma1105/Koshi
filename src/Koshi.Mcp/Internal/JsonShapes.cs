namespace Koshi.Mcp.Internal;

/// <summary>
/// Per-tool structured-output DTOs (#66 Phase 1).
///
/// All shapes are designed to expose CANONICAL values (raw numeric scores,
/// ISO-8601 timestamps via DateTimeOffset, full content where appropriate,
/// stable IDs, structured nested objects) rather than mirroring the lossy
/// text output. Property names use <c>snake_case</c> after the source-gen
/// naming policy renders them.
/// </summary>
internal static class JsonShapes
{
    // ─────────────────────────── koshi_search ────────────────────────────

    public sealed record SearchResultData(
        string Query,
        string Corpus,
        int Total,
        List<SearchHitData> Results);

    public sealed record SearchHitData(
        int Rank,
        double Score,
        string Source,
        string ChunkId,
        string Content);

    // ─────────────────────────── koshi_recall ────────────────────────────

    public sealed record RecallResultData(
        string Query,
        string TypeFilter,
        int Total,
        List<RecalledMemoryData> Memories);

    public sealed record RecalledMemoryData(
        string Id,
        string Type,
        string Subject,
        string Content,
        double Score,
        double Confidence,
        ScopeData Scope,
        string Source,
        DateTimeOffset CreatedAt);

    public sealed record ScopeData(
        string UserId,
        string WorkspaceId,
        string? ThreadId);

    // ───────────────────────── koshi_compile_context ─────────────────────

    public sealed record CompileContextResultData(
        CompileMetricsData Metrics,
        List<CompileSectionData> Sections);

    public sealed record CompileMetricsData(
        int TotalTokensUsed,
        int TokenBudgetAvailable,
        double BudgetUtilization,
        int SectionsIncluded,
        int SectionsDropped,
        string Strategy,
        int CacheableTokens,
        double CacheRatio);

    public sealed record CompileSectionData(
        string Role,
        string Id,
        int TokenCount,
        string Content);

    // ─────────────────────────── koshi_health ────────────────────────────

    public sealed record HealthResultData(
        string Version,
        TimeSpan Uptime,
        long WorkingSetMb,
        HealthRetrievalData Retrieval,
        HealthMemoryData Memory,
        HealthTeamsData Teams,
        HealthConfigurationData Configuration);

    public sealed record PersistenceData(
        bool Enabled,
        string? Path,
        bool LoadAttempted,
        bool LoadSucceeded,
        string? LoadDiscardReason,
        int LoadedCount,
        DateTimeOffset? LoadedAt,
        string? FileOnDisk);

    public sealed record HealthRetrievalData(
        bool Indexed,
        int ChunkCount,
        int SourceCount,
        string? Path,
        PersistenceData Persistence,
        string? SnapshotLoadWarning,
        List<NamedCorpusData> NamedCorpora);

    public sealed record NamedCorpusData(
        string Name,
        int Chunks,
        int Sources,
        string? Path);

    public sealed record HealthMemoryData(
        int RecordCount,
        string Backend,
        PersistenceData Persistence,
        int? UnmanagedNoteCount,
        int? DuplicateIdWarningCount,
        string? VaultWatcherStatus,
        string? VaultFlavor);

    public sealed record HealthTeamsData(
        int TeamCount,
        int ScoreCount,
        int FeedbackCount,
        string Backend,
        PersistenceData Persistence,
        string LastSave);

    public sealed record HealthConfigurationData(
        string ProjectRoot,
        bool ProjectRootFromEnv,
        string IndexPath,
        bool IndexPathFromEnv,
        bool AutoIndexEnabled,
        string IndexFile,
        bool IndexFileFromEnv,
        string MemoryFile,
        bool MemoryFileFromEnv,
        string? MemoryVault,
        bool MemoryVaultFromEnv,
        string TeamsFile,
        bool TeamsFileFromEnv,
        string TokenizerModel,
        string? EmbeddingProvider,
        int? EmbeddingDimensions);

    // ───────────────────────── koshi_score_turn ──────────────────────────

    public sealed record ScoreTurnResultData(
        string TeamId,
        bool TeamRegistered,
        double Composite,
        string Grade,
        ScoreBreakdownData Breakdown,
        ScoreTargetData Target,
        List<string> IssuesParsed,
        string? PersistenceWarning);

    public sealed record ScoreBreakdownData(
        double Retrieval,
        double Efficiency,
        double Cache,
        double Latency,
        double User);

    public sealed record ScoreTargetData(
        double? Value,
        bool? Met);
}
