namespace Koshi.Core.Team;

using Koshi.Core.Harness;

/// <summary>
/// Manages the quality feedback loop — the mechanism by which
/// user feedback improves future retrieval and context compilation.
/// 
/// The loop:
///   1. User rates a turn → QualityFeedback
///   2. FeedbackLoop analyzes patterns across feedback
///   3. Generates weight adjustments for retrieval/compilation
///   4. Over time, system learns what works for each team
/// 
/// This is the "learning" part of harness engineering.
/// </summary>
public sealed class FeedbackLoop
{
    private readonly TeamRegistry _registry;
    private readonly QualityScorer _scorer;

    public FeedbackLoop(TeamRegistry registry, QualityScorer? scorer = null)
    {
        _registry = registry;
        _scorer = scorer ?? new QualityScorer();
    }

    /// <summary>
    /// Process a completed turn: score it and record the score.
    /// </summary>
    public QualityScore ProcessTurn(string teamId, TurnMetrics metrics, QualityFeedback? feedback = null)
    {
        var score = _scorer.Score(metrics, feedback);
        _registry.RecordScore(teamId, score, metrics);

        if (feedback is not null)
            _registry.RecordFeedback(feedback);

        return score;
    }

    /// <summary>
    /// Analyze feedback patterns and suggest configuration adjustments.
    /// Returns concrete actions the team should take.
    /// </summary>
    public FeedbackAnalysis Analyze(string teamId)
    {
        var team = _registry.GetTeam(teamId);
        if (team is null) return FeedbackAnalysis.Empty;

        var scores = _registry.GetScores(teamId);
        var feedback = _registry.GetFeedback(teamId);

        if (scores.Count < 3) return FeedbackAnalysis.InsufficientData;

        var recentScores = scores.TakeLast(10).ToList();
        var olderScores = scores.SkipLast(10).TakeLast(10).ToList();

        // Trend analysis
        float recentAvg = recentScores.Average(s => s.Composite);
        float olderAvg = olderScores.Count > 0 ? olderScores.Average(s => s.Composite) : recentAvg;
        var trend = recentAvg - olderAvg;

        // Dimension analysis
        float avgRetrieval = recentScores.Average(s => s.RetrievalScore);
        float avgEfficiency = recentScores.Average(s => s.EfficiencyScore);
        float avgCache = recentScores.Average(s => s.CacheScore);
        float avgLatency = recentScores.Average(s => s.LatencyScore);

        // Identify weakest dimension
        var dimensions = new Dictionary<string, float>
        {
            ["retrieval"] = avgRetrieval,
            ["efficiency"] = avgEfficiency,
            ["cache"] = avgCache,
            ["latency"] = avgLatency,
        };
        var weakest = dimensions.MinBy(kv => kv.Value);

        // Generate adjustments
        var adjustments = new List<ConfigAdjustment>();

        if (avgRetrieval < 0.5f)
        {
            adjustments.Add(new ConfigAdjustment(
                "RetrievalTopK",
                team.Config.RetrievalTopK.ToString(),
                Math.Min(10, team.Config.RetrievalTopK + 2).ToString(),
                "Low retrieval quality — increase search results"));
        }

        if (avgEfficiency < 0.4f)
        {
            adjustments.Add(new ConfigAdjustment(
                "ContextBudgetTokens",
                team.Config.ContextBudgetTokens.ToString(),
                (team.Config.ContextBudgetTokens / 2).ToString(),
                "Low budget utilization — reduce budget to match actual usage"));
        }
        else if (avgEfficiency > 0.9f && recentScores.Any(s => s.RetrievalScore < 0.6f))
        {
            adjustments.Add(new ConfigAdjustment(
                "ContextBudgetTokens",
                team.Config.ContextBudgetTokens.ToString(),
                ((int)(team.Config.ContextBudgetTokens * 1.5)).ToString(),
                "Budget too tight — relevant content being dropped"));
        }

        if (avgCache < 0.3f && team.Config.TeamContext is null)
        {
            adjustments.Add(new ConfigAdjustment(
                "TeamContext",
                "(none)",
                "(add team conventions)",
                "No team context — add stable conventions for better prompt caching"));
        }

        // Issue pattern detection from feedback
        if (feedback.Count > 0)
        {
            int hallucinations = feedback.Count(f => f.Issues.HasFlag(FeedbackIssue.Hallucinated));
            if (hallucinations > feedback.Count * 0.15)
            {
                adjustments.Add(new ConfigAdjustment(
                    "SystemPrompt",
                    "(current)",
                    "(add grounding instructions)",
                    $"Hallucination rate {hallucinations}/{feedback.Count} — add 'Only answer from provided context' to system prompt"));
            }
        }

        return new FeedbackAnalysis
        {
            TeamId = teamId,
            TurnCount = scores.Count,
            CurrentAvgScore = recentAvg,
            Trend = trend,
            TrendDirection = trend > 0.02f ? TrendDirection.Improving
                : trend < -0.02f ? TrendDirection.Declining
                : TrendDirection.Stable,
            WeakestDimension = weakest.Key,
            SuggestedAdjustments = adjustments,
        };
    }
}

/// <summary>
/// Analysis results from the feedback loop.
/// </summary>
public sealed record FeedbackAnalysis
{
    public string TeamId { get; init; } = "";
    public int TurnCount { get; init; }
    public float CurrentAvgScore { get; init; }
    public float Trend { get; init; }
    public TrendDirection TrendDirection { get; init; }
    public string WeakestDimension { get; init; } = "";
    public IReadOnlyList<ConfigAdjustment> SuggestedAdjustments { get; init; } = [];

    public static readonly FeedbackAnalysis Empty = new();
    public static readonly FeedbackAnalysis InsufficientData = new() { TeamId = "(insufficient data)" };
}

/// <summary>
/// A suggested configuration change from the feedback loop.
/// </summary>
public sealed record ConfigAdjustment(
    string ConfigKey,
    string CurrentValue,
    string SuggestedValue,
    string Reason);

public enum TrendDirection { Improving, Stable, Declining }
