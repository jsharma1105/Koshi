using System.ComponentModel;
using Koshi.Core.Harness;
using Koshi.Core.Team;
using Koshi.Mcp.Internal;
using ModelContextProtocol.Server;
using static Koshi.Mcp.Internal.JsonShapes;

namespace Koshi.Mcp.Tools;

/// <summary>
/// MCP tools for quality scoring and team management.
/// Team registry, per-turn scores, and per-turn feedback persist across
/// server restarts in <c>&lt;project-root&gt;/.koshi/teams.json</c> by default.
/// Override the location with <c>KOSHI_TEAMS_FILE</c>.
/// </summary>
[McpServerToolType]
public sealed class TeamTools
{
    private static readonly TeamsBackend _backend;
    private static readonly TeamRegistry _registry;
    private static readonly QualityScorer _scorer = new();
    private static readonly FeedbackLoop _loop;
    // Single lock that serialises every persistence write. Without this two
    // concurrent mutations can race: callback A snapshots state v1, callback
    // B snapshots v2, then A wins the file write and v2 is silently lost —
    // exactly the "lost after restart" symptom the user filed #59 about.
    private static readonly object _saveLock = new();

    // Snapshot-load telemetry for koshi_health (#70). Captured once in the
    // static constructor; subsequent mutations do not update them.
    private static readonly bool _loadAttempted;
    private static readonly int _loadedTeamCount;
    private static readonly int _loadedScoreCount;
    private static readonly int _loadedFeedbackCount;
    private static readonly DateTimeOffset? _loadedAt;

