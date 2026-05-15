namespace Koshi.Core.Team;

using System.Collections.Concurrent;

/// <summary>
/// Registry for managing teams and their configurations.
/// Provides multi-tenant isolation — each team gets its own
/// memory scope, quality tracking, and pipeline config.
/// </summary>
public sealed class TeamRegistry
{
    private readonly ConcurrentDictionary<string, TeamProfile> _teams = new();
    private readonly ConcurrentDictionary<string, List<QualityFeedback>> _feedback = new();
    private readonly ConcurrentDictionary<string, List<QualityScore>> _scores = new();

    /// <summary>Register a new team.</summary>
    public void Register(TeamProfile team)
    {
        if (!_teams.TryAdd(team.TeamId, team))
            throw new InvalidOperationException($"Team '{team.TeamId}' already registered");
        _feedback[team.TeamId] = [];
        _scores[team.TeamId] = [];
    }

    /// <summary>Get a team by ID.</summary>
    public TeamProfile? GetTeam(string teamId) =>
        _teams.GetValueOrDefault(teamId);

    /// <summary>List all registered teams.</summary>
    public IReadOnlyList<TeamProfile> ListTeams() => [.. _teams.Values];

    /// <summary>Update a team's configuration.</summary>
    public void UpdateConfig(string teamId, TeamConfig newConfig)
    {
        if (!_teams.TryGetValue(teamId, out var team))
            throw new KeyNotFoundException($"Team '{teamId}' not found");
        _teams[teamId] = team with { Config = newConfig };
    }

    /// <summary>Record a quality score for a team.</summary>
    public void RecordScore(string teamId, QualityScore score)
    {
        if (!_scores.TryGetValue(teamId, out var scores))
            throw new KeyNotFoundException($"Team '{teamId}' not found");
        lock (scores) { scores.Add(score); }
    }

    /// <summary>Record user feedback for a team.</summary>
    public void RecordFeedback(QualityFeedback feedback)
    {
        if (!_feedback.TryGetValue(feedback.TeamId, out var feedbackList))
            throw new KeyNotFoundException($"Team '{feedback.TeamId}' not found");
        lock (feedbackList) { feedbackList.Add(feedback); }
    }

    /// <summary>Get all scores for a team.</summary>
    public IReadOnlyList<QualityScore> GetScores(string teamId)
    {
        if (!_scores.TryGetValue(teamId, out var scores)) return [];
        lock (scores) { return [.. scores]; }
    }

    /// <summary>Get all feedback for a team.</summary>
    public IReadOnlyList<QualityFeedback> GetFeedback(string teamId)
    {
        if (!_feedback.TryGetValue(teamId, out var fb)) return [];
        lock (fb) { return [.. fb]; }
    }

    /// <summary>
    /// Build a dashboard for a team from its tracked data.
    /// </summary>
    public TeamDashboard BuildDashboard(
        string teamId,
        Harness.QualityTracker? qualityTracker = null)
    {
        var team = GetTeam(teamId)
            ?? throw new KeyNotFoundException($"Team '{teamId}' not found");

        var scores = GetScores(teamId);
        var feedback = GetFeedback(teamId);

        // Pull quality tracker data if available
        var trackerEntries = qualityTracker?.GetAllEntries()
            .Where(e => e.SessionId.Contains(teamId))
            .ToList() ?? [];

        float avgScore = scores.Count > 0
            ? scores.Average(s => s.Composite) : 0;
        float avgRating = feedback.Count > 0
            ? (float)feedback.Average(f => f.Rating) : 0;
        int targetHits = scores.Count(s => s.MeetsTarget(team.Config.QualityTarget));
        float targetHitRate = scores.Count > 0
            ? (float)targetHits / scores.Count : 0;

        // Build quality trend (last N scores in groups of 5)
        var trend = new Dictionary<string, float>();
        if (scores.Count > 0)
        {
            var groups = scores.Chunk(Math.Max(1, scores.Count / 5));
            int batchIndex = 0;
            foreach (var group in groups)
            {
                trend[$"Batch {++batchIndex}"] = group.Average(s => s.Composite);
            }
        }

        // Generate recommendations
        var recommendations = GenerateRecommendations(team, scores, feedback);

        return new TeamDashboard
        {
            TeamId = teamId,
            TeamName = team.Name,
            TotalTurns = scores.Count,
            TotalSessions = feedback.Select(f => f.SessionId).Distinct().Count(),
            TotalTokensConsumed = qualityTracker?.TotalTokensConsumed ?? 0,
            AvgQualityScore = avgScore,
            AvgCacheHitRate = qualityTracker?.AvgCacheRatio ?? 0,
            AvgBudgetUtilization = qualityTracker?.AvgBudgetUtilization ?? 0,
            AvgLatencyMs = qualityTracker?.AvgTotalLatency.TotalMilliseconds ?? 0,
            FallbackCount = qualityTracker?.FallbackCount ?? 0,
            TotalFactsExtracted = 0,
            FeedbackCount = feedback.Count,
            AvgUserRating = avgRating,
            QualityTarget = team.Config.QualityTarget,
            TargetHitRate = targetHitRate,
            Recommendations = recommendations,
            QualityTrend = trend,
        };
    }

    /// <summary>
    /// Generate actionable recommendations from quality data.
    /// </summary>
    private static List<string> GenerateRecommendations(
        TeamProfile team,
        IReadOnlyList<QualityScore> scores,
        IReadOnlyList<QualityFeedback> feedback)
    {
        var recs = new List<string>();
        if (scores.Count == 0) return recs;

        float avgRetrieval = scores.Average(s => s.RetrievalScore);
        float avgEfficiency = scores.Average(s => s.EfficiencyScore);
        float avgCache = scores.Average(s => s.CacheScore);
        float avgLatency = scores.Average(s => s.LatencyScore);

        if (avgRetrieval < 0.5f)
            recs.Add("⚠ Low retrieval quality — consider increasing TopK or adding more documents to the corpus");

        if (avgEfficiency < 0.5f)
            recs.Add("⚠ Low budget utilization — your context window is mostly empty. Increase retrieval TopK or add team context");

        if (avgCache < 0.5f)
            recs.Add("💡 Low cache ratio — add a stable system prompt and team context to improve prompt caching");

        if (avgLatency < 0.5f)
            recs.Add("🐢 High latency — consider a smaller/faster model or reducing context size");

        // Check for hallucination feedback
        int hallucinations = feedback.Count(f => f.Issues.HasFlag(FeedbackIssue.Hallucinated));
        if (hallucinations > feedback.Count * 0.2)
            recs.Add("🔴 High hallucination rate — improve retrieval quality and add grounding instructions to system prompt");

        // Check for relevance issues
        int irrelevant = feedback.Count(f => f.Issues.HasFlag(FeedbackIssue.Irrelevant));
        if (irrelevant > feedback.Count * 0.3)
            recs.Add("🔍 Users reporting irrelevant results — review retrieval configuration and document coverage");

        if (recs.Count == 0)
            recs.Add("✅ All quality dimensions are healthy — keep monitoring!");

        return recs;
    }
}
