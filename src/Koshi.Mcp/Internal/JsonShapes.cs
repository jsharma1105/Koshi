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

    // ─────────────────────────── koshi_version ───────────────────────────

    public sealed record VersionResultData(
        string Version,
        string DotnetRuntime,
        string Os,
        int ProcessId,
        DateTimeOffset StartedAt);

    // ───────────────────────── koshi_token_count ─────────────────────────

    public sealed record TokenCountResultData(
        int Tokens,
        int Characters,
        double CharsPerToken);

    // ───────────────────────── koshi_budget_plan ─────────────────────────

    public sealed record BudgetPlanResultData(
        int TotalBudget,
        int SystemTokens,
        int TeamTokens,
        int FixedCost,
        int Remaining,
        BudgetSplitData Split,
        BudgetAllocationData Allocation,
        int CacheSavingsEstimate,
        string? SplitNote);

    public sealed record BudgetSplitData(
        int RetrievalPct,
        int MemoryPct,
        int HistoryPct);

    public sealed record BudgetAllocationData(
        int RetrievalTokens,
        int MemoryTokens,
        int HistoryTokens,
        int RetrievalChunksEstimate,
        int MemoryItemsEstimate,
        int HistoryTurnsEstimate);

    // ───────────────────────── koshi_memory_stats ────────────────────────

    public sealed record MemoryStatsResultData(
        int TotalRecords,
        string Backend,
        List<MemoryStatsByTypeEntry> ByType,
        List<MemoryStatsByScopeEntry> ByScope,
        DateTimeOffset? OldestCreatedAt,
        DateTimeOffset? NewestCreatedAt,
        double AvgConfidence,
        bool PersistenceEnabled,
        string? PersistencePath);

    public sealed record MemoryStatsByTypeEntry(
        string Type,
        int Count);

    public sealed record MemoryStatsByScopeEntry(
        string Scope,
        int Count);

    // ───────────────────────── koshi_list_indexed ────────────────────────

    public sealed record ListIndexedResultData(
        string Mode,
        string? Corpus,
        int TotalChunks,
        int TotalSources,
        List<IndexedCorpusEntry> Corpora,
        List<IndexedSourceEntry> Sources);

    public sealed record IndexedCorpusEntry(
        string Name,
        int Chunks,
        int Sources,
        string? Path);

    public sealed record IndexedSourceEntry(
        string Source,
        int Chunks,
        string? Type);

    // ───────────────────────── koshi_list_teams ──────────────────────────

    public sealed record ListTeamsResultData(
        int TotalTeams,
        List<RegisteredTeamEntry> Teams);

    public sealed record RegisteredTeamEntry(
        string Id,
        string Name,
        int ContextBudgetTokens,
        int RetrievalTopK,
        double QualityTarget,
        DateTimeOffset CreatedAt,
        int TurnsScored,
        double AvgQuality);

    // ─────────────────────── koshi_team_dashboard ────────────────────────

    public sealed record TeamDashboardResultData(
        string TeamId,
        string TeamName,
        bool Registered,
        int TotalTurns,
        int TotalSessions,
        long TotalTokensConsumed,
        double AvgQualityScore,
        double AvgCacheHitRate,
        double AvgBudgetUtilization,
        double AvgLatencyMs,
        int FallbackCount,
        int FeedbackCount,
        double AvgUserRating,
        double QualityTarget,
        double TargetHitRate,
        List<TeamTrendPointEntry> QualityTrend,
        List<string> Recommendations);

    public sealed record TeamTrendPointEntry(
        string Period,
        double Score);

    // ───────────────────── koshi_analyze_feedback ────────────────────────

    public sealed record AnalyzeFeedbackResultData(
        string TeamId,
        int TurnCount,
        double CurrentAvgScore,
        double Trend,
        string TrendDirection,
        string WeakestDimension,
        List<ConfigAdjustmentEntry> SuggestedAdjustments);

    public sealed record ConfigAdjustmentEntry(
        string ConfigKey,
        string CurrentValue,
        string SuggestedValue,
        string Reason);

    // ═══════════════════════════ Phase 2b ════════════════════════════════
    // Mutation / persistence tools. All DTOs expose the canonical inputs +
    // post-condition state so callers can verify the mutation landed without
    // re-querying. <c>persistence_warning</c> fields are nullable strings —
    // populated only when the write succeeded but persistence is degraded
    // (e.g. in-process-only store, partial vault flush).

    // ─────────────────────────── koshi_remember ──────────────────────────

    public sealed record RememberResultData(
        string Id,
        string Type,
        string Subject,
        ScopeData Scope,
        string Source,
        double Confidence,
        DateTimeOffset CreatedAt,
        bool Embedded,
        string? EmbeddingModel,
        int EmbeddingDimensions,
        string? EmbedNote,
        bool PersistenceEnabled,
        string? PersistenceWarning);

    // ─────────────────────────── koshi_forget ────────────────────────────

    public sealed record ForgetResultData(
        string Subject,
        int Removed,
        List<string> DeletedIds);

    // ─────────────────────── koshi_clear_memories ────────────────────────

    public sealed record ClearMemoriesResultData(
        bool Confirmed,
        int Removed);

    // ─────────────────────── koshi_capture_turn ──────────────────────────

    public sealed record CaptureTurnResultData(
        bool AutoPromoted,
        int CandidatesExtracted,
        List<CaptureSavedEntry> Saved,
        List<CaptureSkippedEntry> Skipped,
        List<CaptureCandidateEntry> Candidates,
        ScopeData Scope,
        string? Reason,
        string? PersistenceWarning);

    public sealed record CaptureSavedEntry(
        string Id,
        string Subject,
        double Confidence,
        string MatchedPattern);

    public sealed record CaptureSkippedEntry(
        string Subject,
        string Reason);

    public sealed record CaptureCandidateEntry(
        string Subject,
        string Body,
        double Confidence,
        string MatchedPattern);

    // ──────────────────── koshi_memory_export_to_vault ───────────────────

    public sealed record ExportToVaultResultData(
        string VaultPath,
        string Flavor,
        int Exported);

    // ─────────────────── koshi_memory_import_from_vault ──────────────────

    public sealed record ImportFromVaultResultData(
        string VaultPath,
        string Mode,
        int Prior,
        int Incoming,
        int Added,
        int Replaced,
        int Kept);

    // ─────────────────────── koshi_memory_sync_vault ─────────────────────

    public sealed record SyncVaultResultData(
        string Backend,
        bool Synced,
        int Count,
        string? Reason);

    // ─────────────────────────── koshi_index ─────────────────────────────

    public sealed record IndexResultData(
        string Corpus,
        bool IsNamedCorpus,
        int Documents,
        int Chunks,
        int Tokens,
        int ChunkerMaxTokens,
        int ChunkerOverlapTokens,
        string? ChunkerWarning);

    // ─────────────────────── koshi_index_directory ───────────────────────

    public sealed record IndexDirectoryResultData(
        string Corpus,
        bool IsNamedCorpus,
        string Path,
        string? Pattern,
        int MaxFileSizeKb,
        int MaxFiles,
        int FilesIndexed,
        int FilesSkipped,
        int Chunks,
        int Tokens,
        int ChunkerMaxTokens,
        int ChunkerOverlapTokens,
        string? ChunkerWarning,
        string? SnapshotPath,
        bool SnapshotSaved);

    // ───────────────────────── koshi_clear_index ─────────────────────────

    public sealed record ClearIndexResultData(
        string CorpusResolved,
        string Mode,
        int ChunksRemoved,
        bool SnapshotDeleted,
        string? SnapshotPath,
        List<string> NamedCorporaCleared);

    // ──────────────────────── koshi_register_team ────────────────────────

    public sealed record RegisterTeamResultData(
        string TeamId,
        string Name,
        string Description,
        string Operation,
        int ContextBudgetTokens,
        int RetrievalTopK,
        double QualityTarget,
        bool HasSystemPrompt,
        bool HasTeamContext,
        string? PersistenceWarning);
}
