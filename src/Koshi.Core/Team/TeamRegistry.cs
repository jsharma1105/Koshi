namespace Koshi.Core.Team;

using System.Collections.Concurrent;
using Koshi.Core.Harness;

/// <summary>
/// Immutable snapshot of a <see cref="TeamRegistry"/> safe to hand to a
/// persistence layer or any other consumer that needs a stable point-in-time
/// view. Constructed via <see cref="TeamRegistry.Snapshot"/>; the registry's
/// onChanged callback fires with one of these every time the registry mutates.
/// </summary>
public sealed record TeamRegistrySnapshot(
    IReadOnlyList<TeamProfile> Teams,
    IReadOnlyDictionary<string, IReadOnlyList<QualityScore>> ScoresByTeam,
    IReadOnlyDictionary<string, IReadOnlyList<QualityFeedback>> FeedbackByTeam);

/// <summary>
/// Registry for managing teams and their configurations.
/// Provides multi-tenant isolation — each team gets its own
/// memory scope, quality tracking, and pipeline config.
///
/// <para>Optionally persists across process restarts: pass an
/// <c>onChanged</c> callback to the constructor and the registry will invoke
/// it (with an immutable <see cref="TeamRegistrySnapshot"/>) after every
/// successful mutation. State can be re-populated on startup via
/// <see cref="Restore"/>, which deliberately does <em>not</em> fire the
/// callback — that would otherwise produce a load → save → load loop.</para>
/// </summary>
public sealed class TeamRegistry
{
    private readonly ConcurrentDictionary<string, TeamProfile> _teams = new();
    private readonly ConcurrentDictionary<string, List<QualityFeedback>> _feedback = new();
    private readonly ConcurrentDictionary<string, List<QualityScore>> _scores = new();
    private readonly ConcurrentDictionary<string, List<TurnMetrics>> _metrics = new();
    private readonly Action<TeamRegistrySnapshot>? _onChanged;

    /// <summary>Construct an in-memory-only registry (no persistence).</summary>
    public TeamRegistry() : this(onChanged: null) { }

    /// <summary>
    /// Construct a registry whose every mutation is reported to
    /// <paramref name="onChanged"/> as an immutable snapshot. Callers wire
    /// persistence in there; the registry itself never touches disk and has
    /// no IO dependencies (Koshi.Core stays IO-free).
    /// </summary>
    public TeamRegistry(Action<TeamRegistrySnapshot>? onChanged)
    {
        _onChanged = onChanged;
    }

    /// <summary>Register a new team.</summary>
    public void Register(TeamProfile team)
    {
        if (!_teams.TryAdd(team.TeamId, team))
            throw new InvalidOperationException($"Team '{team.TeamId}' already registered");
        _feedback[team.TeamId] = [];
        _scores[team.TeamId] = [];
        _metrics[team.TeamId] = [];
        NotifyChanged();
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
        NotifyChanged();
    }

    /// <summary>Record a quality score for a team. Optionally records the raw per-turn metrics that produced it so the dashboard can aggregate Tokens Used / Avg Latency / Cache Hit Rate / Budget Utilization without needing a side-channel QualityTracker.</summary>
    public void RecordScore(string teamId, QualityScore score, TurnMetrics? metrics = null)
    {
        if (!_scores.TryGetValue(teamId, out var scores))
            throw new KeyNotFoundException($"Team '{teamId}' not found");
        lock (scores) { scores.Add(score); }
        if (metrics is not null && _metrics.TryGetValue(teamId, out var metricsList))
        {
            lock (metricsList) { metricsList.Add(metrics); }
        }
        NotifyChanged();
    }

    /// <summary>Record a quality score and its raw per-turn metrics for a team.</summary>
    public void RecordMetrics(string teamId, TurnMetrics metrics)
    {
        if (!_metrics.TryGetValue(teamId, out var metricsList))
            throw new KeyNotFoundException($"Team '{teamId}' not found");
        lock (metricsList) { metricsList.Add(metrics); }
    }

