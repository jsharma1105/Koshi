using Koshi.Core.Context;
using Koshi.Core.Harness;
using Koshi.Core.Team;

namespace Koshi.Core.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Phase 5 — Team Layer Tests (xUnit)
// ═══════════════════════════════════════════════════════════════════════

#region QualityScorer Tests

public class QualityScorerTests
{
    private readonly QualityScorer _scorer = new();

    [Fact]
    public void Score_HighQualityTurn_ReturnsHighScore()
    {
        var metrics = H.MakeMetrics(chunks: 4, budget: 0.6f, cache: 0.35f, latencyMs: 800, memories: 2);
        var score = _scorer.Score(metrics);

        Assert.True(score.Composite > 0.8f);
        Assert.True(score.Grade is "A" or "B");
    }

    [Fact]
    public void Score_LowQualityTurn_ReturnsLowScore()
    {
        var metrics = H.MakeMetrics(chunks: 0, budget: 0.05f, cache: 0f, latencyMs: 35000);
        var score = _scorer.Score(metrics);

        Assert.True(score.Composite < 0.4f);
        Assert.True(score.Grade is "F" or "D");
    }

    [Fact]
    public void Score_WithPositiveFeedback_BoostsScore()
    {
        var metrics = H.MakeMetrics(chunks: 3, budget: 0.5f, cache: 0.15f, latencyMs: 2000);
        var withoutFeedback = _scorer.Score(metrics);
        var withFeedback = _scorer.Score(metrics, H.MakeFeedback(rating: 5));

        Assert.True(withFeedback.UserScore > withoutFeedback.UserScore);
    }

    [Fact]
    public void Score_WithNegativeFeedback_LowersUserScore()
    {
        var metrics = H.MakeMetrics(chunks: 3, budget: 0.5f, cache: 0.15f, latencyMs: 2000);
        var feedback = H.MakeFeedback(rating: 1, issues: FeedbackIssue.Hallucinated | FeedbackIssue.Irrelevant);
        var score = _scorer.Score(metrics, feedback);

        Assert.True(score.UserScore < 0.15f);
    }

    [Fact]
    public void Score_CompositeAlwaysClampedTo01()
    {
        var best = _scorer.Score(
            H.MakeMetrics(chunks: 5, budget: 0.6f, cache: 0.5f, latencyMs: 500),
            H.MakeFeedback(rating: 5));
        var worst = _scorer.Score(
            H.MakeMetrics(chunks: 0, budget: 0.02f, cache: 0f, latencyMs: 60000),
            H.MakeFeedback(rating: 1, issues: FeedbackIssue.Hallucinated | FeedbackIssue.Irrelevant));

        Assert.InRange(best.Composite, 0f, 1f);
        Assert.InRange(worst.Composite, 0f, 1f);
    }

    [Fact]
    public void Score_ReturnsDimensionBreakdown()
    {
        var score = _scorer.Score(H.MakeMetrics(chunks: 2, budget: 0.45f, cache: 0.2f, latencyMs: 4000));

        Assert.InRange(score.RetrievalScore, 0f, 1f);
        Assert.InRange(score.EfficiencyScore, 0f, 1f);
        Assert.InRange(score.CacheScore, 0f, 1f);
        Assert.InRange(score.LatencyScore, 0f, 1f);
        Assert.InRange(score.UserScore, 0f, 1f);
    }

    [Fact]
    public void Score_CustomWeights_AffectsComposite()
    {
        var heavy = new QualityScorer(new QualityScoringWeights(
            Retrieval: 0.90f, Efficiency: 0.025f, Cache: 0.025f,
            Latency: 0.025f, User: 0.025f));

        var metrics = H.MakeMetrics(chunks: 5, budget: 0.05f, cache: 0f, latencyMs: 30000);

        Assert.True(heavy.Score(metrics).Composite > _scorer.Score(metrics).Composite);
    }

    [Fact]
    public void Score_NoFeedback_UsesNeutralDefault()
    {
        var score = _scorer.Score(H.MakeMetrics(chunks: 3, budget: 0.5f, cache: 0.15f, latencyMs: 2000));
        Assert.Equal(0.7f, score.UserScore);
    }
}

#endregion

#region QualityScore Tests

public class QualityScoreTests
{
    [Theory]
    [InlineData(0.95f, "A")]
    [InlineData(0.85f, "B")]
    [InlineData(0.75f, "C")]
    [InlineData(0.65f, "D")]
    [InlineData(0.40f, "F")]
    public void Grade_ReturnsCorrectLetter(float composite, string expectedGrade)
    {
        var score = new QualityScore { Composite = composite };
        Assert.Equal(expectedGrade, score.Grade);
    }

