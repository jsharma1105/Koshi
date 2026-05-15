namespace Koshi.Core.Harness;

using Koshi.Core.Context;

/// <summary>
/// Fallback strategy for graceful degradation.
/// Each level trades context quality for reliability.
/// 
/// Cascade:
///   Full pipeline → Reduced budget → Memory only → No context → Failed
/// 
/// Key principle: fallbacks are EXPLICIT about what was lost.
/// Fact extraction is disabled during degraded modes to prevent
/// polluting memory with uncertain information.
/// </summary>
public sealed class FallbackStrategy
{
    private readonly IHarnessTracer _tracer;

    public FallbackStrategy(IHarnessTracer? tracer = null)
    {
        _tracer = tracer ?? NullTracer.Instance;
    }

    /// <summary>
    /// Determine the next fallback level after a failure.
    /// Returns the degraded level and the reason.
    /// </summary>
    public FallbackDecision NextFallback(
        FallbackLevel currentLevel,
        FailureType failure,
        SessionTurnContext ctx)
    {
        var nextLevel = (currentLevel, failure) switch
        {
            // Retrieval failures → try with memory only
            (FallbackLevel.None, FailureType.RetrievalFailed) =>
                FallbackLevel.MemoryOnly,

            (FallbackLevel.None, FailureType.RetrievalTimeout) =>
                FallbackLevel.MemoryOnly,

            // Budget exceeded → reduce context
            (FallbackLevel.None, FailureType.BudgetExceeded) =>
                FallbackLevel.ReducedContext,

            // Reduced still failing → memory only
            (FallbackLevel.ReducedContext, _) =>
                FallbackLevel.MemoryOnly,

            // Memory failed too → no context
            (FallbackLevel.MemoryOnly, _) =>
                FallbackLevel.NoContext,

            // LLM timeout at any level → try no context (shorter prompt)
            (_, FailureType.LlmTimeout) when currentLevel < FallbackLevel.NoContext =>
                FallbackLevel.NoContext,

            // Everything failed
            _ => FallbackLevel.Failed,
        };

        string reason = $"{failure} at level {currentLevel}";

        _tracer.AddEvent("fallback", new Dictionary<string, object?>
        {
            ["from_level"] = currentLevel.ToString(),
            ["to_level"] = nextLevel.ToString(),
            ["failure"] = failure.ToString(),
            ["reason"] = reason,
        });

        return new FallbackDecision(nextLevel, reason, ShouldRetry: nextLevel != FallbackLevel.Failed);
    }

    /// <summary>
    /// Adjust the pipeline config for a given fallback level.
    /// Returns a modified config appropriate for the degraded mode.
    /// </summary>
    public HarnessPipelineConfig AdjustConfig(
        HarnessPipelineConfig original, FallbackLevel level) => level switch
    {
        FallbackLevel.None => original,

        FallbackLevel.ReducedContext => original with
        {
            ContextBudget = ContextBudget.Default(
                (original.ContextBudget?.TotalBudget ?? 8192) / 2),
            EnableFactExtraction = !original.SkipFactExtractionOnFallback
                && original.EnableFactExtraction,
        },

        FallbackLevel.MemoryOnly => original with
        {
            EnableRetrieval = false,
            EnableFactExtraction = !original.SkipFactExtractionOnFallback
                && original.EnableFactExtraction,
        },

        FallbackLevel.NoContext => original with
        {
            EnableRetrieval = false,
            EnableMemory = false,
            EnableFactExtraction = false,
        },

        _ => original with
        {
            EnableRetrieval = false,
            EnableMemory = false,
            EnableFactExtraction = false,
        },
    };

    /// <summary>
    /// Build a disclaimer to prepend when answering in degraded mode.
    /// </summary>
    public static string? GetDegradedDisclaimer(FallbackLevel level) => level switch
    {
        FallbackLevel.ReducedContext =>
            "[Note: Answering with reduced context due to budget constraints. Some relevant information may be missing.]",
        FallbackLevel.MemoryOnly =>
            "[Note: Document retrieval was unavailable. Answering from memory and general knowledge only.]",
        FallbackLevel.NoContext =>
            "[Note: Answering without project-specific context. Response may be less accurate.]",
        _ => null,
    };
}

/// <summary>Types of failures that can trigger fallback.</summary>
public enum FailureType
{
    RetrievalFailed,
    RetrievalTimeout,
    MemoryFailed,
    BudgetExceeded,
    LlmTimeout,
    LlmError,
    FactExtractionFailed,
    Unknown,
}

/// <summary>Decision from the fallback strategy.</summary>
public sealed record FallbackDecision(
    FallbackLevel Level,
    string Reason,
    bool ShouldRetry);