    /// <summary>Record user feedback for a team.</summary>
    public void RecordFeedback(QualityFeedback feedback)
    {
        if (!_feedback.TryGetValue(feedback.TeamId, out var feedbackList))
            throw new KeyNotFoundException($"Team '{feedback.TeamId}' not found");
        lock (feedbackList) { feedbackList.Add(feedback); }
        NotifyChanged();
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

    /// <summary>Get all raw per-turn metrics recorded for a team.</summary>
    public IReadOnlyList<TurnMetrics> GetMetrics(string teamId)
    {
        if (!_metrics.TryGetValue(teamId, out var m)) return [];
        lock (m) { return [.. m]; }
    }

    /// <summary>
    /// Take an immutable point-in-time snapshot of every team, every per-team
    /// score list, and every per-team feedback list. Each inner list is a
    /// fresh copy — mutating the registry after taking a snapshot does not
    /// affect the snapshot. Used by the persistence layer; also useful for
    /// diagnostics and testing.
    /// </summary>
    public TeamRegistrySnapshot Snapshot()
    {
        IReadOnlyList<TeamProfile> teams = [.. _teams.Values];
        var scoresByTeam = new Dictionary<string, IReadOnlyList<QualityScore>>(_teams.Count);
        foreach (var kv in _scores)
        {
            lock (kv.Value)
            {
                scoresByTeam[kv.Key] = [.. kv.Value];
            }
        }
        var feedbackByTeam = new Dictionary<string, IReadOnlyList<QualityFeedback>>(_teams.Count);
        foreach (var kv in _feedback)
        {
            lock (kv.Value)
            {
                feedbackByTeam[kv.Key] = [.. kv.Value];
            }
        }
        return new TeamRegistrySnapshot(teams, scoresByTeam, feedbackByTeam);
    }

    /// <summary>
    /// Replace the entire registry contents from a persisted snapshot.
    /// Deliberately does <em>not</em> fire the onChanged callback — restoring
    /// is the inverse of persistence and would otherwise produce a write loop.
    ///
    /// <para>Referential integrity is enforced on restore: scores or feedback
    /// keyed against a team id that is not in <paramref name="teams"/> are
    /// dropped (with a stderr diagnostic listing the dropped count). Duplicate
    /// team ids in the input collection are resolved last-wins, again with a
    /// stderr diagnostic so manual edits to <c>teams.json</c> don't silently
    /// pick a non-deterministic winner.</para>
    /// </summary>
    public void Restore(
        IEnumerable<TeamProfile> teams,
        IReadOnlyDictionary<string, IReadOnlyList<QualityScore>>? scoresByTeam = null,
        IReadOnlyDictionary<string, IReadOnlyList<QualityFeedback>>? feedbackByTeam = null)
    {
        ArgumentNullException.ThrowIfNull(teams);

        _teams.Clear();
        _scores.Clear();
        _feedback.Clear();
        _metrics.Clear();

        var duplicates = 0;
        foreach (var team in teams)
        {
            if (!_teams.TryAdd(team.TeamId, team))
            {
                _teams[team.TeamId] = team;
                duplicates++;
            }
            // Pre-seed empty lists so RecordScore / RecordFeedback / RecordMetrics
            // succeed even for teams with no history yet.
            _scores.TryAdd(team.TeamId, []);
            _feedback.TryAdd(team.TeamId, []);
            _metrics.TryAdd(team.TeamId, []);
        }
        if (duplicates > 0)
        {
            Console.Error.WriteLine(
                $"[koshi] TeamRegistry.Restore: {duplicates} duplicate team id(s) in restore input; last entry wins.");
        }

        var orphanScoreTeams = 0;
        if (scoresByTeam is not null)
        {
            foreach (var (teamId, scores) in scoresByTeam)
            {
                if (!_teams.ContainsKey(teamId))
                {
                    orphanScoreTeams++;
                    continue;
                }
                _scores[teamId] = [.. scores];
            }
        }
        if (orphanScoreTeams > 0)
        {
            Console.Error.WriteLine(
                $"[koshi] TeamRegistry.Restore: dropped scores for {orphanScoreTeams} unknown team id(s).");
        }

        var orphanFeedbackTeams = 0;
        if (feedbackByTeam is not null)
        {
            foreach (var (teamId, feedback) in feedbackByTeam)
            {
                if (!_teams.ContainsKey(teamId))
                {
                    orphanFeedbackTeams++;
                    continue;
                }
                _feedback[teamId] = [.. feedback];
            }
        }
        if (orphanFeedbackTeams > 0)
        {
            Console.Error.WriteLine(
                $"[koshi] TeamRegistry.Restore: dropped feedback for {orphanFeedbackTeams} unknown team id(s).");
        }
    }

    private void NotifyChanged()
    {
        var cb = _onChanged;
        if (cb is null) return;
        cb(Snapshot());
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

        // Aggregate the raw per-turn metrics. Prefer the harness QualityTracker
        // when one is supplied (preserves existing SessionOrchestrator behavior);
        // otherwise compute from the metrics we captured in RecordScore so that
        // dashboards built purely from koshi_score_turn (MCP path) populate
        // Tokens Used / Avg Latency / Cache Hit Rate / Budget Util.
        var metrics = GetMetrics(teamId);
        long totalTokens;
        float avgCacheRatio;
        float avgBudgetUtilization;
        double avgLatencyMs;
        int fallbackCount;
        if (qualityTracker is not null)
        {
            totalTokens = qualityTracker.TotalTokensConsumed;
            avgCacheRatio = qualityTracker.AvgCacheRatio;
            avgBudgetUtilization = qualityTracker.AvgBudgetUtilization;
            avgLatencyMs = qualityTracker.AvgTotalLatency.TotalMilliseconds;
            fallbackCount = qualityTracker.FallbackCount;
        }
        else if (metrics.Count > 0)
        {
            totalTokens = metrics.Sum(m => (long)m.InputTokens + m.OutputTokens);
            avgCacheRatio = metrics.Average(m => m.CacheRatio);
            avgBudgetUtilization = metrics.Average(m => m.BudgetUtilization);
            avgLatencyMs = metrics.Average(m => m.TotalLatency.TotalMilliseconds);
            fallbackCount = 0; // TurnMetrics has no FallbackLevel — tracked only via harness
        }
        else
        {
            totalTokens = 0;
            avgCacheRatio = 0;
            avgBudgetUtilization = 0;
            avgLatencyMs = 0;
            fallbackCount = 0;
        }

        return new TeamDashboard
        {
            TeamId = teamId,
            TeamName = team.Name,
            TotalTurns = scores.Count,
            TotalSessions = feedback.Select(f => f.SessionId).Distinct().Count(),
            TotalTokensConsumed = totalTokens,
            AvgQualityScore = avgScore,
            AvgCacheHitRate = avgCacheRatio,
            AvgBudgetUtilization = avgBudgetUtilization,
            AvgLatencyMs = avgLatencyMs,
            FallbackCount = fallbackCount,
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