    [Fact]
    public void MeetsTarget_AboveTarget_ReturnsTrue()
    {
        Assert.True(new QualityScore { Composite = 0.8f }.MeetsTarget(0.7f));
    }

    [Fact]
    public void MeetsTarget_BelowTarget_ReturnsFalse()
    {
        Assert.False(new QualityScore { Composite = 0.5f }.MeetsTarget(0.7f));
    }
}

#endregion

#region TeamRegistry Tests

public class TeamRegistryTests
{
    private readonly TeamRegistry _registry = new();

    [Fact]
    public void Register_AddsTeam()
    {
        _registry.Register(H.MakeTeam("team-1", "Team One"));
        var team = _registry.GetTeam("team-1");

        Assert.NotNull(team);
        Assert.Equal("Team One", team!.Name);
    }

    [Fact]
    public void Register_DuplicateId_Throws()
    {
        _registry.Register(H.MakeTeam("team-1", "Team One"));
        Assert.Throws<InvalidOperationException>(() =>
            _registry.Register(H.MakeTeam("team-1", "Duplicate")));
    }

    [Fact]
    public void ListTeams_ReturnsAll()
    {
        _registry.Register(H.MakeTeam("a", "Alpha"));
        _registry.Register(H.MakeTeam("b", "Beta"));

        Assert.Equal(2, _registry.ListTeams().Count);
    }

    [Fact]
    public void UpdateConfig_ChangesConfig()
    {
        _registry.Register(H.MakeTeam("team-1", "T1"));
        _registry.UpdateConfig("team-1", new TeamConfig { ContextBudgetTokens = 16384 });

        Assert.Equal(16384, _registry.GetTeam("team-1")!.Config.ContextBudgetTokens);
    }

    [Fact]
    public void UpdateConfig_UnknownTeam_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            _registry.UpdateConfig("nope", new TeamConfig()));
    }

    [Fact]
    public void RecordScore_AndRetrieve()
    {
        _registry.Register(H.MakeTeam("t", "T"));
        _registry.RecordScore("t", new QualityScore { Composite = 0.85f });

        var scores = _registry.GetScores("t");
        Assert.Single(scores);
        Assert.Equal(0.85f, scores[0].Composite);
    }

    [Fact]
    public void RecordFeedback_AndRetrieve()
    {
        _registry.Register(H.MakeTeam("t", "T"));
        _registry.RecordFeedback(H.MakeFeedback(rating: 4, teamId: "t"));

        var fb = _registry.GetFeedback("t");
        Assert.Single(fb);
        Assert.Equal(4, fb[0].Rating);
    }

    [Fact]
    public void BuildDashboard_CalculatesAggregates()
    {
        _registry.Register(H.MakeTeam("t", "TestTeam"));
        _registry.RecordScore("t", new QualityScore { Composite = 0.9f });
        _registry.RecordScore("t", new QualityScore { Composite = 0.7f });
        _registry.RecordFeedback(H.MakeFeedback(rating: 5, teamId: "t"));

        var db = _registry.BuildDashboard("t");

        Assert.Equal("TestTeam", db.TeamName);
        Assert.Equal(2, db.TotalTurns);
        Assert.Equal(0.8f, db.AvgQualityScore, 0.01f);
        Assert.Equal(1, db.FeedbackCount);
        Assert.Equal(5f, db.AvgUserRating);
    }

    [Fact]
    public void BuildDashboard_GeneratesRecommendations()
    {
        _registry.Register(H.MakeTeam("t", "T"));
        for (int i = 0; i < 5; i++)
            _registry.RecordScore("t", new QualityScore { Composite = 0.5f, RetrievalScore = 0.3f });

        Assert.NotEmpty(_registry.BuildDashboard("t").Recommendations);
    }

    [Fact]
    public void BuildDashboard_TargetHitRate_Correct()
    {
        _registry.Register(H.MakeTeam("t", "T")); // default target 0.7
        _registry.RecordScore("t", new QualityScore { Composite = 0.8f });
        _registry.RecordScore("t", new QualityScore { Composite = 0.6f });
        _registry.RecordScore("t", new QualityScore { Composite = 0.9f });
        _registry.RecordScore("t", new QualityScore { Composite = 0.5f });

        Assert.Equal(0.5f, _registry.BuildDashboard("t").TargetHitRate, 0.01f);
    }
}

#endregion

#region FeedbackLoop Tests

public class FeedbackLoopTests
{
    private readonly TeamRegistry _registry = new();
    private readonly FeedbackLoop _loop;

