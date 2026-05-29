using Koshi.Core.Context;
using Koshi.Core.Team;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// On-disk round-trip tests for <see cref="TeamsBackend"/>, the persistence
/// layer added for issue #59. Companion suite to
/// <see cref="TeamRegistryPersistenceTests"/> which tests the in-memory
/// snapshot / restore semantics.
/// </summary>
public class TeamsBackendTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _file;

    public TeamsBackendTests()
    {
        _tmpDir = Path.Join(Path.GetTempPath(), "koshi-teamsbe-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
        _file = Path.Join(_tmpDir, "teams.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { _ = ex; }
        GC.SuppressFinalize(this);
    }

    private static TeamProfile MakeTeam(string id, string name = "Team") => new()
    {
        TeamId = id,
        Name = name,
        Description = $"Team {id}",
        Members = [new TeamMember("alice", "Alice", TeamRole.Lead)],
        Config = new TeamConfig
        {
            ContextBudgetTokens = 2048,
            RetrievalTopK = 7,
            QualityTarget = 0.85f,
            PositioningStrategy = PositioningStrategy.RelevanceDescending,
            EnableMemory = true,
            SystemPrompt = "You are a tester.",
        },
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

    private static QualityFeedback MakeFeedback(string teamId, int rating = 4) => new()
    {
        SessionId = "session-1",
        TurnIndex = 7,
        TeamId = teamId,
        UserId = "alice",
        Rating = rating,
        Issues = FeedbackIssue.TooVerbose | FeedbackIssue.SlowResponse,
        Comment = "Took too long and was wordy",
        Timestamp = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Backend_metadata_reports_enabled_for_real_path()
    {
        var be = new TeamsBackend(_file);

        Assert.True(be.IsEnabled);
        Assert.Equal(Path.GetFullPath(_file), be.Path);
        Assert.Null(be.LastLoadError);
        Assert.Null(be.LastSaveError);
    }

    [Fact]
    public void Null_path_disables_persistence()
    {
        var be = new TeamsBackend(null);

        Assert.False(be.IsEnabled);
        Assert.Null(be.Path);

        // Save and Load are no-ops; no file should appear.
        be.Save(new TeamsEnvelope { Teams = [MakeTeam("alpha")] });
        var loaded = be.Load();

        Assert.Null(loaded);
    }

    [Fact]
    public void Whitespace_path_disables_persistence()
    {
        var be = new TeamsBackend("   ");
        Assert.False(be.IsEnabled);
    }

    [Fact]
    public void Load_returns_null_when_file_does_not_exist_yet()
    {
        var be = new TeamsBackend(_file);

        var loaded = be.Load();

        Assert.Null(loaded);
        Assert.Null(be.LastLoadError);   // cold start is not an error
    }

    [Fact]
    public void Save_then_Load_round_trips_full_envelope()
    {
        var be = new TeamsBackend(_file);
        var src = new TeamsEnvelope
        {
            Teams = [MakeTeam("alpha"), MakeTeam("beta", "Beta Team")],
            ScoresByTeam = new Dictionary<string, List<QualityScore>>
            {
                ["alpha"] = [MakeScore(0.95f), MakeScore(0.75f)],
                ["beta"] = [MakeScore(0.60f)],
            },
            FeedbackByTeam = new Dictionary<string, List<QualityFeedback>>
            {
                ["alpha"] = [MakeFeedback("alpha", rating: 5)],
            },
        };

        be.Save(src);
        var loaded = be.Load();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Teams.Count);
        Assert.Equal("Beta Team", loaded.Teams.Single(t => t.TeamId == "beta").Name);
        Assert.Equal(2, loaded.ScoresByTeam["alpha"].Count);
        Assert.Equal(0.95f, loaded.ScoresByTeam["alpha"][0].Composite);
        Assert.Single(loaded.FeedbackByTeam["alpha"]);
        Assert.Equal(FeedbackIssue.TooVerbose | FeedbackIssue.SlowResponse, loaded.FeedbackByTeam["alpha"][0].Issues);
    }

    [Fact]
    public void Save_creates_missing_parent_directory()
    {
        var nested = Path.Join(_tmpDir, "deeply", "nested", "teams.json");
        var be = new TeamsBackend(nested);

        be.Save(new TeamsEnvelope { Teams = [MakeTeam("alpha")] });

        Assert.True(File.Exists(nested));
        Assert.Null(be.LastSaveError);
    }

    [Fact]
    public void Save_is_atomic_via_temp_file_swap()
    {
        // We can't easily prove atomicity in a unit test, but we can prove
        // the temp file is cleaned up after a successful save — i.e. the
        // Move (not Copy+Delete) path is taken.
        var be = new TeamsBackend(_file);
        be.Save(new TeamsEnvelope { Teams = [MakeTeam("alpha")] });

        Assert.True(File.Exists(_file));
        Assert.False(File.Exists(_file + ".tmp"));
    }

    [Fact]
    public void Save_overwrites_existing_file()
    {
        var be = new TeamsBackend(_file);
        be.Save(new TeamsEnvelope { Teams = [MakeTeam("alpha")] });
        be.Save(new TeamsEnvelope { Teams = [MakeTeam("beta")] });

        var loaded = be.Load();

        Assert.NotNull(loaded);
        Assert.Single(loaded!.Teams);
        Assert.Equal("beta", loaded.Teams[0].TeamId);
    }

    [Fact]
    public void Load_returns_null_and_records_LastLoadError_on_corrupt_json()
    {
        File.WriteAllText(_file, "{ this is not valid json");
        var be = new TeamsBackend(_file);

        var loaded = be.Load();

        Assert.Null(loaded);
        Assert.NotNull(be.LastLoadError);
        Assert.Contains("JsonException", be.LastLoadError!);
    }

    [Fact]
    public void Load_returns_null_on_empty_file_without_recording_error()
    {
        File.WriteAllText(_file, "");
        var be = new TeamsBackend(_file);

        var loaded = be.Load();

        Assert.Null(loaded);
        Assert.Null(be.LastLoadError);
    }

    [Fact]
    public void Load_clears_LastLoadError_on_subsequent_success()
    {
        File.WriteAllText(_file, "not valid");
        var be = new TeamsBackend(_file);
        _ = be.Load();
        Assert.NotNull(be.LastLoadError);

        // Repair the file and reload — the sticky error should clear.
        be.Save(new TeamsEnvelope { Teams = [MakeTeam("alpha")] });
        _ = be.Load();

        Assert.Null(be.LastLoadError);
    }

    [Fact]
    public void Envelope_From_snapshot_factory_preserves_all_data()
    {
        var reg = new TeamRegistry();
        reg.Register(MakeTeam("alpha"));
        reg.RecordScore("alpha", MakeScore());
        reg.RecordFeedback(MakeFeedback("alpha"));

        var envelope = TeamsEnvelope.From(reg.Snapshot());

        Assert.Equal(1, envelope.SchemaVersion);
        Assert.Single(envelope.Teams);
        Assert.Single(envelope.ScoresByTeam["alpha"]);
        Assert.Single(envelope.FeedbackByTeam["alpha"]);
    }

    [Fact]
    public void Round_trip_through_backend_into_registry_restores_full_state()
    {
        // End-to-end: registry → snapshot → envelope → file → envelope → registry.
        // This is the exact path the production code takes on shutdown +
        // restart, and the closest unit test to the user-visible behaviour
        // the original bug report (#59) was about.
        var src = new TeamRegistry();
        src.Register(MakeTeam("alpha", "Alpha"));
        src.Register(MakeTeam("beta", "Beta"));
        src.RecordScore("alpha", MakeScore(0.92f));
        src.RecordFeedback(MakeFeedback("beta", rating: 3));

        var be = new TeamsBackend(_file);
        be.Save(TeamsEnvelope.From(src.Snapshot()));

        var loaded = be.Load();
        Assert.NotNull(loaded);

        var dst = new TeamRegistry();
        var scoresByTeam = loaded!.ScoresByTeam.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<QualityScore>)kv.Value);
        var feedbackByTeam = loaded.FeedbackByTeam.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<QualityFeedback>)kv.Value);
        dst.Restore(loaded.Teams, scoresByTeam, feedbackByTeam);

        Assert.Equal(2, dst.ListTeams().Count);
        Assert.Equal("Alpha", dst.GetTeam("alpha")!.Name);
        Assert.Equal(0.92f, dst.GetScores("alpha").Single().Composite);
        Assert.Equal(3, dst.GetFeedback("beta").Single().Rating);
    }
}
