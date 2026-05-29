using Koshi.Core.Team;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for the persistence hooks added to <see cref="TeamRegistry"/> to fix
/// issue #59 — team registry + per-turn scores were lost on every server
/// restart. The on-disk serialisation itself is covered separately in
/// <c>TeamsBackendTests</c>; this suite exercises only the registry's
/// snapshot / restore / change-notification contract.
/// </summary>
public class TeamRegistryPersistenceTests
{
    private static TeamProfile MakeTeam(string id, string name = "Team") => new()
    {
        TeamId = id,
        Name = name,
        Description = $"Team {id}",
        Members = [],
        Config = new TeamConfig { ContextBudgetTokens = 1000, RetrievalTopK = 5, QualityTarget = 0.8f },
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static QualityScore MakeScore(float composite = 0.85f) => new()
    {
        Composite = composite,
        RetrievalScore = composite,
        EfficiencyScore = composite,
        CacheScore = composite,
        LatencyScore = composite,
        UserScore = composite,
    };

    private static QualityFeedback MakeFeedback(string teamId, int rating = 5) => new()
    {
        SessionId = "s1",
        TurnIndex = 0,
        TeamId = teamId,
        UserId = "u1",
        Rating = rating,
        Issues = FeedbackIssue.None,
        Timestamp = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Snapshot_returns_empty_snapshot_for_fresh_registry()
    {
        var reg = new TeamRegistry();

        var snap = reg.Snapshot();

        Assert.Empty(snap.Teams);
        Assert.Empty(snap.ScoresByTeam);
        Assert.Empty(snap.FeedbackByTeam);
    }

    [Fact]
    public void Snapshot_captures_all_three_collections()
    {
        var reg = new TeamRegistry();
        reg.Register(MakeTeam("alpha"));
        reg.RecordScore("alpha", MakeScore(0.9f));
        reg.RecordFeedback(MakeFeedback("alpha"));

        var snap = reg.Snapshot();

        Assert.Single(snap.Teams);
        Assert.Equal("alpha", snap.Teams[0].TeamId);
        Assert.Single(snap.ScoresByTeam["alpha"]);
        Assert.Equal(0.9f, snap.ScoresByTeam["alpha"][0].Composite);
        Assert.Single(snap.FeedbackByTeam["alpha"]);
    }

    [Fact]
    public void Snapshot_is_immutable_against_subsequent_mutations()
    {
        var reg = new TeamRegistry();
        reg.Register(MakeTeam("alpha"));
        reg.RecordScore("alpha", MakeScore(0.5f));

        var snap = reg.Snapshot();

        // Mutate the registry after taking the snapshot; the snapshot must
        // not reflect either the new score or the new team.
        reg.RecordScore("alpha", MakeScore(0.7f));
        reg.Register(MakeTeam("beta"));

        Assert.Single(snap.Teams);
        Assert.Single(snap.ScoresByTeam["alpha"]);
        Assert.Equal(0.5f, snap.ScoresByTeam["alpha"][0].Composite);
    }

    [Fact]
    public void Callback_fires_once_per_mutation()
    {
        int calls = 0;
        TeamRegistrySnapshot? lastSnap = null;
        var reg = new TeamRegistry(onChanged: snap => { calls++; lastSnap = snap; });

        reg.Register(MakeTeam("alpha"));
        Assert.Equal(1, calls);

        reg.RecordScore("alpha", MakeScore());
        Assert.Equal(2, calls);

        reg.RecordFeedback(MakeFeedback("alpha"));
        Assert.Equal(3, calls);

        reg.UpdateConfig("alpha", new TeamConfig { ContextBudgetTokens = 2000, RetrievalTopK = 10, QualityTarget = 0.9f });
        Assert.Equal(4, calls);

        Assert.NotNull(lastSnap);
        Assert.Equal(2000, lastSnap!.Teams[0].Config.ContextBudgetTokens);
    }

    [Fact]
    public void Callback_receives_snapshot_with_completed_mutation()
    {
        // Regression guard: callback must fire AFTER the registry mutation
        // is committed, otherwise a persistence layer that calls it during
        // the mutation would write a stale snapshot.
        int? scoreCountAtCallback = null;
        var reg = new TeamRegistry(onChanged: snap =>
        {
            if (snap.ScoresByTeam.TryGetValue("alpha", out var scores))
                scoreCountAtCallback = scores.Count;
        });

        reg.Register(MakeTeam("alpha"));
        reg.RecordScore("alpha", MakeScore());

        Assert.Equal(1, scoreCountAtCallback);
    }

    [Fact]
    public void Restore_does_not_fire_callback()
    {
        // Critical: restore is the inverse of persistence; firing the
        // callback during restore would produce an infinite load → save loop.
        int calls = 0;
        var reg = new TeamRegistry(onChanged: _ => calls++);

        reg.Restore([MakeTeam("alpha"), MakeTeam("beta")]);

        Assert.Equal(0, calls);
        Assert.Equal(2, reg.ListTeams().Count);
    }

    [Fact]
    public void Restore_round_trips_state_through_snapshot()
    {
        var src = new TeamRegistry();
        src.Register(MakeTeam("alpha", "Alpha team"));
        src.Register(MakeTeam("beta", "Beta team"));
        src.RecordScore("alpha", MakeScore(0.9f));
        src.RecordScore("alpha", MakeScore(0.7f));
        src.RecordFeedback(MakeFeedback("beta", rating: 3));

        var snap = src.Snapshot();

        var dst = new TeamRegistry();
        dst.Restore(snap.Teams, snap.ScoresByTeam, snap.FeedbackByTeam);

        Assert.Equal(2, dst.ListTeams().Count);
        Assert.Equal(2, dst.GetScores("alpha").Count);
        Assert.Single(dst.GetFeedback("beta"));
        Assert.Equal(3, dst.GetFeedback("beta")[0].Rating);
    }

    [Fact]
    public void Restore_drops_scores_for_unknown_team_ids()
    {
        var reg = new TeamRegistry();
        var orphanScores = new Dictionary<string, IReadOnlyList<QualityScore>>
        {
            ["alpha"] = [MakeScore()],
            ["ghost"] = [MakeScore(), MakeScore()],   // ghost team was deleted
        };

        reg.Restore([MakeTeam("alpha")], orphanScores);

        Assert.Single(reg.ListTeams());
        Assert.Single(reg.GetScores("alpha"));
        Assert.Empty(reg.GetScores("ghost"));   // dropped silently per documented Restore contract
    }

    [Fact]
    public void Restore_drops_feedback_for_unknown_team_ids()
    {
        var reg = new TeamRegistry();
        var orphanFeedback = new Dictionary<string, IReadOnlyList<QualityFeedback>>
        {
            ["alpha"] = [MakeFeedback("alpha")],
            ["ghost"] = [MakeFeedback("ghost")],
        };

        reg.Restore([MakeTeam("alpha")], feedbackByTeam: orphanFeedback);

        Assert.Single(reg.GetFeedback("alpha"));
        Assert.Empty(reg.GetFeedback("ghost"));
    }

    [Fact]
    public void Restore_resolves_duplicate_team_ids_last_wins()
    {
        var reg = new TeamRegistry();

        reg.Restore([
            MakeTeam("alpha", "First version"),
            MakeTeam("alpha", "Second version"),  // duplicate id, manually edited file
        ]);

        Assert.Single(reg.ListTeams());
        Assert.Equal("Second version", reg.GetTeam("alpha")!.Name);
    }

    [Fact]
    public void Restore_clears_existing_state_before_refilling()
    {
        var reg = new TeamRegistry();
        reg.Register(MakeTeam("alpha"));
        reg.RecordScore("alpha", MakeScore());

        reg.Restore([MakeTeam("beta")]);

        Assert.Null(reg.GetTeam("alpha"));
        Assert.Equal("beta", reg.ListTeams().Single().TeamId);
        Assert.Empty(reg.GetScores("alpha"));
    }

    [Fact]
    public void Restore_seeds_empty_score_and_feedback_buckets_for_each_team()
    {
        // Without seeding the per-team buckets on restore, the next
        // RecordScore call after a restart would throw KeyNotFoundException
        // for a team that had never been scored before — even though the
        // user already registered it.
        var reg = new TeamRegistry();
        reg.Restore([MakeTeam("alpha")]);

        // Should not throw, should not require Register-again first.
        reg.RecordScore("alpha", MakeScore());

        Assert.Single(reg.GetScores("alpha"));
    }

    [Fact]
    public void Parameterless_constructor_still_works_for_existing_callers()
    {
        // Regression guard for the public-API change: the existing
        // parameterless ctor must keep working (TeamTests.cs and any
        // external caller relies on this).
        var reg = new TeamRegistry();
        reg.Register(MakeTeam("alpha"));
        Assert.Single(reg.ListTeams());
    }
}
