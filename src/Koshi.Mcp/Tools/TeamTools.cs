using System.ComponentModel;
using Koshi.Core.Harness;
using Koshi.Core.Team;
using ModelContextProtocol.Server;

namespace Koshi.Mcp.Tools;

/// <summary>
/// MCP tools for quality scoring and team management.
/// </summary>
[McpServerToolType]
public sealed class TeamTools
{
    private static readonly TeamRegistry _registry = new();
    private static readonly QualityScorer _scorer = new();
    private static readonly FeedbackLoop _loop = new(_registry, _scorer);

    [McpServerTool(Name = "koshi_register_team"), Description(
        "Register a team with custom configuration for context engineering.")]
    public static string RegisterTeam(
        [Description("Unique team identifier (e.g., 'platform-team')")] string teamId,
        [Description("Team display name")] string name,
        [Description("Team description")] string? description = null,
        [Description("Token budget (default: 8192)")] int tokenBudget = 8192,
        [Description("Retrieval TopK (default: 5)")] int topK = 5,
        [Description("Quality target 0-1 (default: 0.7)")] float qualityTarget = 0.7f,
        [Description("Custom system prompt")] string? systemPrompt = null,
        [Description("Team conventions/context (cacheable)")] string? teamContext = null)
    {
        try
        {
            var team = new TeamProfile
            {
                TeamId = teamId,
                Name = name,
                Description = description ?? "",
                Config = new TeamConfig
                {
                    ContextBudgetTokens = tokenBudget,
                    RetrievalTopK = topK,
                    QualityTarget = qualityTarget,
                    SystemPrompt = systemPrompt,
                    TeamContext = teamContext,
                },
            };

            _registry.Register(team);
            return $"✅ Registered team '{name}' (id: {teamId}, budget: {tokenBudget}, topK: {topK}, target: {qualityTarget:P0})";
        }
        catch (InvalidOperationException ex)
        {
            return $"❌ {ex.Message}";
        }
    }

