namespace Koshi.Core.Team;

using Koshi.Core.Context;
using Koshi.Core.Harness;

// ─── Team Profile ───────────────────────────────────────────────────────

/// <summary>
/// A team's profile and configuration for the Koshi system.
/// Each team gets isolated memory, customized budgets, and quality tracking.
/// </summary>
public sealed record TeamProfile
{
    public required string TeamId { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<TeamMember> Members { get; init; } = [];
    public TeamConfig Config { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A member of a team with a role.
/// </summary>
public sealed record TeamMember(string UserId, string DisplayName, TeamRole Role = TeamRole.Member);

public enum TeamRole { Member, Lead, Admin }

// ─── Team Configuration ─────────────────────────────────────────────────

/// <summary>
/// Per-team configuration that overrides system defaults.
/// Allows each team to tune their Koshi instance.
/// </summary>
public sealed record TeamConfig
{
    /// <summary>Token budget for context window.</summary>
    public int ContextBudgetTokens { get; init; } = 8192;

    /// <summary>Preferred positioning strategy.</summary>
    public PositioningStrategy PositioningStrategy { get; init; } = PositioningStrategy.CacheOptimized;

    /// <summary>Maximum retrieval results per query.</summary>
    public int RetrievalTopK { get; init; } = 5;

    /// <summary>Whether to enable memory for this team.</summary>
    public bool EnableMemory { get; init; } = true;

    /// <summary>Whether to enable automatic fact extraction.</summary>
    public bool EnableFactExtraction { get; init; } = true;

    /// <summary>Custom system prompt for this team.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Team conventions to include as cacheable context.</summary>
    public string? TeamContext { get; init; }

    /// <summary>Preferred chat model (e.g., "phi4-mini", "gpt-4o").</summary>
    public string? PreferredModel { get; init; }

    /// <summary>Quality target — minimum acceptable composite score (0-1).</summary>
    public float QualityTarget { get; init; } = 0.7f;

    /// <summary>Convert to pipeline config for the orchestrator.</summary>
    public HarnessPipelineConfig ToPipelineConfig() => new()
    {
        SystemPrompt = SystemPrompt ?? "You are a helpful assistant.",
        TeamContext = TeamContext,
        EnableRetrieval = true,
        EnableMemory = EnableMemory,
        EnableFactExtraction = EnableFactExtraction,
        ContextBudget = ContextBudget.Default(ContextBudgetTokens),
        PositioningStrategy = PositioningStrategy,
        RetrievalOptions = new Models.RetrievalOptions(TopK: RetrievalTopK),
    };
}

// ─── Quality Feedback ───────────────────────────────────────────────────

/// <summary>
/// User feedback on a turn's quality. The signal that drives improvement.
/// </summary>
public sealed record QualityFeedback
{
    public required string SessionId { get; init; }
    public required int TurnIndex { get; init; }
    public required string TeamId { get; init; }
    public required string UserId { get; init; }

    /// <summary>User rating: 1 (poor) to 5 (excellent).</summary>
    public required int Rating { get; init; }

    /// <summary>What was wrong (optional structured feedback).</summary>
    public FeedbackIssue Issues { get; init; } = FeedbackIssue.None;

    /// <summary>Free-text comment.</summary>
    public string? Comment { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Structured feedback categories — what went wrong.
/// </summary>
[Flags]
public enum FeedbackIssue
{
    None = 0,
    Irrelevant = 1,         // Retrieved context wasn't relevant
    Incomplete = 2,         // Missing important information
    Hallucinated = 4,       // Model made things up
    TooVerbose = 8,         // Response was too long
    TooTerse = 16,          // Response was too short
    WrongFormat = 32,       // Output format didn't match expectations
    Outdated = 64,          // Information was out of date
    SlowResponse = 128,     // Latency was too high
}

// ─── Quality Score ──────────────────────────────────────────────────────

/// <summary>
/// Composite quality score for a turn, combining system metrics and user feedback.
/// </summary>
public sealed record QualityScore
{
    /// <summary>Composite score (0-1). Higher is better.</summary>
    public float Composite { get; init; }

    /// <summary>Per-dimension scores.</summary>
    public float RetrievalScore { get; init; }
    public float EfficiencyScore { get; init; }
    public float CacheScore { get; init; }
    public float LatencyScore { get; init; }
    public float UserScore { get; init; }

    /// <summary>Whether this turn met the team's quality target.</summary>
    public bool MeetsTarget(float target) => Composite >= target;

    /// <summary>Human-readable grade.</summary>
    public string Grade => Composite switch
    {
        >= 0.9f => "A",
        >= 0.8f => "B",
        >= 0.7f => "C",
        >= 0.6f => "D",
        _ => "F",
    };
}

// ─── Team Dashboard Data ────────────────────────────────────────────────

/// <summary>
/// Aggregated dashboard data for a team.
/// </summary>
public sealed record TeamDashboard
{
    public required string TeamId { get; init; }
    public required string TeamName { get; init; }
    public int TotalTurns { get; init; }
    public int TotalSessions { get; init; }
    public long TotalTokensConsumed { get; init; }
    public float AvgQualityScore { get; init; }
    public float AvgCacheHitRate { get; init; }
    public float AvgBudgetUtilization { get; init; }
    public double AvgLatencyMs { get; init; }
    public int FallbackCount { get; init; }
    public int TotalFactsExtracted { get; init; }
    public int FeedbackCount { get; init; }
    public float AvgUserRating { get; init; }
    public float QualityTarget { get; init; }
    public float TargetHitRate { get; init; }
    public IReadOnlyList<string> Recommendations { get; init; } = [];
    public IReadOnlyDictionary<string, float> QualityTrend { get; init; } = new Dictionary<string, float>();
}
