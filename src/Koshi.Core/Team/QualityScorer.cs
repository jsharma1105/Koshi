namespace Koshi.Core.Team;

using Koshi.Core.Harness;

/// <summary>
/// Scores the quality of each turn using a weighted composite of
/// system metrics and user feedback. The scoring function that
/// tells you whether your AI system is getting better or worse.
/// 
/// Dimensions:
///   1. Retrieval — did we find relevant content?
///   2. Efficiency — how well did we use the token budget?
///   3. Cache — are we saving money via prompt caching?
///   4. Latency — is the response fast enough?
///   5. User — did the user rate it positively?
/// </summary>
public sealed class QualityScorer
{
    private readonly QualityScoringWeights _weights;

    public QualityScorer(QualityScoringWeights? weights = null)
    {
        _weights = weights ?? QualityScoringWeights.Default;
    }

    /// <summary>
    /// Score a turn from its metrics and optional user feedback.
    /// </summary>
    public QualityScore Score(TurnMetrics metrics, QualityFeedback? feedback = null)
    {
        float retrieval = ScoreRetrieval(metrics);
        float efficiency = ScoreEfficiency(metrics);
        float cache = ScoreCache(metrics);
        float latency = ScoreLatency(metrics);
        float user = feedback is not null ? ScoreUserFeedback(feedback) : 0.7f; // neutral default

        float composite = _weights.Retrieval * retrieval
            + _weights.Efficiency * efficiency
            + _weights.Cache * cache
            + _weights.Latency * latency
            + _weights.User * user;

        return new QualityScore
        {
            Composite = Math.Clamp(composite, 0f, 1f),
            RetrievalScore = retrieval,
            EfficiencyScore = efficiency,
            CacheScore = cache,
            LatencyScore = latency,
            UserScore = user,
        };
    }

    /// <summary>
    /// Score retrieval quality: did we find relevant chunks?
    /// More chunks = better (up to a point), no fallback = good.
    /// </summary>
    private static float ScoreRetrieval(TurnMetrics m)
    {
        // Base: how many chunks did we retrieve?
        float chunkScore = m.RetrievedChunkCount switch
        {
            0 => 0.2f,
            1 => 0.5f,
            2 => 0.7f,
            >= 3 => 0.9f,
            _ => 0.5f,
        };

        // Penalty for using fallback
        float fallbackPenalty = m.RetrievedChunkCount == 0 ? 0.3f : 0f;

        // Bonus for including memories
        float memoryBonus = m.RecalledMemoryCount > 0 ? 0.1f : 0f;

        return Math.Clamp(chunkScore - fallbackPenalty + memoryBonus, 0f, 1f);
    }

    /// <summary>
    /// Score budget efficiency: using tokens well, not wasting.
    /// Sweet spot is 40-80% utilization.
    /// </summary>
    private static float ScoreEfficiency(TurnMetrics m)
    {
        float util = m.BudgetUtilization;
        return util switch
        {
            < 0.1f => 0.3f,     // Under-utilizing — probably missing context
            < 0.4f => 0.6f,     // Could include more
            < 0.8f => 1.0f,     // Sweet spot
            < 0.95f => 0.8f,    // Getting tight
            _ => 0.5f,          // Over-packed — likely dropping content
        };
    }

    /// <summary>
    /// Score cache performance: are we reusing cached prefixes?
    /// </summary>
    private static float ScoreCache(TurnMetrics m) =>
        m.CacheRatio switch
        {
            >= 0.3f => 1.0f,    // Good cache prefix
            >= 0.1f => 0.7f,    // Some caching
            > 0f => 0.5f,       // Minimal caching
            _ => 0.3f,          // No caching at all
        };

    /// <summary>
    /// Score latency: faster is better, with a generous threshold.
    /// </summary>
    private static float ScoreLatency(TurnMetrics m)
    {
        double ms = m.TotalLatency.TotalMilliseconds;
        return ms switch
        {
            < 1000 => 1.0f,     // Sub-second — excellent
            < 3000 => 0.9f,     // 1-3s — good
            < 5000 => 0.7f,     // 3-5s — acceptable
            < 10000 => 0.5f,    // 5-10s — slow
            < 30000 => 0.3f,    // 10-30s — painful
            _ => 0.1f,          // 30s+ — unacceptable
        };
    }

    /// <summary>
    /// Convert user feedback rating (1-5) to a 0-1 score.
    /// </summary>
    private static float ScoreUserFeedback(QualityFeedback feedback)
    {
        float base_ = (feedback.Rating - 1) / 4f; // 1→0, 5→1

        // Additional penalty for specific issue flags
        int issueCount = CountFlags(feedback.Issues);
        float issuePenalty = issueCount * 0.05f;

        return Math.Clamp(base_ - issuePenalty, 0f, 1f);
    }

    private static int CountFlags(FeedbackIssue issues)
    {
        int count = 0;
        for (int i = 0; i < 8; i++)
            if (((int)issues & (1 << i)) != 0) count++;
        return count;
    }
}

/// <summary>
/// Weights for the quality scoring dimensions. Must sum to 1.0.
/// </summary>
public sealed record QualityScoringWeights(
    float Retrieval,
    float Efficiency,
    float Cache,
    float Latency,
    float User)
{
    public static readonly QualityScoringWeights Default = new(
        Retrieval: 0.30f,
        Efficiency: 0.15f,
        Cache: 0.10f,
        Latency: 0.15f,
        User: 0.30f);
}
