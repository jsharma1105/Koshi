namespace Koshi.Core.Memory;

/// <summary>
/// Computes time-decayed relevance for memories.
/// Different memory types decay at different rates — decisions and preferences
/// are near-permanent, while transient facts fade unless re-accessed.
/// 
/// Formula: relevance = confidence × e^(-λ × days) × accessBoost
/// Where accessBoost = min(1.5, 1 + 0.1 × ln(1 + accessCount))
/// </summary>
public static class MemoryDecay
{
    // Decay rates per memory type (λ)
    // Higher = faster decay
    private static readonly Dictionary<MemoryType, float> DecayRates = new()
    {
        [MemoryType.Fact] = 0.05f,        // Half-life ≈ 14 days
        [MemoryType.Decision] = 0.005f,    // Half-life ≈ 139 days (near-permanent)
        [MemoryType.Pattern] = 0.02f,      // Half-life ≈ 35 days
        [MemoryType.Preference] = 0.003f,  // Half-life ≈ 231 days (near-permanent)
    };

    private const float MaxAccessBoost = 1.5f;

    /// <summary>
    /// Compute the current relevance of a memory, factoring in time decay and access frequency.
    /// </summary>
    public static float ComputeRelevance(MemoryRecord memory, DateTimeOffset now)
    {
        float lambda = DecayRates.GetValueOrDefault(memory.Type, 0.05f);
        double daysSinceAccess = Math.Max(0, (now - memory.LastAccessedAt).TotalDays);

        float timeDecay = (float)Math.Exp(-lambda * daysSinceAccess);
        float accessBoost = Math.Min(MaxAccessBoost, 1.0f + 0.1f * (float)Math.Log(1 + memory.AccessCount));

        return memory.Confidence * timeDecay * accessBoost;
    }

    /// <summary>
    /// Determine which tier a memory should be in based on its current relevance and age.
    /// </summary>
    public static MemoryTier RecommendTier(MemoryRecord memory, DateTimeOffset now)
    {
        float relevance = ComputeRelevance(memory, now);
        double daysSinceCreation = (now - memory.CreatedAt).TotalDays;

        return (relevance, daysSinceCreation) switch
        {
            ( >= 0.5f, _) => MemoryTier.Hot,
            ( >= 0.2f, _) => MemoryTier.Warm,
            (_, < 1) => MemoryTier.Hot, // Very recent, keep hot regardless
            _ => MemoryTier.Cold,
        };
    }

    /// <summary>
    /// Check if a memory should be considered for removal (below forget threshold).
    /// </summary>
    public static bool ShouldForget(MemoryRecord memory, DateTimeOffset now, float threshold = 0.05f)
    {
        // Never auto-forget decisions or preferences
        if (memory.Type is MemoryType.Decision or MemoryType.Preference)
            return false;

        return ComputeRelevance(memory, now) < threshold;
    }

    /// <summary>
    /// Get the half-life in days for a given memory type.
    /// Half-life = ln(2) / λ
    /// </summary>
    public static double GetHalfLifeDays(MemoryType type)
    {
        float lambda = DecayRates.GetValueOrDefault(type, 0.05f);
        return Math.Log(2) / lambda;
    }
}
