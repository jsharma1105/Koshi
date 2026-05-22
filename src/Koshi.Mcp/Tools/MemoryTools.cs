using System.ComponentModel;
using Koshi.Core.Memory;
using Koshi.Core.Models;
using Koshi.Core.Retrieval;
using Koshi.Mcp.Internal;
using ModelContextProtocol.Server;

namespace Koshi.Mcp.Tools;

/// <summary>
/// MCP tools for memory management — store, recall, and manage facts.
/// Memories live in-process by default. Set <c>KOSHI_MEMORY_FILE</c> to persist as a single
/// JSON envelope, or <c>KOSHI_MEMORY_VAULT</c> to persist as one Markdown file per memory
/// under <c>&lt;vault&gt;/koshi/{facts,decisions,patterns,preferences}/</c> for Git-friendly
/// team sharing.
/// </summary>
[McpServerToolType]
public sealed class MemoryTools
{
    private const int MaxMemories = 1_000;
    private const int MaxRecallTopK = 25;

    private static readonly MemoryStore _store;

    static MemoryTools()
    {
        var paths = PathConfig.Default;

        IMemoryBackend backend;
        if (paths.MemoryVault is not null)
        {
            // Vault is opt-in. If KOSHI_MEMORY_FILE was also set explicitly,
            // warn that vault wins — defaults never trigger this warning
            // because vault has no default.
            if (paths.MemoryFileFromEnv)
                Console.Error.WriteLine(
                    "[koshi] Both KOSHI_MEMORY_VAULT and KOSHI_MEMORY_FILE are set; vault takes precedence.");
            backend = new VaultBackend(paths.MemoryVault);
        }
        else
        {
            // JSON backend: persistence is now ALWAYS on (defaults to
            // <project>/.koshi/memory.json) — pass the resolved path so the
            // backend writes there. Pass null only if the resolved path is
            // somehow blank, which the resolver guarantees it isn't.
            backend = new JsonFileBackend(paths.MemoryFile);
        }
        _store = new MemoryStore(backend);

        // Drop <root>/.koshi/.gitignore so memory + index files don't get
        // accidentally committed when Koshi is using the default state dir.
        // Best-effort, never throws.
        paths.EnsureStateDirGitIgnore();
    }