    public FeedbackLoopTests() => _loop = new FeedbackLoop(_registry);

    [Fact]
    public void ProcessTurn_ScoresAndRecords()
    {
        _registry.Register(H.MakeTeam("t", "T"));
        var score = _loop.ProcessTurn("t", H.MakeMetrics(chunks: 3, budget: 0.5f, cache: 0.2f, latencyMs: 2000));

        Assert.True(score.Composite > 0f);
        Assert.Single(_registry.GetScores("t"));
    }

    [Fact]
    public void ProcessTurn_WithFeedback_RecordsBoth()
    {
        _registry.Register(H.MakeTeam("t", "T"));
        _loop.ProcessTurn("t",
            H.MakeMetrics(chunks: 3, budget: 0.5f, cache: 0.2f, latencyMs: 2000),
            H.MakeFeedback(rating: 4, teamId: "t"));

        Assert.Single(_registry.GetScores("t"));
        Assert.Single(_registry.GetFeedback("t"));
    }

    [Fact]
    public void Analyze_InsufficientData_ReturnsMarker()
    {
        _registry.Register(H.MakeTeam("t", "T"));
        _loop.ProcessTurn("t", H.MakeMetrics(chunks: 1, budget: 0.3f, cache: 0f, latencyMs: 3000));
        _loop.ProcessTurn("t", H.MakeMetrics(chunks: 2, budget: 0.4f, cache: 0.1f, latencyMs: 2500));

        Assert.Equal("(insufficient data)", _loop.Analyze("t").TeamId);
    }

    [Fact]
    public void Analyze_UnknownTeam_ReturnsEmpty()
    {
        Assert.Equal(FeedbackAnalysis.Empty, _loop.Analyze("nonexistent"));
    }

    [Fact]
    public void Analyze_ImprovingTrend_Detected()
    {
        _registry.Register(H.MakeTeam("t", "T"));
        for (int i = 0; i < 10; i++)
            _loop.ProcessTurn("t", H.MakeMetrics(chunks: 1, budget: 0.2f, cache: 0f, latencyMs: 8000));
        for (int i = 0; i < 10; i++)
            _loop.ProcessTurn("t", H.MakeMetrics(chunks: 4, budget: 0.6f, cache: 0.3f, latencyMs: 1500));

        var analysis = _loop.Analyze("t");
        Assert.Equal(TrendDirection.Improving, analysis.TrendDirection);
        Assert.True(analysis.Trend > 0);
    }

    [Fact]
    public void Analyze_DecliningTrend_Detected()
    {
        _registry.Register(H.MakeTeam("t", "T"));
        for (int i = 0; i < 10; i++)
            _loop.ProcessTurn("t", H.MakeMetrics(chunks: 4, budget: 0.6f, cache: 0.3f, latencyMs: 1500));
        for (int i = 0; i < 10; i++)
            _loop.ProcessTurn("t", H.MakeMetrics(chunks: 0, budget: 0.05f, cache: 0f, latencyMs: 15000));

        var analysis = _loop.Analyze("t");
        Assert.Equal(TrendDirection.Declining, analysis.TrendDirection);
        Assert.True(analysis.Trend < 0);
    }

    [Fact]
    public void Analyze_LowRetrieval_SuggestsTopKIncrease()
    {
        _registry.Register(H.MakeTeam("t", "T", topK: 3));
        for (int i = 0; i < 5; i++)
            _loop.ProcessTurn("t", H.MakeMetrics(chunks: 0, budget: 0.1f, cache: 0f, latencyMs: 3000));

        var adj = _loop.Analyze("t").SuggestedAdjustments;
        Assert.Contains(adj, a => a.ConfigKey == "RetrievalTopK");
    }

    [Fact]
    public void Analyze_LowEfficiency_SuggestsBudgetReduction()
    {
        _registry.Register(H.MakeTeam("t", "T"));
        for (int i = 0; i < 5; i++)
            _loop.ProcessTurn("t", H.MakeMetrics(chunks: 3, budget: 0.08f, cache: 0.15f, latencyMs: 2000));

        var adj = _loop.Analyze("t").SuggestedAdjustments;
        Assert.Contains(adj, a => a.ConfigKey == "ContextBudgetTokens");
    }

    [Fact]
    public void Analyze_IdentifiesWeakestDimension()
    {
        _registry.Register(H.MakeTeam("t", "T"));
        for (int i = 0; i < 5; i++)
            _loop.ProcessTurn("t", H.MakeMetrics(chunks: 5, budget: 0.6f, cache: 0f, latencyMs: 500));

        Assert.Equal("cache", _loop.Analyze("t").WeakestDimension);
    }
}

