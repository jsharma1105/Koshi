using Koshi.Core.Harness;
using Koshi.Core.Team;

namespace Koshi.Core.Tests;

/// <summary>
/// Regression coverage for issue #61: koshi_team_dashboard was reporting 0 for
/// Tokens Used / Avg Latency / Cache Hit Rate / Budget Util even though
/// koshi_score_turn was being called with real per-turn metrics.
///
/// Root cause: TeamRegistry stored only the derived QualityScore (the 0..1
/// dimension scores), never the raw TurnMetrics. BuildDashboard then read
/// those four aggregates from an optional QualityTracker that the MCP path
/// never passed.
///
/// Fix: capture TurnMetrics alongside the QualityScore in RecordScore /
/// FeedbackLoop.ProcessTurn, and aggregate them in BuildDashboard when no
/// QualityTracker is supplied.
/// </summary>
public class TeamDashboardMetricsTests
{
    private readonly TeamRegistry _registry = new();
    private readonly FeedbackLoop _loop;

    public TeamDashboardMetricsTests() => _loop = new FeedbackLoop(_registry);

    private static TeamProfile MakeTeam(string id, string name, int budget = 16384, float target = 0.75f) =>
        new()
        {
            TeamId = id,
            Name = name,
            Config = new TeamConfig { ContextBudgetTokens = budget, QualityTarget = target },
        };

    private static TurnMetrics MakeMetrics(int chunks, float budget, float cache, int latencyMs, int inputTokens = 0)
    {
        int tokens = inputTokens > 0 ? inputTokens : (int)Math.Round(budget * 16384);
        return new TurnMetrics
        {
            RetrievedChunkCount = chunks,
            BudgetUtilization = budget,
            CacheRatio = cache,
            InputTokens = tokens,
            CachedTokens = (int)Math.Round(cache * tokens),
            TotalLatency = TimeSpan.FromMilliseconds(latencyMs),
            RetrievalLatency = TimeSpan.FromMilliseconds(latencyMs * 0.1),
            LlmLatency = TimeSpan.FromMilliseconds(latencyMs * 0.8),
        };
    }

    [Fact]
    public void Dashboard_AggregatesRawMetrics_FromProcessTurn_Issue61Repro()
    {
        // Exact scenario from issue #61: four scored turns, expect non-zero
        // Tokens Used / Avg Latency / Cache Hit Rate / Budget Util.
        _registry.Register(MakeTeam("acme", "Acme"));

        _loop.ProcessTurn("acme", MakeMetrics(chunks: 5,  budget: 0.092f, cache: 0.044f, latencyMs: 500));
        _loop.ProcessTurn("acme", MakeMetrics(chunks: 8,  budget: 0.45f,  cache: 0.35f,  latencyMs: 850));
        _loop.ProcessTurn("acme", MakeMetrics(chunks: 3,  budget: 0.18f,  cache: 0.20f,  latencyMs: 420));
        _loop.ProcessTurn("acme", MakeMetrics(chunks: 12, budget: 0.78f,  cache: 0.55f,  latencyMs: 1200));

        var db = _registry.BuildDashboard("acme");

        Assert.Equal(4, db.TotalTurns);
        Assert.True(db.TotalTokensConsumed > 0, $"Tokens Used should be > 0, was {db.TotalTokensConsumed}");
        Assert.Equal(742.5, db.AvgLatencyMs, 1.0);
        Assert.InRange(db.AvgCacheHitRate, 0.28f, 0.30f);
        Assert.InRange(db.AvgBudgetUtilization, 0.37f, 0.38f);
    }

    [Fact]
    public void Dashboard_NoMetrics_ReportsZero_NotCrash()
    {
        // Pure score-only path (legacy / explicit RecordScore calls without
        // metrics) must continue to render zeros instead of throwing or
        // dividing by zero.
        _registry.Register(MakeTeam("solo", "Solo"));
        _registry.RecordScore("solo", new QualityScore { Composite = 0.8f });
        _registry.RecordScore("solo", new QualityScore { Composite = 0.6f });

        var db = _registry.BuildDashboard("solo");

        Assert.Equal(2, db.TotalTurns);
        Assert.Equal(0, db.TotalTokensConsumed);
        Assert.Equal(0, db.AvgLatencyMs);
        Assert.Equal(0f, db.AvgCacheHitRate);
        Assert.Equal(0f, db.AvgBudgetUtilization);
    }

    [Fact]
    public void Dashboard_RecordScore_WithExplicitMetrics_AggregatesThem()
    {
        // Direct RecordScore overload (the new 3-arg form) flows metrics in too.
        _registry.Register(MakeTeam("direct", "Direct"));
        _registry.RecordScore("direct",
            new QualityScore { Composite = 0.9f },
            MakeMetrics(chunks: 4, budget: 0.5f, cache: 0.3f, latencyMs: 600, inputTokens: 8000));

        var db = _registry.BuildDashboard("direct");

        Assert.Equal(1, db.TotalTurns);
        Assert.Equal(8000, db.TotalTokensConsumed);
        Assert.Equal(600, db.AvgLatencyMs);
        Assert.Equal(0.3f, db.AvgCacheHitRate, 0.001f);
        Assert.Equal(0.5f, db.AvgBudgetUtilization, 0.001f);
    }

    [Fact]
    public void Dashboard_RecordMetrics_AppendsIndependently()
    {
        _registry.Register(MakeTeam("indep", "Indep"));
        _registry.RecordScore("indep", new QualityScore { Composite = 0.8f });
        _registry.RecordMetrics("indep", MakeMetrics(chunks: 3, budget: 0.4f, cache: 0.2f, latencyMs: 800, inputTokens: 4000));

        var db = _registry.BuildDashboard("indep");

        Assert.Equal(1, db.TotalTurns);
        Assert.Equal(4000, db.TotalTokensConsumed);
        Assert.Equal(800, db.AvgLatencyMs);
    }

    [Fact]
    public void GetMetrics_UnknownTeam_ReturnsEmpty()
    {
        Assert.Empty(_registry.GetMetrics("nope"));
    }

    [Fact]
    public void RecordMetrics_UnknownTeam_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            _registry.RecordMetrics("nope", MakeMetrics(1, 0.1f, 0.1f, 100)));
    }

    [Fact]
    public void RecordScore_TwoArgOverload_BackCompat()
    {
        // The original 2-arg RecordScore(teamId, score) must still work and
        // simply skip metrics capture (legacy callers and existing tests rely
        // on it).
        _registry.Register(MakeTeam("legacy", "Legacy"));
        _registry.RecordScore("legacy", new QualityScore { Composite = 0.85f });

        Assert.Single(_registry.GetScores("legacy"));
        Assert.Empty(_registry.GetMetrics("legacy"));
    }
}