    [McpServerTool(Name = "koshi_remember"), Description(
        "Store a fact, decision, pattern, or preference in memory. " +
        "Memories persist across restarts by default (saved to <project-root>/.koshi/memory.json " +
        "in v0.6.0+). Set KOSHI_MEMORY_FILE to override the path, or KOSHI_MEMORY_VAULT to switch " +
        "to a Git-friendly Obsidian-style vault. " +
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
        string? threadId = null,
        [Description("If true and an IEmbeddingProvider is registered (see koshi_health), embed the content and store the vector alongside the memory for future hybrid recall. Default: false. No-op when no provider is configured.")]
        bool embedSelf = false)
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

        float[]? embedding = null;
        string? embeddingModel = null;
        int embeddingDims = 0;
        string? embedNote = null;
        if (embedSelf)
        {
            var provider = Koshi.Core.Retrieval.EmbeddingProviderRegistry.Current;
            if (provider is null)
            {
                embedNote = "embedSelf=true ignored: no IEmbeddingProvider registered (install Koshi.Embeddings.* and set KOSHI_EMBEDDING_PROVIDER).";
            }
            else
            {
                try
                {
                    embedding = provider.EmbedAsync(content).GetAwaiter().GetResult();
                    embeddingModel = provider.ModelName;
                    embeddingDims = provider.Dimensions;
                }
                catch (Exception ex)
                {
                    embedNote = $"embedSelf=true failed: {ex.Message}";
                }
            }
        }

        return _store.WithFreshState(memories =>
        {
            if (memories.Count >= MaxMemories)
                return $"❌ Memory limit reached ({MaxMemories}). Use koshi_forget to free space.";

            var record = new MemoryRecord
            {
                Id = _store.AllocateId(),
                Type = memType,
                Content = content,
                Subject = subject,
                Scope = scope,
                Source = source,
                Confidence = confidence,
                Embedding = embedding,
                EmbeddingModel = embeddingModel,
                EmbeddingDimensions = embeddingDims,
            };

            memories.Add(record);
            _store.Upsert(record);

            var preview = content.Length > 80 ? content[..80] + "..." : content;
            var scopeLabel = scope.UserId == "*"
                ? $"workspace='{scope.WorkspaceId}'"
                : $"user='{scope.UserId}', workspace='{scope.WorkspaceId}'";
            if (scope.ThreadId is not null) scopeLabel += $", thread='{scope.ThreadId}'";
            var msg = $"✅ Remembered [{memType}] about '{subject}' ({scopeLabel}): \"{preview}\" (confidence: {confidence:P0})";
            if (embedding is not null)
                msg += $"\n   🔢 embedded ({embeddingModel}, dim={embeddingDims})";
            if (embedNote is not null)
                msg += $"\n   ⚠ {embedNote}";
            if (!_store.Backend.IsEnabled)
                msg += "\n   ⚠ Persistence is disabled — memory is in-process only. Set KOSHI_MEMORY_FILE or KOSHI_MEMORY_VAULT to persist across restarts.";
            return msg;
        });
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

        return _store.WithFreshState(memories =>
        {
            List<MemoryRecord> candidates = [.. memories];

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
        });
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
        "Show statistics about stored memories: counts by type, top subjects, average confidence, " +
        "persistence status, and (for vault backends) counts of unmanaged notes and duplicate-id warnings.")]
    public static string MemoryStats()
    {
        return _store.WithFreshState(all =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"═══ Memory Stats ({all.Count} of {MaxMemories} max) ═══\n");

            var backend = _store.Backend;
            sb.AppendLine($"  Backend:        {backend.BackendKind}");
            sb.AppendLine($"  Persistence:    {(backend.IsEnabled ? $"enabled → {backend.Location}" : "disabled (in-memory only)")}");
            if (backend.BackendKind == "vault")
            {
                sb.AppendLine($"  Unmanaged notes: {backend.UnmanagedNoteCount}");
                if (backend.UnmanagedNoteCount > 0)
                {
                    foreach (var p in backend.UnmanagedNotePaths.Take(5))
                        sb.AppendLine($"    - {p}");
                    if (backend.UnmanagedNotePaths.Count > 5)
                        sb.AppendLine($"    ... and {backend.UnmanagedNotePaths.Count - 5} more");
                }
                sb.AppendLine($"  Duplicate-id warnings: {backend.DuplicateIdWarningCount}");
            }
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
        });
    }

    [McpServerTool(Name = "koshi_forget"), Description(
        "Remove a memory by its subject. Removes all memories matching the subject (case-insensitive).")]
    public static string Forget(
        [Description("Subject to forget (case-insensitive match)")] string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return "❌ Subject must not be empty.";

        return _store.WithFreshState(memories =>
        {
            var toRemove = memories
                .Where(m => m.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (toRemove.Count == 0)
                return $"No memories found with subject '{subject}'.";

            foreach (var rec in toRemove) memories.Remove(rec);
            foreach (var rec in toRemove) _store.Delete(rec.Id);

            return $"✅ Forgot {toRemove.Count} memory(ies) about '{subject}'.";
        });
    }

    [McpServerTool(Name = "koshi_clear_memories"), Description(
        "Clear ALL stored memories. Useful when switching projects or resetting state. " +
        "Requires confirm=true to actually clear.")]
    public static string ClearMemories(
        [Description("Set to true to confirm deletion of all memories")] bool confirm = false)
    {
        if (!confirm)
            return "⚠️ This will delete ALL memories. Re-call with confirm=true to proceed.";

        return _store.WithFreshState(memories =>
        {
            int removed = memories.Count;
            _store.ReplaceAll([]);
            return removed == 0
                ? "Memory store already empty."
                : $"✅ Cleared {removed} memory(ies).";
        });
    }

    [McpServerTool(Name = "koshi_memory_export_to_vault"), Description(
        "Bulk-export current memories as Markdown files into the target vault. " +
        "Defaults to Obsidian layout (<vaultPath>/koshi/<type>/), independent of the active " +
        "KOSHI_VAULT_FLAVOR env var. Override via flavor=obsidian|foam|logseq|dendron. " +
        "By default refuses to write into a non-empty vault. Pass overwrite=true to replace " +
        "existing koshi-tagged files in the target vault (user notes without koshi.id are never touched).")]
    public static string ExportToVault(
        [Description("Path to the target vault directory")] string vaultPath,
        [Description("If true, replace existing koshi-tagged files in the vault")] bool overwrite = false,
        [Description("Layout flavor for the target vault: obsidian (default) | foam | logseq | dendron")] string flavor = "obsidian")
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            return "❌ vaultPath must not be empty.";

        var resolved = PathConfig.Default.ResolveUserPath(vaultPath)!;
        var layout = VaultLayout.Resolve(flavor);

        VaultBackend target;
        try { target = new VaultBackend(resolved, watch: false, layout: layout); }
        catch (Exception ex) { return $"❌ Could not open vault '{resolved}': {ex.Message}"; }

        try
        {
            var existing = target.LoadAll();
            if (existing.Count > 0 && !overwrite)
                return $"❌ Vault already contains {existing.Count} managed memories at '{target.Location}'. " +
                       "Pass overwrite=true to replace them.";

            var snapshot = _store.WithFreshState(memories => memories.ToList());
            target.ReplaceAll(snapshot);
            return $"✅ Exported {snapshot.Count} memories to vault at '{target.Location}'.";
        }
        finally { target.Dispose(); }
    }

    [McpServerTool(Name = "koshi_memory_import_from_vault"), Description(
        "Import memories from a Markdown vault into the current backend. " +
        "Defaults to Obsidian layout, independent of the active KOSHI_VAULT_FLAVOR env var. " +
        "Override via flavor=obsidian|foam|logseq|dendron to match the source vault's layout. " +
        "mode='merge' (default): id collisions keep current memory. " +
        "mode='overlay': id collisions, vault wins. " +
        "mode='replace': drop all current memories, take vault as-is.")]
    public static string ImportFromVault(
        [Description("Path to the source vault directory")] string vaultPath,
        [Description("Conflict resolution mode: merge | overlay | replace")] string mode = "merge",
        [Description("Layout flavor for the source vault: obsidian (default) | foam | logseq | dendron")] string flavor = "obsidian")
    {
        if (string.IsNullOrWhiteSpace(vaultPath))
            return "❌ vaultPath must not be empty.";
        var modeNorm = (mode ?? "merge").Trim().ToLowerInvariant();
        if (modeNorm is not ("merge" or "overlay" or "replace"))
            return "❌ mode must be one of: merge, overlay, replace.";

        var resolved = PathConfig.Default.ResolveUserPath(vaultPath)!;
        var layout = VaultLayout.Resolve(flavor);

        VaultBackend source;
        try { source = new VaultBackend(resolved, watch: false, layout: layout); }
        catch (Exception ex) { return $"❌ Could not open vault '{resolved}': {ex.Message}"; }

        try
        {
            var incoming = source.LoadAll();
            if (incoming.Count == 0)
                return $"No managed memories found at '{source.Location}'.";

            return _store.WithFreshState(memories =>
            {
                if (modeNorm == "replace")
                {
                    int prior = memories.Count;
                    _store.ReplaceAll(incoming);
                    return $"✅ Replaced {prior} current memories with {incoming.Count} from vault.";
                }

                int added = 0, replaced = 0, kept = 0;
                var byId = memories.ToDictionary(m => m.Id, StringComparer.Ordinal);
                foreach (var inc in incoming)
                {
                    if (byId.ContainsKey(inc.Id))
                    {
                        if (modeNorm == "overlay")
                        {
                            int idx = memories.FindIndex(m => m.Id == inc.Id);
                            memories[idx] = inc;
                            _store.Upsert(inc);
                            replaced++;
                        }
                        else
                        {
                            kept++;
                        }
                    }
                    else
                    {
                        memories.Add(inc);
                        _store.Upsert(inc);
                        added++;
                    }
                }
                return $"✅ Import complete: {added} added, {replaced} replaced, {kept} kept (existing).";
            });
        }
        finally { source.Dispose(); }
    }

    [McpServerTool(Name = "koshi_memory_sync_vault"), Description(
        "Force a re-scan of the vault backend so external edits and git pulls are picked up. " +
        "No-op when the backend is not a vault.")]
    public static string SyncVault()
    {
        if (_store.Backend.BackendKind != "vault")
            return $"Backend '{_store.Backend.BackendKind}' is not a vault — no sync needed.";

        _store.ForceReload();
        var count = _store.WithFreshState(memories => memories.Count);
        return $"✅ Reloaded vault. {count} memories now in cache.";
    }

    [McpServerTool(Name = "koshi_capture_turn"), Description(
        "Capture decisions made in the current conversation turn into memory. " +
        "Pass a 1-3 paragraph summary of the turn and any linked PR/commits. " +
        "The server applies lightweight pattern heuristics (no LLM) to extract " +
        "decision-shape sentences and persists each as a Decision memory with " +
        "provenance. Set auto_promote=false to preview candidates without saving. " +
        "Recommended: call this once at the end of every meaningful turn — see " +
        "docs/copilot-instructions-snippet.md for a snippet you can paste into " +
        "your repo's .github/copilot-instructions.md so the agent calls it reliably.")]
    public static string CaptureTurn(
        [Description("Plain-text summary of the turn (1-3 paragraphs). Decision-shape sentences will be extracted.")]
        string turn_summary,
        [Description("Optional linked PR number (e.g., 1234) to record as provenance.")]
        int linked_pr = 0,
        [Description("Optional comma-separated linked commit SHAs/refs to record as provenance.")]
        string? linked_commits = null,
        [Description("If true (default), persist extracted decisions immediately. If false, return candidates without saving.")]
        bool auto_promote = true,
        [Description("Maximum candidates to extract (default 5).")]
        int max_candidates = 5,
        [Description("Confidence floor for accepting candidates (0-1, default 0.5).")]
        float min_confidence = 0.5f,
        [Description("Optional user identifier. Empty or '*' means globally visible.")]
        string? userId = null,
        [Description("Optional workspace identifier. Defaults to 'default'.")]
        string? workspaceId = null,
        [Description("Optional thread identifier for conversation-scoped memories.")]
        string? threadId = null)
    {
        if (string.IsNullOrWhiteSpace(turn_summary))
            return "❌ turn_summary must not be empty.";

        min_confidence = Math.Clamp(min_confidence, 0f, 1f);
        max_candidates = Math.Clamp(max_candidates, 1, 20);

        var allCandidates = DecisionExtractor.Extract(turn_summary, max_candidates);
        var candidates = allCandidates.Where(c => c.Confidence >= min_confidence).ToList();

        if (candidates.Count == 0)
        {
            return "ℹ No decision-shape sentences detected in the turn summary " +
                   $"(extractor found {allCandidates.Count} weak match(es), all below confidence floor {min_confidence:F2}). " +
                   "Either no decisions were made, or rephrase explicitly — e.g., " +
                   "\"Decision: ...\", \"We chose X over Y because Z\", \"Fixed by ...\".";
        }

        var scope = new MemoryScope(
            UserId: string.IsNullOrWhiteSpace(userId) ? "*" : userId.Trim(),
            WorkspaceId: string.IsNullOrWhiteSpace(workspaceId) ? "default" : workspaceId.Trim(),
            ThreadId: string.IsNullOrWhiteSpace(threadId) ? null : threadId.Trim());

        var provenance = BuildProvenance(linked_pr, linked_commits);

        if (!auto_promote)
        {
            var preview = new System.Text.StringBuilder();
            preview.AppendLine($"📋 {candidates.Count} candidate decision(s) extracted (auto_promote=false, none saved):\n");
            int i = 1;
            foreach (var cand in candidates)
            {
                preview.AppendLine($"  [{i++}] (conf {cand.Confidence:F2}, {cand.MatchedPattern})");
                preview.AppendLine($"      Subject: {cand.Subject}");
                preview.AppendLine($"      Body:    {Truncate(cand.Body, 160)}");
                preview.AppendLine();
            }
            preview.AppendLine("To persist these, re-call with auto_promote=true (default) or call koshi_remember manually.");
            return preview.ToString();
        }

        return _store.WithFreshState(memories =>
        {
            var saved = new List<(string Id, string Subject)>();
            var skipped = new List<(string Subject, string Reason)>();

            foreach (var cand in candidates)
            {
                if (memories.Count >= MaxMemories)
                {
                    skipped.Add((cand.Subject, "memory limit reached"));
                    break;
                }

                var dupe = memories.FirstOrDefault(m =>
                    m.Type == MemoryType.Decision &&
                    string.Equals(m.Subject, cand.Subject, StringComparison.OrdinalIgnoreCase) &&
                    m.Scope.WorkspaceId == scope.WorkspaceId);
                if (dupe is not null)
                {
                    skipped.Add((cand.Subject, $"duplicate of {dupe.Id}"));
                    continue;
                }

                var body = provenance is null
                    ? cand.Body
                    : $"{cand.Body}\n\n---\n{provenance}";

                var record = new MemoryRecord
                {
                    Id = _store.AllocateId(),
                    Type = MemoryType.Decision,
                    Content = body,
                    Subject = cand.Subject,
                    Scope = scope,
                    Source = "koshi_capture_turn",
                    Confidence = cand.Confidence,
                };

                memories.Add(record);
                _store.Upsert(record);
                saved.Add((record.Id, cand.Subject));
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"✅ Captured {saved.Count} decision(s) from turn summary.");
            foreach (var (id, subject) in saved)
                sb.AppendLine($"   • {id}: {subject}");
            if (skipped.Count > 0)
            {
                sb.AppendLine($"\n⏭ Skipped {skipped.Count}:");
                foreach (var (subject, reason) in skipped)
                    sb.AppendLine($"   • {subject} — {reason}");
            }
            if (!_store.Backend.IsEnabled)
                sb.AppendLine("\n⚠ Persistence is disabled — captures are in-process only. Set KOSHI_MEMORY_FILE or KOSHI_MEMORY_VAULT to persist.");
            return sb.ToString();
        });
    }

    private static string? BuildProvenance(int linkedPr, string? linkedCommits)
    {
        var parts = new List<string>();
        if (linkedPr > 0) parts.Add($"PR #{linkedPr}");
        if (!string.IsNullOrWhiteSpace(linkedCommits))
        {
            var commits = linkedCommits
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (commits.Length > 0) parts.Add($"commits {string.Join(", ", commits)}");
        }
        if (parts.Count == 0) return null;
        return $"provenance: {string.Join(", ", parts)}\ncaptured: {DateTimeOffset.UtcNow:O}";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    internal static MemoryStatus GetStatus()
    {
        return _store.WithFreshState(memories =>
        {
            // Cache Backend once so a hypothetical future swap (lazy init,
            // reconnect) can't make VaultWatcherStatus and VaultFlavor
            // disagree about whether the backend is a vault.
            var backend = _store.Backend;
            var vault = backend as VaultBackend;
            return new MemoryStatus(
                Count: memories.Count,
                PersistenceEnabled: backend.IsEnabled,
                Path: backend.Location,
                BackendKind: backend.BackendKind,
                UnmanagedNoteCount: backend.UnmanagedNoteCount,
                DuplicateIdWarningCount: backend.DuplicateIdWarningCount,
                VaultWatcherStatus: vault?.WatcherStatus,
                VaultFlavor: vault?.Layout.FlavorName);
        });
    }
}

internal sealed record MemoryStatus(
    int Count,
    bool PersistenceEnabled,
    string? Path,
    string BackendKind,
    int UnmanagedNoteCount,
    int DuplicateIdWarningCount,
    string? VaultWatcherStatus,
    string? VaultFlavor);