    static TeamTools()
    {
        var paths = PathConfig.Default;
        _backend = new TeamsBackend(paths.TeamsFile);
        _registry = new TeamRegistry(onChanged: snapshot =>
        {
            // Serialised so concurrent mutations cannot reorder the on-disk
            // state behind the live registry's back. Backend exceptions are
            // swallowed here (they're already on stderr + LastSaveError);
            // mutation tool responses surface the failure to the MCP user.
            lock (_saveLock)
            {
                try { _backend.Save(TeamsEnvelope.From(snapshot)); }
                catch (TeamsPersistenceException) { /* observed via _backend.LastSaveError */ }
            }
        });
        _loop = new FeedbackLoop(_registry, _scorer);

        _loadAttempted = _backend.IsEnabled;
        var loaded = _backend.Load();
        if (loaded is not null)
        {
            var scoresByTeam = loaded.ScoresByTeam.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<QualityScore>)kv.Value);
            var feedbackByTeam = loaded.FeedbackByTeam.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<QualityFeedback>)kv.Value);
            _registry.Restore(loaded.Teams, scoresByTeam, feedbackByTeam);

            _loadedTeamCount = loaded.Teams.Count;
            _loadedScoreCount = loaded.ScoresByTeam.Values.Sum(s => s.Count);
            _loadedFeedbackCount = loaded.FeedbackByTeam.Values.Sum(f => f.Count);
        }
        if (_loadAttempted) _loadedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Status snapshot for <c>koshi_health</c>. Forces this type's static
    /// constructor to run (which loads the persisted teams file) the first
    /// time it is called, so health output never lies about "0 teams" simply
    /// because no team tool has been called yet this process.
    /// </summary>
    public static TeamsStatus GetStatus()
    {
        var snap = _registry.Snapshot();
        int scoreCount = snap.ScoresByTeam.Values.Sum(s => s.Count);
        int feedbackCount = snap.FeedbackByTeam.Values.Sum(f => f.Count);
        return new TeamsStatus(
            TeamCount: snap.Teams.Count,
            ScoreCount: scoreCount,
            FeedbackCount: feedbackCount,
            PersistenceEnabled: _backend.IsEnabled,
            Path: _backend.Path,
            LastLoadError: _backend.LastLoadError,
            LastSaveError: _backend.LastSaveError,
            LoadAttempted: _loadAttempted,
            LoadedTeamCount: _loadedTeamCount,
            LoadedScoreCount: _loadedScoreCount,
            LoadedFeedbackCount: _loadedFeedbackCount,
            LoadedAt: _loadedAt);
    }

    [McpServerTool(Name = "koshi_register_team"), Description(
        "WHEN TO CALL: When the user asks to set up team-scoped quality tracking and context " +
        "engineering policy (token budget, topK, quality target, system prompt). Once per team — " +
        "subsequent calls update. Skip if the project does not need per-team policy.\n" +
        "WHAT IT DOES: Persists a TeamProfile so koshi_score_turn / koshi_team_dashboard / " +
        "koshi_analyze_feedback can attribute scores and metrics to this team across restarts.\n" +
        "WHAT YOU GIVE IT: teamId + name (required); description; tokenBudget (default 8192); topK " +
        "(default 5); qualityTarget (0-1, default 0.7); systemPrompt; teamContext (cacheable).")]
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
            return AppendWarning(
                $"✅ Registered team '{name}' (id: {teamId}, budget: {tokenBudget}, topK: {topK}, target: {qualityTarget:P0})",
                PersistenceWarning());
        }
        catch (InvalidOperationException ex)
        {
            return $"❌ {ex.Message}";
        }
    }

    [McpServerTool(Name = "koshi_score_turn"), Description(
        "WHEN TO CALL: After responding to the user, to score the just-completed turn against the " +
        "team's quality config. Skip if no team is registered for this workspace. Cheap — call once " +
        "per meaningful turn.\n" +
        "WHAT IT DOES: Computes a composite quality score across retrieval / efficiency / cache / " +
        "latency / user-rating dimensions; appends to the team's score and metrics history (persisted).\n" +
        "WHAT YOU GIVE IT: teamId (required); retrievedChunks; memoriesRecalled; budgetUtilization " +
        "(0-1); cacheRatio (0-1); latencyMs; userRating (1-5, 0 = no rating); optional metric tags. " +
        "Pass format=\"json\" for a parseable envelope (#66).")]
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
        int tokensUsed = 0,
        [Description("Output mode: 'text' (default, human-readable) or 'json' (stable structured envelope, issue #66).")]
        string? format = null)
    {
        var fmt = OutputFormatting.Resolve(format, out var fmtErr);
        if (fmtErr is not null)
            return fmt == OutputFormat.Json
                ? OutputFormatting.Error<ScoreTurnResultData>(OutputErrorCodes.InvalidFormat, fmtErr,
                    KoshiOutputJsonContext.Default.JsonEnvelopeScoreTurnResultData)
                : "❌ " + fmtErr;

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

        var issueFlags = ParseIssues(issues);
        QualityFeedback? feedback = null;
        if (userRating > 0)
        {
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

        if (fmt == OutputFormat.Json)
        {
            var breakdown = new ScoreBreakdownData(
                Retrieval: score.RetrievalScore,
                Efficiency: score.EfficiencyScore,
                Cache: score.CacheScore,
                Latency: score.LatencyScore,
                User: score.UserScore);
            var target = teamProfile is not null
                ? new ScoreTargetData(teamProfile.Config.QualityTarget, score.MeetsTarget(teamProfile.Config.QualityTarget))
                : new ScoreTargetData(null, null);
            var parsedIssues = IssueFlagsToList(issueFlags);
            var warning = PersistenceWarning();
            var payload = new ScoreTurnResultData(
                TeamId: teamId,
                TeamRegistered: teamProfile is not null,
                Composite: score.Composite,
                Grade: score.Grade.ToString(),
                Breakdown: breakdown,
                Target: target,
                IssuesParsed: parsedIssues,
                PersistenceWarning: warning is null ? null : OutputFormatting.StripTextDecorations(warning));
            return OutputFormatting.Ok(payload,
                KoshiOutputJsonContext.Default.JsonEnvelopeScoreTurnResultData);
        }

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

        return AppendWarning(sb.ToString(), PersistenceWarning());
    }

    private static List<string> IssueFlagsToList(FeedbackIssue flags)
    {
        var list = new List<string>();
        if (flags == FeedbackIssue.None) return list;
        if ((flags & FeedbackIssue.Irrelevant) != 0) list.Add("irrelevant");
        if ((flags & FeedbackIssue.Incomplete) != 0) list.Add("incomplete");
        if ((flags & FeedbackIssue.Hallucinated) != 0) list.Add("hallucinated");
        if ((flags & FeedbackIssue.TooVerbose) != 0) list.Add("verbose");
        if ((flags & FeedbackIssue.TooTerse) != 0) list.Add("terse");
        if ((flags & FeedbackIssue.WrongFormat) != 0) list.Add("format");
        if ((flags & FeedbackIssue.Outdated) != 0) list.Add("outdated");
        if ((flags & FeedbackIssue.SlowResponse) != 0) list.Add("slow");
        return list;
    }

    [McpServerTool(Name = "koshi_team_dashboard"), Description(
        "WHEN TO CALL: When the user asks how the team is doing, when investigating a drop in answer " +
        "quality, or when reviewing score trends. Requires the team to be registered.\n" +
        "WHAT IT DOES: Renders the team's quality dashboard — score trend, latency / budget / cache " +
        "metrics, target-attainment, and recommendations.\n" +
        "WHAT YOU GIVE IT: teamId (required).")]
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
        "WHEN TO CALL: When the user asks for tuning recommendations after at least 3 turns have been " +
        "scored for this team. Returns ⚠ if there is insufficient data.\n" +
        "WHAT IT DOES: Trend analysis across recent scores — direction, weakest dimension, and concrete " +
        "config-key adjustments (e.g., raise topK, lower budget).\n" +
        "WHAT YOU GIVE IT: teamId (required).")]
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
        "WHEN TO CALL: To check which teams are registered before scoring or analysing, or when the " +
        "user asks 'which teams do we have set up?'.\n" +
        "WHAT IT DOES: Lists every registered team with id, name, budget, topK, target, and current " +
        "average quality score.")]
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

    /// <summary>
    /// If the most recent save attempt failed, return a one-line warning the
    /// caller should append to the tool's success message. The in-memory
    /// mutation already succeeded; this just gives the MCP user a visible
    /// signal that durability is broken before they hit "lost on restart".
    /// </summary>
    private static string? PersistenceWarning()
    {
        var err = _backend.LastSaveError;
        if (err is null) return null;
        return $"⚠ Team persistence failed: {err}. " +
               $"Mutation kept in memory but will not survive restart. " +
               $"Check write access to '{_backend.Path}' or run koshi_health for details.";
    }

    private static string AppendWarning(string body, string? warning) =>
        warning is null ? body : body + "\n" + warning;
}

/// <summary>
/// Lightweight status snapshot for <c>koshi_health</c>. Plain record so the
/// diagnostic tool can build the Teams section without taking another
/// registry snapshot.
/// </summary>
public sealed record TeamsStatus(
    int TeamCount,
    int ScoreCount,
    int FeedbackCount,
    bool PersistenceEnabled,
    string? Path,
    string? LastLoadError,
    string? LastSaveError,
    bool LoadAttempted,
    int LoadedTeamCount,
    int LoadedScoreCount,
    int LoadedFeedbackCount,
    DateTimeOffset? LoadedAt);
