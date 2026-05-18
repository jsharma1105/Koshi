using System.ComponentModel;
using Koshi.Core.Memory;
using Koshi.Core.Models;
using Koshi.Core.Retrieval;
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
        "Memories persist for the session, and across restarts when KOSHI_MEMORY_FILE is set. " +
        "Scope (userId/workspaceId/threadId) controls who can recall the memory. " +
        "Leave userId unset (or pass '*') to make the memory globally visible.")]
    public static string Remember(
        [Description("The content to remember")] string content,
        [Description("Subject/topic of this memory")] string subject,
        [Description("Type: Fact, Decision, Pattern, or Preference")] string type = "Fact",
        [Description("Confidence 0-1 (how certain is this?)")] float confidence = 0.8f,
        [Description("Source of this information (e.g., 'user', 'codebase', 'documentation')")]
        string source = "user",
        [Description("Optional user identifier. Empty or '*' means globally visible.")]
        string? userId = null,
        [Description("Optional workspace identifier. Defaults to 'default'.")]
        string? workspaceId = null,
        [Description("Optional thread identifier for conversation-scoped memories.")]
        string? threadId = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "❌ Content must not be empty.";
        if (string.IsNullOrWhiteSpace(subject))
            return "❌ Subject must not be empty.";

        confidence = Math.Clamp(confidence, 0f, 1f);
        var memType = Enum.TryParse<MemoryType>(type, true, out var mt) ? mt : MemoryType.Fact;

        var scope = new MemoryScope(
            UserId: string.IsNullOrWhiteSpace(userId) ? "*" : userId.Trim(),
            WorkspaceId: string.IsNullOrWhiteSpace(workspaceId) ? "default" : workspaceId.Trim(),
            ThreadId: string.IsNullOrWhiteSpace(threadId) ? null : threadId.Trim());

        var record = new MemoryRecord
        {
            Id = $"mem-{Interlocked.Increment(ref _nextId):D6}",
            Type = memType,
            Content = content,
            Subject = subject,
            Scope = scope,
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
        var scopeLabel = scope.UserId == "*"
            ? $"workspace='{scope.WorkspaceId}'"
            : $"user='{scope.UserId}', workspace='{scope.WorkspaceId}'";
        if (scope.ThreadId is not null) scopeLabel += $", thread='{scope.ThreadId}'";
        return $"✅ Remembered [{memType}] about '{subject}' ({scopeLabel}): \"{preview}\" (confidence: {confidence:P0})";
    }

    [McpServerTool(Name = "koshi_recall"), Description(
        "Recall memories relevant to a topic. Searches stored facts, decisions, and patterns " +
        "using BM25 keyword ranking blended with recency and confidence. " +
        "Scope filters (userId/workspaceId/threadId) narrow the candidate set; " +
        "globally-scoped memories (UserId='*') are always visible regardless of the userId filter.")]
    public static string Recall(
        [Description("Topic or query to search memories for")] string query,
        [Description("Filter by type: Fact, Decision, Pattern, Preference, or All")] string type = "All",
        [Description("Maximum results to return (1-25, default: 5)")] int topK = 5,
        [Description("Optional user filter. Empty = no user filter. Global ('*') memories are always returned.")]
        string? userId = null,
        [Description("Optional workspace filter. Empty = no workspace filter.")]
        string? workspaceId = null,
        [Description("Optional thread filter. Empty = no thread filter.")]
        string? threadId = null)
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

        var hasScopeFilter = !string.IsNullOrWhiteSpace(userId)
                          || !string.IsNullOrWhiteSpace(workspaceId)
                          || !string.IsNullOrWhiteSpace(threadId);
        if (hasScopeFilter)
        {
            candidates = candidates.Where(m => MatchesScope(m.Scope, userId, workspaceId, threadId)).ToList();
        }

        if (candidates.Count == 0)
            return hasScopeFilter
                ? $"No memories match the scope filter (user='{userId}', workspace='{workspaceId}', thread='{threadId}')."
                : $"No memories of type '{type}'.";

        var bm25Scores = ComputeBm25Scores(query, candidates);
        double maxBm25 = bm25Scores.Count > 0 ? bm25Scores.Values.Max() : 0.0;
        var bm25Denominator = maxBm25 > 0 ? maxBm25 : 1.0;

        var now = DateTimeOffset.UtcNow;
        var scored = candidates
            .Select(m =>
            {
                var rawBm25 = bm25Scores.GetValueOrDefault(m.Id, 0.0);
                var normalizedBm25 = (float)(rawBm25 / bm25Denominator);
                var recencyBonus = (float)Math.Exp(-(now - m.CreatedAt).TotalHours / 24.0);
                // Per #24 acceptance criteria: 0.6·BM25 + 0.3·recency + 0.1·confidence.
                var combined = 0.6f * normalizedBm25 + 0.3f * recencyBonus + 0.1f * m.Confidence;
                return (Memory: m, Score: combined, Bm25: normalizedBm25);
            })
            // Require some textual relevance — pure recency/confidence shouldn't surface unrelated memories.
            .Where(x => x.Bm25 > 0)
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        if (scored.Count == 0)
            return $"No memories found matching '{query}'.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"═══ Recalled {scored.Count} memories for: \"{query}\" ═══\n");

        foreach (var (mem, score, _) in scored)
        {
            var scopeLabel = mem.Scope.UserId == "*"
                ? $"workspace='{mem.Scope.WorkspaceId}'"
                : $"user='{mem.Scope.UserId}', workspace='{mem.Scope.WorkspaceId}'";
            if (mem.Scope.ThreadId is not null) scopeLabel += $", thread='{mem.Scope.ThreadId}'";

            sb.AppendLine($"  [{mem.Type}] {mem.Subject} (score: {score:F2}, confidence: {mem.Confidence:P0})");
            sb.AppendLine($"    {mem.Content}");
            sb.AppendLine($"    Scope: {scopeLabel} | Source: {mem.Source} | Stored: {mem.CreatedAt:g}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns true when the memory's scope is visible to the caller's filter.
    /// Globally-scoped memories (UserId == "*") are ALWAYS visible; the user filter
    /// is otherwise an exact (case-insensitive) match. Workspace and thread filters
    /// require exact matches when provided. Empty/whitespace filter strings disable
    /// the corresponding filter.
    /// </summary>
    private static bool MatchesScope(MemoryScope scope, string? userId, string? workspaceId, string? threadId)
    {
        bool userOk = string.IsNullOrWhiteSpace(userId)
                   || scope.UserId == "*"
                   || string.Equals(scope.UserId, userId, StringComparison.OrdinalIgnoreCase);

        bool workspaceOk = string.IsNullOrWhiteSpace(workspaceId)
                        || string.Equals(scope.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase);

        bool threadOk = string.IsNullOrWhiteSpace(threadId)
                     || string.Equals(scope.ThreadId, threadId, StringComparison.OrdinalIgnoreCase);

        return userOk && workspaceOk && threadOk;
    }

    /// <summary>
    /// Builds a transient BM25 index over the candidate memories and runs the query.
    /// Returns memoryId -&gt; raw BM25 score (zero or omitted for non-matches). Memory cap
    /// is 1,000, so the per-call indexing cost is negligible (typically &lt; 5 ms).
    /// </summary>
    private static Dictionary<string, double> ComputeBm25Scores(string query, IReadOnlyList<MemoryRecord> candidates)
    {
        if (candidates.Count == 0) return [];

        var chunks = new List<Chunk>(candidates.Count);
        foreach (var m in candidates)
        {
            chunks.Add(new Chunk(
                Id: m.Id,
                Content: $"{m.Subject} {m.Content}",
                Metadata: new ChunkMetadata(
                    Source: m.Subject,
                    DocumentType: "memory",
                    StartOffset: 0,
                    EndOffset: 0,
                    IngestedAt: m.CreatedAt)));
        }

        var retriever = new KeywordRetriever();
        retriever.Index(chunks);
        var results = retriever
            .SearchAsync(query, new RetrievalOptions(TopK: candidates.Count))
            .GetAwaiter().GetResult();

        var scores = new Dictionary<string, double>(results.Count);
        foreach (var r in results)
            scores[r.Chunk.Id] = r.Score;
        return scores;
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
