namespace Koshi.Core.Memory;

using Koshi.Core.Retrieval;

/// <summary>
/// Detects possible contradictions between new and existing memories.
/// Uses subject matching + embedding similarity to find conflicts.
/// Flags only — does NOT auto-supersede (that's a human decision).
/// </summary>
public sealed class ContradictionDetector
{
    private readonly IMemoryStore _memoryStore;
    private readonly float _similarityThreshold;

    public ContradictionDetector(IMemoryStore memoryStore, float similarityThreshold = 0.85f)
    {
        _memoryStore = memoryStore;
        _similarityThreshold = similarityThreshold;
    }

    /// <summary>
    /// Check a new memory against existing memories for possible contradictions.
    /// Returns any existing memories on the same subject with high embedding similarity.
    /// </summary>
    public async Task<IReadOnlyList<Contradiction>> DetectAsync(
        MemoryRecord incoming, CancellationToken ct = default)
    {
        if (incoming.Embedding is null) return [];

        var contradictions = new List<Contradiction>();

        // Get all existing memories in the same scope
        var existing = await _memoryStore.GetByScopeAsync(incoming.Scope, ct: ct);

        foreach (var candidate in existing)
        {
            ct.ThrowIfCancellationRequested();
            if (candidate.Id == incoming.Id) continue;
            if (candidate.Embedding is null) continue;
            if (candidate.SupersededBy is not null) continue; // Already superseded

            // Check 1: Same or similar subject
            bool subjectMatch = string.Equals(
                candidate.Subject, incoming.Subject, StringComparison.OrdinalIgnoreCase);

            if (!subjectMatch)
            {
                // Fuzzy subject match — check if subjects share significant words
                subjectMatch = HasSignificantOverlap(candidate.Subject, incoming.Subject);
            }

            if (!subjectMatch) continue;

            // Check 2: High embedding similarity (same topic, potentially different conclusion)
            float similarity = Similarity.Cosine(incoming.Embedding, candidate.Embedding);

            if (similarity >= _similarityThreshold)
            {
                contradictions.Add(new Contradiction(
                    Existing: candidate,
                    Incoming: incoming,
                    Similarity: similarity,
                    Explanation: $"Same subject '{incoming.Subject}' with {similarity:P0} embedding similarity"));
            }
        }

        return contradictions;
    }

    /// <summary>
    /// Flag a memory as superseded by another. Reversible — just sets a field.
    /// </summary>
    public async Task SupersedeAsync(
        string existingId, string newId, string note, CancellationToken ct = default)
    {
        var existing = await _memoryStore.GetAsync(existingId, ct);
        if (existing is null) return;

        var updated = existing with
        {
            SupersededBy = newId,
            ContradictionNote = note,
        };

        await _memoryStore.UpdateAsync(updated, ct);
    }

    /// <summary>
    /// Undo a superseding — restores the memory to active status.
    /// </summary>
    public async Task UndoSupersedeAsync(string id, CancellationToken ct = default)
    {
        var memory = await _memoryStore.GetAsync(id, ct);
        if (memory is null) return;

        var restored = memory with
        {
            SupersededBy = null,
            ContradictionNote = $"[Restored] {memory.ContradictionNote}",
        };

        await _memoryStore.UpdateAsync(restored, ct);
    }

    private static bool HasSignificantOverlap(string subject1, string subject2)
    {
        var words1 = subject1.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToHashSet();
        var words2 = subject2.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2).ToHashSet();

        if (words1.Count == 0 || words2.Count == 0) return false;

        int overlap = words1.Intersect(words2).Count();
        int minCount = Math.Min(words1.Count, words2.Count);

        // At least half of the shorter subject's words must overlap
        return overlap >= Math.Max(1, minCount / 2);
    }
}
