using Koshi.Core.Team;

namespace Koshi.Mcp.Internal;

/// <summary>
/// On-disk JSON shape for the team registry file (default location
/// <c>&lt;root&gt;/.koshi/teams.json</c>, overridable via <c>KOSHI_TEAMS_FILE</c>).
/// One envelope holds the full registry snapshot: every registered team, every
/// per-turn quality score keyed by team id, and every piece of user feedback
/// keyed by team id. Atomic write semantics are owned by <see cref="TeamsBackend"/>.
/// </summary>
/// <remarks>
/// <para>Single-envelope was a deliberate choice over a per-team JSONL log:
/// scoring volume for the documented workflow (a few teams × hundreds of
/// scores per session) is comfortably small, and atomic single-file writes
/// keep the recovery model symmetric with <c>memory.json</c>. If scoring ever
/// becomes a high-frequency path, callers can swap <see cref="TeamsBackend"/>
/// for a JSONL-based implementation without touching
/// <see cref="Koshi.Core.Team.TeamRegistry"/>.</para>
///
/// <para>All collection types here are concrete (List / Dictionary) so the AOT
/// source generator in <see cref="KoshiJsonContext"/> can emit metadata for
/// them. Do not change them to interface types without updating the context.</para>
/// </remarks>
public sealed class TeamsEnvelope
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TeamProfile> Teams { get; set; } = [];
    public Dictionary<string, List<QualityScore>> ScoresByTeam { get; set; } = [];
    public Dictionary<string, List<QualityFeedback>> FeedbackByTeam { get; set; } = [];

    /// <summary>
    /// Convert an immutable <see cref="TeamRegistrySnapshot"/> into a persistable envelope.
    /// </summary>
    public static TeamsEnvelope From(TeamRegistrySnapshot snapshot) => new()
    {
        SchemaVersion = 1,
        SavedAt = DateTimeOffset.UtcNow,
        Teams = [.. snapshot.Teams],
        ScoresByTeam = snapshot.ScoresByTeam.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToList()),
        FeedbackByTeam = snapshot.FeedbackByTeam.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ToList()),
    };
}