#endregion

#region DashboardRenderer Tests

public class DashboardRendererTests
{
    [Fact]
    public void Render_ProducesDashboardOutput()
    {
        var dashboard = new TeamDashboard
        {
            TeamId = "test",
            TeamName = "TestTeam",
            TotalTurns = 20,
            AvgQualityScore = 0.82f,
            QualityTarget = 0.7f,
            TargetHitRate = 0.9f,
            FeedbackCount = 12,
            AvgUserRating = 4.2f,
            QualityTrend = new Dictionary<string, float>
            {
                ["Batch 1"] = 0.75f,
                ["Batch 2"] = 0.85f,
            },
            Recommendations = ["Test recommendation"],
        };

        var lines = DashboardRenderer.Render(dashboard);
        var output = string.Join("\n", lines);

        Assert.True(lines.Count > 5);
        Assert.Contains("TestTeam", output);
        Assert.Contains("Quality Score", output);
        Assert.Contains("Batch 1", output);
        Assert.Contains("Test recommendation", output);
    }

    [Fact]
    public void RenderComparison_ShowsBothTeams()
    {
        var d1 = new TeamDashboard { TeamId = "a", TeamName = "Alpha", AvgQualityScore = 0.9f };
        var d2 = new TeamDashboard { TeamId = "b", TeamName = "Beta", AvgQualityScore = 0.6f };

        var output = string.Join("\n", DashboardRenderer.RenderComparison(d1, d2));

        Assert.Contains("Alpha", output);
        Assert.Contains("Beta", output);
        Assert.Contains("leads by", output);
    }

    [Fact]
    public void RenderComparison_CloseTeams_ShowsMatched()
    {
        var d1 = new TeamDashboard { TeamId = "a", TeamName = "A", AvgQualityScore = 0.80f };
        var d2 = new TeamDashboard { TeamId = "b", TeamName = "B", AvgQualityScore = 0.82f };

        var output = string.Join("\n", DashboardRenderer.RenderComparison(d1, d2));

        Assert.Contains("closely matched", output);
    }
}

#endregion

#region TeamConfig Tests

public class TeamConfigTests
{
    [Fact]
    public void ToPipelineConfig_MapsCorrectly()
    {
        var config = new TeamConfig
        {
            ContextBudgetTokens = 4096,
            RetrievalTopK = 3,
            EnableMemory = false,
            EnableFactExtraction = false,
            SystemPrompt = "Custom prompt",
            PositioningStrategy = PositioningStrategy.PrimacyRecency,
        };

        var pc = config.ToPipelineConfig();

        Assert.Equal("Custom prompt", pc.SystemPrompt);
        Assert.False(pc.EnableMemory);
        Assert.False(pc.EnableFactExtraction);
        Assert.Equal(PositioningStrategy.PrimacyRecency, pc.PositioningStrategy);
        Assert.Equal(3, pc.RetrievalOptions.TopK);
    }

    [Fact]
    public void ToPipelineConfig_DefaultSystemPrompt_WhenNull()
    {
        var pc = new TeamConfig().ToPipelineConfig();
        Assert.Equal("You are a helpful assistant.", pc.SystemPrompt);
    }
}

#endregion

#region Test Helpers

file static class H
{
    public static TeamProfile MakeTeam(string id, string name, int topK = 5) => new()
    {
        TeamId = id,
        Name = name,
        Config = new TeamConfig { RetrievalTopK = topK },
    };

    public static TurnMetrics MakeMetrics(int chunks, float budget, float cache,
        int latencyMs, int memories = 0) => new()
    {
        RetrievedChunkCount = chunks,
        RecalledMemoryCount = memories,
        BudgetUtilization = budget,
        CacheRatio = cache,
        TotalLatency = TimeSpan.FromMilliseconds(latencyMs),
        RetrievalLatency = TimeSpan.FromMilliseconds(100),
        MemoryLatency = TimeSpan.FromMilliseconds(50),
        CompilationLatency = TimeSpan.FromMilliseconds(5),
        LlmLatency = TimeSpan.FromMilliseconds(latencyMs - 155),
        InputTokens = 500,
        OutputTokens = 200,
        CachedTokens = (int)(500 * cache),
    };

    public static QualityFeedback MakeFeedback(int rating, string teamId = "test",
        FeedbackIssue issues = FeedbackIssue.None) => new()
    {
        SessionId = "session-1",
        TurnIndex = 0,
        TeamId = teamId,
        UserId = "user-1",
        Rating = rating,
        Issues = issues,
    };
}

#endregion