    [McpServerTool(Name = "koshi_score_turn"), Description(
        "Score the quality of an AI interaction turn. " +
        "Provide metrics about the turn and get a composite quality score.")]
    public static string ScoreTurn(
        [Description("Team ID to score for")] string teamId,
        [Description("Number of retrieved chunks used")] int retrievedChunks = 0,
        [Description("Number of memories recalled")] int memoriesRecalled = 0,
        [Description("Budget utilization 0-1 (tokens used / budget)")] float budgetUtilization = 0.5f,
        [Description("Cache ratio 0-1 (cached tokens / total input tokens)")] float cacheRatio = 0f,
        [Description("Total latency in milliseconds")] int latencyMs = 3000,
        [Description("User rating 1-5 (0 = no rating)")] int userRating = 0,
        [Description("Issues: irrelevant,incomplete,hallucinated,verbose,terse,format,outdated,slow (comma-separated)")]
        string? issues = null,
        [Description("Total input tokens for the turn (0 = derive from budgetUtilization x team's ContextBudgetTokens)")]
        int tokensUsed = 0)
    {
        var teamProfile = _registry.GetTeam(teamId);
        int inputTokens = tokensUsed > 0
            ? tokensUsed
            : teamProfile is not null
                ? (int)Math.Round(budgetUtilization * teamProfile.Config.ContextBudgetTokens)
                : 0;
        int cachedTokens = (int)Math.Round(cacheRatio * inputTokens);

        var metrics = new TurnMetrics
        {
            InputTokens = inputTokens,
            CachedTokens = cachedTokens,
            RetrievedChunkCount = retrievedChunks,
            RecalledMemoryCount = memoriesRecalled,
            BudgetUtilization = budgetUtilization,
            CacheRatio = cacheRatio,
            TotalLatency = TimeSpan.FromMilliseconds(latencyMs),
            RetrievalLatency = TimeSpan.FromMilliseconds(latencyMs * 0.1),
            LlmLatency = TimeSpan.FromMilliseconds(latencyMs * 0.8),
        };

        QualityFeedback? feedback = null;
        if (userRating > 0)
        {
            var issueFlags = ParseIssues(issues);
            feedback = new QualityFeedback
            {
                SessionId = "mcp-session",
                TurnIndex = 0,
                TeamId = teamId,
                UserId = "mcp-user",
                Rating = userRating,
                Issues = issueFlags,
            };
        }

        QualityScore score = teamProfile is not null
            ? _loop.ProcessTurn(teamId, metrics, feedback)
            : _scorer.Score(metrics, feedback);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ Quality Score: {score.Grade} ({score.Composite:F2}) ═══\n");
        sb.AppendLine($"  Retrieval:  {Bar(score.RetrievalScore)} {score.RetrievalScore:F2}");
        sb.AppendLine($"  Efficiency: {Bar(score.EfficiencyScore)} {score.EfficiencyScore:F2}");
        sb.AppendLine($"  Cache:      {Bar(score.CacheScore)} {score.CacheScore:F2}");
        sb.AppendLine($"  Latency:    {Bar(score.LatencyScore)} {score.LatencyScore:F2}");
        sb.AppendLine($"  User:       {Bar(score.UserScore)} {score.UserScore:F2}");
        sb.AppendLine();
        sb.AppendLine($"  Composite:  {Bar(score.Composite)} {score.Composite:F2} → Grade {score.Grade}");

        if (teamProfile is not null)
        {
            bool meetsTarget = score.MeetsTarget(teamProfile.Config.QualityTarget);
            sb.AppendLine($"\n  Target: {teamProfile.Config.QualityTarget:F2} → {(meetsTarget ? "✅ Met" : "❌ Below target")}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "koshi_team_dashboard"), Description(
        "Show quality dashboard for a registered team with trends and recommendations.")]
    public static string Dashboard(
        [Description("Team ID to show dashboard for")] string teamId)
    {
        if (_registry.GetTeam(teamId) is null)
            return $"❌ Team '{teamId}' not found. Register it first with koshi_register_team.";

        var dashboard = _registry.BuildDashboard(teamId);
        var lines = DashboardRenderer.Render(dashboard);
        return string.Join("\n", lines);
    }

    [McpServerTool(Name = "koshi_analyze_feedback"), Description(
        "Analyze quality trends for a team and get config improvement suggestions.")]
    public static string AnalyzeFeedback(
        [Description("Team ID to analyze")] string teamId)
    {
        if (_registry.GetTeam(teamId) is null)
            return $"❌ Team '{teamId}' not found.";

        var analysis = _loop.Analyze(teamId);

        if (analysis.TeamId == "(insufficient data)")
            return "⚠️ Not enough data yet. Score at least 3 turns first.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ Feedback Analysis: {teamId} ═══\n");

        var trendIcon = analysis.TrendDirection switch
        {
            TrendDirection.Improving => "📈",
            TrendDirection.Declining => "📉",
            _ => "➡️",
        };

        sb.AppendLine($"  Trend: {trendIcon} {analysis.TrendDirection} ({analysis.Trend:+0.00;-0.00})");
        sb.AppendLine($"  Current avg score: {analysis.CurrentAvgScore:F2}");
        sb.AppendLine($"  Weakest dimension: {analysis.WeakestDimension}");
        sb.AppendLine($"  Turns analyzed: {analysis.TurnCount}");

        if (analysis.SuggestedAdjustments.Count > 0)
        {
            sb.AppendLine($"\n  Suggested adjustments:");
            foreach (var adj in analysis.SuggestedAdjustments)
            {
                sb.AppendLine($"    • {adj.ConfigKey}: {adj.CurrentValue} → {adj.SuggestedValue}");
                sb.AppendLine($"      Reason: {adj.Reason}");
            }
        }
        else
        {
            sb.AppendLine("\n  ✅ No adjustments needed — all dimensions healthy.");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "koshi_list_teams"), Description(
        "List all registered teams and their configurations.")]
    public static string ListTeams()
    {
        var teams = _registry.ListTeams();
        if (teams.Count == 0)
            return "No teams registered. Use koshi_register_team to create one.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ Registered Teams ({teams.Count}) ═══\n");

        foreach (var team in teams)
        {
            var scores = _registry.GetScores(team.TeamId);
            var avgScore = scores.Count > 0 ? scores.Average(s => s.Composite) : 0;

            sb.AppendLine($"  [{team.TeamId}] {team.Name}");
            sb.AppendLine($"    Budget: {team.Config.ContextBudgetTokens}, TopK: {team.Config.RetrievalTopK}, Target: {team.Config.QualityTarget:P0}");
            sb.AppendLine($"    Turns scored: {scores.Count}, Avg quality: {avgScore:F2}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Bar(float value)
    {
        int len = (int)(value * 15);
        return new string('█', len) + new string('░', 15 - len);
    }

    private static FeedbackIssue ParseIssues(string? issues)
    {
        if (string.IsNullOrWhiteSpace(issues)) return FeedbackIssue.None;

        var flags = FeedbackIssue.None;
        foreach (var part in issues.Split(',', StringSplitOptions.TrimEntries))
        {
            flags |= part.ToLowerInvariant() switch
            {
                "irrelevant" => FeedbackIssue.Irrelevant,
                "incomplete" => FeedbackIssue.Incomplete,
                "hallucinated" => FeedbackIssue.Hallucinated,
                "verbose" => FeedbackIssue.TooVerbose,
                "terse" => FeedbackIssue.TooTerse,
                "format" => FeedbackIssue.WrongFormat,
                "outdated" => FeedbackIssue.Outdated,
                "slow" => FeedbackIssue.SlowResponse,
                _ => FeedbackIssue.None,
            };
        }
        return flags;
    }
}
