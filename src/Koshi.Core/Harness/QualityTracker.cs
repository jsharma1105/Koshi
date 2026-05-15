namespace Koshi.Core.Harness;

using System.Collections.Concurrent;

/// <summary>
/// Append-only metrics sink that tracks quality and performance across turns.
/// High-value metrics only (per rubber-duck advice): token usage, latency,
/// cache hits, retrieval counts, fallback usage.
/// </summary>
public sealed class QualityTracker
{
    private readonly ConcurrentBag<TurnMetricsEntry> _entries = [];

    public int TurnCount => _entries.Count;

    /// <summary>Record metrics from a completed turn.</summary>
    public void Record(SessionTurnContext ctx)
    {
        var compiled = ctx.CompiledContext;
        var metrics = compiled?.Metrics;

        _entries.Add(new TurnMetricsEntry
        {
            SessionId = ctx.Session.SessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Query = ctx.Query,
            InputTokens = metrics?.TotalTokensUsed ?? 0,
            OutputTokens = ctx.LlmResponse is not null
                ? EstimateOutputTokens(ctx.LlmResponse)
                : 0,
            CachedTokens = metrics?.CacheableTokens ?? 0,
            BudgetUtilization = metrics?.BudgetUtilization ?? 0,
            CacheRatio = metrics?.CacheRatio ?? 0,
            RetrievedChunkCount = ctx.RetrievedChunks.Count,
            RecalledMemoryCount = ctx.RecalledMemories.Count,
            ContextSectionsIncluded = metrics?.SectionsIncluded ?? 0,
            ContextSectionsDropped = metrics?.SectionsDropped ?? 0,
            RetrievalLatency = ctx.RetrievalLatency ?? TimeSpan.Zero,
            MemoryLatency = ctx.MemoryLatency ?? TimeSpan.Zero,
            CompilationLatency = ctx.CompilationLatency ?? TimeSpan.Zero,
            LlmLatency = ctx.LlmLatency ?? TimeSpan.Zero,
            TotalLatency = ctx.TotalLatency,
            FallbackLevel = ctx.FallbackLevel,
            FactsExtracted = ctx.FactExtraction?.Accepted ?? 0,
            PositioningStrategy = metrics?.StrategyUsed ?? Context.PositioningStrategy.CacheOptimized,
        });
    }

    // ── Aggregate Metrics ───────────────────────────────────────────────

    /// <summary>Get all recorded entries.</summary>
    public IReadOnlyList<TurnMetricsEntry> GetAllEntries() => [.. _entries];

    /// <summary>Average input tokens per turn.</summary>
    public float AvgInputTokens => _entries.Count > 0
        ? (float)_entries.Average(e => e.InputTokens) : 0;

    /// <summary>Average total latency.</summary>
    public TimeSpan AvgTotalLatency => _entries.Count > 0
        ? TimeSpan.FromMilliseconds(_entries.Average(e => e.TotalLatency.TotalMilliseconds)) : TimeSpan.Zero;

    /// <summary>Average budget utilization.</summary>
    public float AvgBudgetUtilization => _entries.Count > 0
        ? (float)_entries.Average(e => e.BudgetUtilization) : 0;

    /// <summary>Average cache ratio.</summary>
    public float AvgCacheRatio => _entries.Count > 0
        ? (float)_entries.Average(e => e.CacheRatio) : 0;

    /// <summary>Number of turns that used fallback.</summary>
    public int FallbackCount => _entries.Count(e => e.FallbackLevel != FallbackLevel.None);

    /// <summary>Total tokens consumed (input + output).</summary>
    public long TotalTokensConsumed => _entries.Sum(e => (long)e.InputTokens + e.OutputTokens);

    /// <summary>Get a summary report.</summary>
    public QualitySummary GetSummary() => new()
    {
        TurnCount = _entries.Count,
        TotalInputTokens = _entries.Sum(e => e.InputTokens),
        TotalOutputTokens = _entries.Sum(e => e.OutputTokens),
        TotalCachedTokens = _entries.Sum(e => e.CachedTokens),
        AvgInputTokens = AvgInputTokens,
        AvgBudgetUtilization = AvgBudgetUtilization,
        AvgCacheRatio = AvgCacheRatio,
        AvgTotalLatencyMs = AvgTotalLatency.TotalMilliseconds,
        FallbackCount = FallbackCount,
        TotalFactsExtracted = _entries.Sum(e => e.FactsExtracted),
    };

    private static int EstimateOutputTokens(string text) =>
        (int)(text.Length / 3.5); // rough BPE estimate
}

/// <summary>
/// A single recorded turn with all tracked metrics.
/// </summary>
public sealed record TurnMetricsEntry
{
    public required string SessionId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Query { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CachedTokens { get; init; }
    public float BudgetUtilization { get; init; }
    public float CacheRatio { get; init; }
    public int RetrievedChunkCount { get; init; }
    public int RecalledMemoryCount { get; init; }
    public int ContextSectionsIncluded { get; init; }
    public int ContextSectionsDropped { get; init; }
    public TimeSpan RetrievalLatency { get; init; }
    public TimeSpan MemoryLatency { get; init; }
    public TimeSpan CompilationLatency { get; init; }
    public TimeSpan LlmLatency { get; init; }
    public TimeSpan TotalLatency { get; init; }
    public FallbackLevel FallbackLevel { get; init; }
    public int FactsExtracted { get; init; }
    public Context.PositioningStrategy PositioningStrategy { get; init; }
}

/// <summary>
/// Aggregate quality summary across all tracked turns.
/// </summary>
public sealed record QualitySummary
{
    public int TurnCount { get; init; }
    public int TotalInputTokens { get; init; }
    public int TotalOutputTokens { get; init; }
    public int TotalCachedTokens { get; init; }
    public float AvgInputTokens { get; init; }
    public float AvgBudgetUtilization { get; init; }
    public float AvgCacheRatio { get; init; }
    public double AvgTotalLatencyMs { get; init; }
    public int FallbackCount { get; init; }
    public int TotalFactsExtracted { get; init; }
}
