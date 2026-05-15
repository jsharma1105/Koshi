using System.ComponentModel;
using System.Text.Json;
using Koshi.Core.Memory;
using Koshi.Mcp.Internal;
using ModelContextProtocol.Server;

namespace Koshi.Mcp.Tools;

/// <summary>
/// MCP tools for memory management — store, recall, and manage facts.
/// Memories live in-process by default; set KOSHI_MEMORY_FILE to persist to disk as JSON.
/// </summary>
[McpServerToolType]
public sealed class MemoryTools
{
    private const int MaxMemories = 1_000;
    private const int MaxRecallTopK = 25;

    private static readonly Lock _lock = new();
    private static readonly List<MemoryRecord> _memories;
    private static int _nextId;
    private static readonly MemoryPersistence _persistence;

    static MemoryTools()
    {
        _persistence = new MemoryPersistence(Environment.GetEnvironmentVariable("KOSHI_MEMORY_FILE"));
        _memories = _persistence.LoadOrEmpty();
        _nextId = _memories
            .Select(m => int.TryParse(m.Id.AsSpan(m.Id.LastIndexOf('-') + 1), out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    [McpServerTool(Name = "koshi_remember"), Description(
        "Store a fact, decision, pattern, or preference in memory. " +
        "Memories persist for the session, and across restarts when KOSHI_MEMORY_FILE is set.")]
    public static string Remember(
        [Description("The content to remember")] string content,
        [Description("Subject/topic of this memory")] string subject,
        [Description("Type: Fact, Decision, Pattern, or Preference")] string type = "Fact",
        [Description("Confidence 0-1 (how certain is this?)")] float confidence = 0.8f,
        [Description("Source of this information (e.g., 'user', 'codebase', 'documentation')")]
        string source = "user")
    {
        if (string.IsNullOrWhiteSpace(content))
            return "❌ Content must not be empty.";
        if (string.IsNullOrWhiteSpace(subject))
            return "❌ Subject must not be empty.";

        confidence = Math.Clamp(confidence, 0f, 1f);
        var memType = Enum.TryParse<MemoryType>(type, true, out var mt) ? mt : MemoryType.Fact;

        var record = new MemoryRecord
        {
            Id = $"mem-{Interlocked.Increment(ref _nextId):D6}",
            Type = memType,
            Content = content,
            Subject = subject,
            Scope = MemoryScope.Global(),
            Source = source,
            Confidence = confidence,
        };

        lock (_lock)
        {
            if (_memories.Count >= MaxMemories)
                return $"❌ Memory limit reached ({MaxMemories}). Use koshi_forget to free space.";
            _memories.Add(record);
            _persistence.Save(_memories);
        }

        var preview = content.Length > 80 ? content[..80] + "..." : content;
        return $"✅ Remembered [{memType}] about '{subject}': \"{preview}\" (confidence: {confidence:P0})";
    }

    [McpServerTool(Name = "koshi_recall"), Description(
        "Recall memories relevant to a topic. Searches stored facts, decisions, and patterns " +
        "with a keyword + recency + confidence ranking.")]
    public static string Recall(
        [Description("Topic or query to search memories for")] string query,
        [Description("Filter by type: Fact, Decision, Pattern, Preference, or All")] string type = "All",
        [Description("Maximum results to return (1-25, default: 5)")] int topK = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "❌ Query must not be empty.";

        topK = Math.Clamp(topK < 1 ? 5 : topK, 1, MaxRecallTopK);

        List<MemoryRecord> candidates;
        lock (_lock) { candidates = [.. _memories]; }

        if (candidates.Count == 0)
            return "No memories stored yet. Use koshi_remember to store facts.";

        if (!string.Equals(type, "All", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<MemoryType>(type, true, out var mt))
        {
            candidates = candidates.Where(m => m.Type == mt).ToList();
        }

        var queryTerms = query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var scored = candidates
            .Select(m =>
            {
                var text = $"{m.Subject} {m.Content}".ToLowerInvariant();
                float matchScore = queryTerms.Length == 0
                    ? 0
                    : queryTerms.Count(t => text.Contains(t)) / (float)queryTerms.Length;
                float recencyBonus = (float)Math.Exp(-(DateTimeOffset.UtcNow - m.CreatedAt).TotalHours / 24.0);
                // Weights sum to 1.0 — match dominates, recency complements, confidence breaks ties.
                return (Memory: m, Score: matchScore * 0.6f + recencyBonus * 0.3f + m.Confidence * 0.1f);
            })
            .Where(x => x.Score > 0.05f)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        if (scored.Count == 0)
            return $"No memories found matching '{query}'.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ Recalled {scored.Count} memories for: \"{query}\" ═══\n");

        foreach (var (mem, score) in scored)
        {
            sb.AppendLine($"  [{mem.Type}] {mem.Subject} (score: {score:F2}, confidence: {mem.Confidence:P0})");
            sb.AppendLine($"    {mem.Content}");
            sb.AppendLine($"    Source: {mem.Source} | Stored: {mem.CreatedAt:g}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "koshi_memory_stats"), Description(
        "Show statistics about stored memories: counts by type, top subjects, average confidence, and persistence status.")]
    public static string MemoryStats()
    {
        List<MemoryRecord> all;
        lock (_lock) { all = [.. _memories]; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ Memory Stats ({all.Count} of {MaxMemories} max) ═══\n");
        sb.AppendLine($"  Persistence: {(_persistence.IsEnabled ? $"enabled → {_persistence.Path}" : "disabled (in-memory only)")}");
        sb.AppendLine();

        if (all.Count == 0)
        {
            sb.AppendLine("  No memories stored yet.");
            return sb.ToString();
        }

        var byType = all.GroupBy(m => m.Type).ToDictionary(g => g.Key, g => g.Count());
        var bySubject = all.GroupBy(m => m.Subject).OrderByDescending(g => g.Count()).Take(5);

        sb.AppendLine("  By type:");
        foreach (var (t, count) in byType)
            sb.AppendLine($"    {t}: {count}");

        sb.AppendLine("\n  Top subjects:");
        foreach (var group in bySubject)
            sb.AppendLine($"    {group.Key}: {group.Count()} memories");

        sb.AppendLine($"\n  Avg confidence: {all.Average(m => m.Confidence):P0}");
        sb.AppendLine($"  Oldest: {all.Min(m => m.CreatedAt):g}");
        sb.AppendLine($"  Newest: {all.Max(m => m.CreatedAt):g}");

        return sb.ToString();
    }

    [McpServerTool(Name = "koshi_forget"), Description(
        "Remove a memory by its subject. Removes all memories matching the subject (case-insensitive).")]
    public static string Forget(
        [Description("Subject to forget (case-insensitive match)")] string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "❌ Subject must not be empty.";

        int removed;
        lock (_lock)
        {
            removed = _memories.RemoveAll(m =>
                m.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) _persistence.Save(_memories);
        }

        return removed > 0
            ? $"✅ Forgot {removed} memory(ies) about '{subject}'."
            : $"No memories found with subject '{subject}'.";
    }

    [McpServerTool(Name = "koshi_clear_memories"), Description(
        "Clear ALL stored memories. Useful when switching projects or resetting state. " +
        "Requires confirm=true to actually clear.")]
    public static string ClearMemories(
        [Description("Set to true to confirm deletion of all memories")] bool confirm = false)
    {
        if (!confirm)
            return "⚠️ This will delete ALL memories. Re-call with confirm=true to proceed.";

        int removed;
        lock (_lock)
        {
            removed = _memories.Count;
            _memories.Clear();
            _persistence.Save(_memories);
        }

        return removed == 0
            ? "Memory store already empty."
            : $"✅ Cleared {removed} memory(ies).";
    }

    internal static (int count, bool persistenceEnabled, string? path) GetStatus()
    {
        lock (_lock)
        {
            return (_memories.Count, _persistence.IsEnabled, _persistence.Path);
        }
    }
}
