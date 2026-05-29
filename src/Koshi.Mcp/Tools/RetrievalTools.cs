using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Koshi.Core.Ingestion;
using Koshi.Core.Models;
using Koshi.Core.Retrieval;
using Koshi.Core.Tokenization;
using Koshi.Mcp.Internal;
using ModelContextProtocol.Server;

namespace Koshi.Mcp.Tools;

/// <summary>
/// MCP tools for retrieval — chunk and search documents using BM25 keyword retrieval.
/// </summary>
[McpServerToolType]
public sealed class RetrievalTools
{
    private const int MaxChunks = 50_000;
    private const int MaxTopK = 50;
    private const int DefaultMaxFileSizeKb = 256;
    private const int DefaultMaxFiles = 5_000;
    private const int DefaultPreviewChars = 500;

    /// <summary>
    /// Name of the default corpus. All retrieval tools target this corpus
    /// when the caller omits the <c>corpus</c> argument, preserving the
    /// single-corpus behavior of v0.5.x / v0.6.0.
    /// </summary>
    internal const string DefaultCorpusName = "default";

    private static readonly Lock _lock = new();

    // Token counter: was a per-class Lazy<TokenCounter> (cl100k_base ~20 MB).
    // Issue #29 — moved to Koshi.Core.Tokenization.TokenCounters.Shared so
    // RetrievalTools + ContextTools share a single instance and a single env
    // var (KOSHI_TOKENIZER_MODEL) selects the encoding for both.

    private static readonly IndexPersistence _defaultPersistence;
    // Mutable so tests can point it at a sandbox file without changing the
    // global process environment. Production code never reassigns this — only
    // the InternalsVisibleTo'd test reset hook does.
    private static IndexPersistence _persistence;

    // Default-corpus state. The default corpus is the only corpus that is
    // wired into snapshot persistence + auto-indexing — additional named
    // corpora (issue #23) are session-only and live in _extraCorpora below.
    private static KeywordRetriever? _keywordRetriever;
    private static List<Chunk> _indexedChunks = [];
    private static string? _indexedFromPath;
    private static IndexEnumerationParams? _indexedEnumeration;
    private static bool _isIndexed;
    private static bool _snapshotLoadAttempted;
    private static bool _loadedFromSnapshot;
    // Why the last snapshot load attempt was discarded (if any). Surfaced via
    // GetStatus → koshi_health so users can see WHY they're stuck on
    // "No documents indexed" instead of a silent stderr log they may have
    // never seen. Null when there has been no discard.
    private static string? _snapshotDiscardReason;
    // Non-fatal note about a loaded snapshot — e.g. "loaded from a snapshot
    // file whose source is outside the project root". Surfaced in health
    // output so the user can sanity-check that they're searching what they
    // expect to be searching.
    private static string? _snapshotLoadWarning;

    // Named-corpus registry (issue #23). The default corpus is intentionally
    // NOT a key here — it lives in the legacy single-corpus fields above so
    // that all existing snapshot-persistence / auto-index logic continues to
    // work unchanged. Named corpora are in-memory only.
    private static readonly ConcurrentDictionary<string, NamedCorpus> _extraCorpora =
        new(StringComparer.OrdinalIgnoreCase);

    internal sealed record NamedCorpus(
        KeywordRetriever Retriever,
        List<Chunk> Chunks,
        string? SourcePath,
        DateTimeOffset IndexedAt);

    private static bool IsDefaultCorpus(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Equals(DefaultCorpusName, StringComparison.OrdinalIgnoreCase);

    // Auto-index retry state (#31). Until v0.5.0 we tracked a one-shot
    // `_autoIndexAttempted` bool, which permanently blocked retries after a
    // single failure — even if the user fixed the env var or the directory
    // appeared later. We now throttle re-attempts on a per-path basis: a
    // failed auto-index returns the same error for AutoIndexRetryAfter, then
    // the next search re-runs the attempt.
    private static DateTimeOffset _lastAutoIndexAttempt = DateTimeOffset.MinValue;
    private static string? _lastAutoIndexFailureMessage;
    private static readonly TimeSpan AutoIndexRetryAfter = TimeSpan.FromSeconds(30);

    static RetrievalTools()
    {
        _defaultPersistence = new IndexPersistence(PathConfig.Default.IndexFile);
        _persistence = _defaultPersistence;
    }

    /// <summary>
    /// Test-only hook: redirect persistence at a sandbox file and wipe all
    /// in-memory corpus state. Not for production use. Pass null to revert
    /// to the process-default persistence resolved from <c>PathConfig</c>.
    /// </summary>
    internal static void ResetForTests(string? sandboxIndexFile)
    {
        lock (_lock)
        {
            _persistence = sandboxIndexFile is null
                ? _defaultPersistence
                : new IndexPersistence(sandboxIndexFile);
            _indexedChunks = [];
            _keywordRetriever = null;
            _indexedFromPath = null;
            _indexedEnumeration = null;
            _isIndexed = false;
            _snapshotLoadAttempted = false;
            _loadedFromSnapshot = false;
            _snapshotDiscardReason = null;
            _snapshotLoadWarning = null;
            _lastAutoIndexAttempt = DateTimeOffset.MinValue;
            _lastAutoIndexFailureMessage = null;
        }
    }

    [McpServerTool(Name = "koshi_index"), Description(
        "WHEN TO CALL: When you have in-memory documents (parsed JSON, fetched URLs, generated text) " +
        "to make searchable. For files already on disk, use koshi_index_directory instead.\n" +
        "WHAT IT DOES: Chunks and BM25-indexes the documents under a corpus name. Replaces any prior " +
        "corpus of the same name. Only the default corpus is persisted via KOSHI_INDEX_FILE.\n" +
        "WHAT YOU GIVE IT: documents (JSON array of {content, source, type}); optional maxTokens / " +
        "overlapTokens / corpus name.")]
    public static string Index(
        [Description("JSON array of documents: [{\"content\": \"...\", \"source\": \"filename.md\", \"type\": \"documentation\"}]")]
        string documents,
        [Description("Optional chunker max tokens per chunk (default 512, range 64-2048). Falls back to KOSHI_CHUNK_MAX_TOKENS env var.")]
        int? maxTokens = null,
        [Description("Optional chunker overlap between chunks (default 50, range 0-256, must be < maxTokens/2). Falls back to KOSHI_CHUNK_OVERLAP_TOKENS env var.")]
        int? overlapTokens = null,
        [Description("Optional named corpus to write into. Defaults to 'default'. Named corpora are in-memory only — only the default corpus is persisted via KOSHI_INDEX_FILE.")]
        string? corpus = null)
    {
        List<DocInput>? docs;
        try
        {
            docs = JsonSerializer.Deserialize(documents, KoshiJsonContext.Default.ListDocInput);
        }
        catch (JsonException ex)
        {
            return $"❌ Invalid JSON: {ex.Message}";
        }

        if (docs is null || docs.Count == 0)
            return "❌ No documents provided.";

        var cfg = ChunkerConfig.Resolve(maxTokens, overlapTokens);
        var chunker = new FixedSizeChunker(TokenCounters.Shared, cfg.MaxTokens, cfg.OverlapTokens);
        var allChunks = new List<Chunk>();

        foreach (var doc in docs)
        {
            var chunks = chunker.Chunk(doc.Content, doc.Source ?? "unknown", doc.Type ?? "document");
            allChunks.AddRange(chunks);
        }

        if (allChunks.Count > MaxChunks)
            return $"❌ Too many chunks ({allChunks.Count} > {MaxChunks}). Reduce document count or size.";

        var corpusName = IsDefaultCorpus(corpus) ? DefaultCorpusName : corpus!.Trim();
        if (IsDefaultCorpus(corpusName))
        {
            ReplaceIndex(allChunks, source: ContentFingerprint.InMemorySource, enumeration: null);
        }
        else
        {
            ReplaceNamedCorpus(corpusName, allChunks, source: ContentFingerprint.InMemorySource);
        }

        var msg = $"✅ Indexed {docs.Count} documents → {allChunks.Count} chunks ({allChunks.Sum(c => c.TokenCount)} tokens) — {cfg.Describe()} [corpus={corpusName}]";
        if (cfg.Warning is not null) msg += $"\n   ⚠ {cfg.Warning}";
        return msg;
    }

    [McpServerTool(Name = "koshi_index_directory"), Description(
        "WHEN TO CALL: When the user asks to make a project/folder searchable, or once at session start " +
        "if KOSHI_INDEX_PATH is set and nothing is yet indexed. Required before koshi_search returns " +
        "useful results on disk content.\n" +
        "WHAT IT DOES: Recursively indexes supported text files under path (or $KOSHI_INDEX_PATH). " +
        "Excludes secrets (.env*, *.pem, *.key, secrets.*), build outputs (bin/obj/dist/node_modules/.git), " +
        "and files over the size limit. Persists to KOSHI_INDEX_FILE if set. Replaces the corpus.\n" +
        "WHAT YOU GIVE IT: path (optional — defaults to $KOSHI_INDEX_PATH); pattern (optional glob); " +
        "maxFileSizeKb (default 256); maxFiles (default 5000); optional chunker tuning; optional corpus.")]
    public static string IndexDirectory(
        [Description("Absolute directory path to index (defaults to $KOSHI_INDEX_PATH)")]
        string? path = null,
        [Description("Glob pattern to filter files (e.g. '*.md'). If empty, all supported text file types are indexed.")]
        string? pattern = null,
        [Description("Maximum file size in KB to index (default: 256)")] int maxFileSizeKb = DefaultMaxFileSizeKb,
        [Description("Maximum number of files to index (default: 5000)")] int maxFiles = DefaultMaxFiles,
        [Description("Optional chunker max tokens per chunk (default 512, range 64-2048). Falls back to KOSHI_CHUNK_MAX_TOKENS env var. Recommended: 1024 for JSON-heavy directories, 512 for code/prose, 256 for short docs.")]
        int? maxTokens = null,
        [Description("Optional chunker overlap between chunks (default 50, range 0-256, must be < maxTokens/2). Falls back to KOSHI_CHUNK_OVERLAP_TOKENS env var.")]
        int? overlapTokens = null,
        [Description("Optional named corpus to write into. Defaults to 'default'. Named corpora are session-only — only the default corpus is persisted via KOSHI_INDEX_FILE.")]
        string? corpus = null)
    {
        var dirPath = ResolveIndexPath(path);
        if (dirPath is null)
        {
            return "❌ No path provided and KOSHI_INDEX_PATH is not set. " +
                   "Either pass an absolute path or set the KOSHI_INDEX_PATH environment variable in your MCP client config.";
        }

        if (!Directory.Exists(dirPath))
            return $"❌ Directory not found: {dirPath}";

        if (maxFileSizeKb < 1) maxFileSizeKb = DefaultMaxFileSizeKb;
        if (maxFiles < 1) maxFiles = DefaultMaxFiles;
        long maxBytes = maxFileSizeKb * 1024L;

        var enumeration = new IndexEnumerationParams
        {
            Pattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern,
            MaxFileSizeBytes = maxBytes,
            MaxFiles = maxFiles,
        };

        IEnumerable<string> files;
        try
        {
            files = SafeFileEnumerator.EnumerateIndexableFiles(dirPath, enumeration.Pattern, maxBytes, maxFiles);
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"❌ Access denied to directory: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"❌ Failed to enumerate directory: {ex.Message}";
        }

        var fileList = files.ToList();
        if (fileList.Count == 0)
            return $"❌ No supported, readable files found in: {dirPath}";

        var chunkerCfg = ChunkerConfig.Resolve(maxTokens, overlapTokens);
        var chunker = new FixedSizeChunker(TokenCounters.Shared, chunkerCfg.MaxTokens, chunkerCfg.OverlapTokens);
        var allChunks = new List<Chunk>(capacity: fileList.Count * 4);
        int skipped = 0;

        foreach (var file in fileList)
        {
            try
            {
                var content = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(content)) { skipped++; continue; }

                var relativePath = Path.GetRelativePath(dirPath, file);
                var ext = Path.GetExtension(file).TrimStart('.');
                var chunks = chunker.Chunk(content, relativePath, string.IsNullOrEmpty(ext) ? "document" : ext);
                allChunks.AddRange(chunks);

                if (allChunks.Count > MaxChunks)
                    return $"❌ Too many chunks ({allChunks.Count} > {MaxChunks}). Use a pattern filter to reduce scope.";
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                    or NotSupportedException or PathTooLongException)
            {
                _ = ex;
                skipped++;
            }
        }

        if (allChunks.Count == 0)
            return "❌ All files were empty or unreadable.";

        var corpusName = IsDefaultCorpus(corpus) ? DefaultCorpusName : corpus!.Trim();
        if (IsDefaultCorpus(corpusName))
        {
            ReplaceIndex(allChunks, source: dirPath, enumeration: enumeration);
        }
        else
        {
            ReplaceNamedCorpus(corpusName, allChunks, source: dirPath);
        }

        var msg = $"✅ Indexed {fileList.Count - skipped} files from '{dirPath}' → {allChunks.Count} chunks ({allChunks.Sum(c => c.TokenCount)} tokens) — {chunkerCfg.Describe()} [corpus={corpusName}]";
        if (chunkerCfg.Warning is not null) msg += $"\n   ⚠ {chunkerCfg.Warning}";
        if (skipped > 0) msg += $" ({skipped} skipped)";
        if (IsDefaultCorpus(corpusName) && _persistence.IsEnabled) msg += $"\n   Snapshot saved → {_persistence.Path}";
        if (!IsDefaultCorpus(corpusName)) msg += "\n   (named corpus — not persisted to disk)";
        return msg;
    }

    [McpServerTool(Name = "koshi_search"), Description(
        "WHEN TO CALL: BEFORE answering any code or documentation question about the indexed project. " +
        "Cheap — always preferable to guessing or asking the user to paste files. Call early in the " +
        "turn, ideally in parallel with koshi_recall.\n" +
        "WHAT IT DOES: BM25 keyword search over the indexed corpus. Auto-loads from KOSHI_INDEX_FILE " +
        "if a snapshot exists; auto-indexes KOSHI_INDEX_PATH once on first use if neither is loaded.\n" +
        "WHAT YOU GIVE IT: query (required); topK (1-50, default 5); corpus (optional — see " +
        "koshi_list_indexed for names).")]
    public static string Search(
        [Description("The search query")] string query,
        [Description("Number of results to return (1-50, default: 5)")] int topK = 5,
        [Description("Optional named corpus to search. Defaults to 'default'. Use koshi_list_indexed() to see available corpora.")]
        string? corpus = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "❌ Query must not be empty.";

        topK = Math.Clamp(topK < 1 ? 5 : topK, 1, MaxTopK);

        var corpusName = IsDefaultCorpus(corpus) ? DefaultCorpusName : corpus!.Trim();

        // Named-corpus path bypasses auto-index / snapshot loading entirely —
        // those features are scoped to the default corpus only (issue #23).
        if (!IsDefaultCorpus(corpusName))
        {
            if (!_extraCorpora.TryGetValue(corpusName, out var named))
                return $"❌ Unknown corpus '{corpusName}'. Call koshi_list_indexed() to see available corpora.";

            var namedResults = named.Retriever
                .SearchAsync(query, new RetrievalOptions(TopK: topK))
                .GetAwaiter().GetResult();
            return FormatSearchResults(query, namedResults, corpusName);
        }

        EnsureCorpusLoaded();

        if (!_isIndexed)
        {
            // Auto-index from KOSHI_INDEX_PATH remains opt-in even after the
            // v0.6.0 project-root defaults: a user-supplied env var (or
            // explicit koshi_index_directory call) is the only signal that
            // says "yes, please scan files automatically". Defaulting the
            // PATH to the project root would otherwise risk a costly
            // surprise scan on the first search call.
            if (!PathConfig.Default.IndexPathFromEnv)
            {
                return "❌ No documents indexed. Call koshi_index_directory(path) first, " +
                       "or set the KOSHI_INDEX_PATH / KOSHI_INDEX_FILE environment variable in your MCP client config.";
            }

            var envPath = PathConfig.Default.IndexPath;

            // Throttle: if the most recent attempt failed and the retry window
            // hasn't elapsed, surface the cached failure WITHOUT re-running
            // the (potentially slow) directory enumeration. Outside the
            // window, retry — this is the #31 fix vs. the old one-shot block.
            var now = DateTimeOffset.UtcNow;
            var sinceLastAttempt = now - _lastAutoIndexAttempt;
            if (_lastAutoIndexFailureMessage is not null && sinceLastAttempt < AutoIndexRetryAfter)
            {
                var retryIn = AutoIndexRetryAfter - sinceLastAttempt;
                return _lastAutoIndexFailureMessage +
                       $"\n(next auto-retry in {Math.Max(1, (int)Math.Ceiling(retryIn.TotalSeconds))}s; " +
                       $"call koshi_index_directory(\"{envPath}\") to retry immediately)";
            }

            _lastAutoIndexAttempt = now;
            Console.Error.WriteLine($"[koshi] auto-indexing from KOSHI_INDEX_PATH='{envPath}'");
            var auto = IndexDirectory(envPath);
            if (auto.StartsWith('❌'))
            {
                var failureMsg = "❌ No documents indexed. Auto-index from KOSHI_INDEX_PATH failed:\n" + auto;
                _lastAutoIndexFailureMessage = failureMsg;
                return failureMsg +
                       $"\n(next auto-retry in {AutoIndexRetryAfter.TotalSeconds:F0}s; " +
                       $"call koshi_index_directory(\"{envPath}\") to retry immediately)";
            }

            // Success: clear the failure cache so subsequent re-clears + retries start fresh.
            _lastAutoIndexFailureMessage = null;
        }

        KeywordRetriever retriever;
        lock (_lock)
        {
            if (!_isIndexed || _keywordRetriever is null)
                return "❌ No documents indexed. Call koshi_index_directory or koshi_index first.";
            retriever = _keywordRetriever;
        }

        var options = new RetrievalOptions(TopK: topK);
        var results = retriever.SearchAsync(query, options).GetAwaiter().GetResult();

        return FormatSearchResults(query, results, DefaultCorpusName);
    }

    private static string FormatSearchResults(string query, IReadOnlyList<SearchResult> results, string corpusName)
    {
        if (results.Count == 0)
            return $"No results found for: \"{query}\" [corpus={corpusName}]";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {results.Count} results for: \"{query}\" [corpus={corpusName}]\n");

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"─── Result {i + 1} (score: {r.Score:F4}, source: {r.Chunk.Metadata.Source}) ───");
            sb.AppendLine(r.Chunk.Content.Length > DefaultPreviewChars
                ? r.Chunk.Content[..DefaultPreviewChars] + "..."
                : r.Chunk.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "koshi_list_indexed"), Description(
        "WHEN TO CALL: Before searching, to confirm which corpora and files are actually indexed; or " +
        "to verify that an index_directory call landed.\n" +
        "WHAT IT DOES: Lists every indexed corpus with chunk and source counts. Pass a corpus name " +
        "for per-source chunk-level detail.\n" +
        "WHAT YOU GIVE IT: corpus (optional — omit for summary of all corpora; name for detail).")]
    public static string ListIndexed(
        [Description("Optional corpus name. When omitted, lists every corpus (default + named). When provided, shows per-source chunk counts for that corpus only.")]
        string? corpus = null)
    {
        EnsureCorpusLoaded();

        // Single-corpus detailed view.
        if (!string.IsNullOrWhiteSpace(corpus))
        {
            return IsDefaultCorpus(corpus)
                ? DescribeDefaultCorpusDetail()
                : DescribeNamedCorpusDetail(corpus!.Trim());
        }

        // Multi-corpus summary.
        var sb = new System.Text.StringBuilder();
        var corpora = new List<(string name, int chunks, int sources, string? path, bool snapshot)>();

        lock (_lock)
        {
            if (_isIndexed && _indexedChunks.Count > 0)
            {
                var sources = _indexedChunks.Select(c => c.Metadata.Source).Distinct().Count();
                corpora.Add((DefaultCorpusName, _indexedChunks.Count, sources, _indexedFromPath, _loadedFromSnapshot));
            }
        }
        foreach (var kv in _extraCorpora)
        {
            var sources = kv.Value.Chunks.Select(c => c.Metadata.Source).Distinct().Count();
            corpora.Add((kv.Key, kv.Value.Chunks.Count, sources, kv.Value.SourcePath, false));
        }

        if (corpora.Count == 0)
            return "No corpora indexed yet. Call koshi_index_directory or koshi_index first.";

        sb.AppendLine($"{corpora.Count} corpus{(corpora.Count == 1 ? "" : "es")} indexed:");
        foreach (var c in corpora.OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append($"  • {c.name}: {c.chunks} chunks from {c.sources} sources");
            if (c.path is not null) sb.Append($" ({c.path}{(c.snapshot ? ", from snapshot" : "")})");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string DescribeDefaultCorpusDetail()
    {
        lock (_lock)
        {
            if (!_isIndexed || _indexedChunks.Count == 0)
                return "No documents indexed yet in the default corpus. Call koshi_index_directory or koshi_index first.";

            var bySource = _indexedChunks
                .GroupBy(c => c.Metadata.Source)
                .OrderBy(g => g.Key)
                .ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Corpus '{DefaultCorpusName}': {_indexedChunks.Count} chunks from {bySource.Count} sources");
            if (_indexedFromPath is not null)
                sb.AppendLine($"Source: {_indexedFromPath}{(_loadedFromSnapshot ? " (loaded from snapshot)" : "")}");
            sb.AppendLine();

            foreach (var group in bySource)
                sb.AppendLine($"  • {group.Key} ({group.Count()} chunks, {group.Sum(c => c.TokenCount)} tokens)");
            return sb.ToString();
        }
    }

    private static string DescribeNamedCorpusDetail(string corpusName)
    {
        if (!_extraCorpora.TryGetValue(corpusName, out var named))
            return $"Unknown corpus '{corpusName}'. Call koshi_list_indexed() to see available corpora.";

        var bySource = named.Chunks
            .GroupBy(c => c.Metadata.Source)
            .OrderBy(g => g.Key)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Corpus '{corpusName}': {named.Chunks.Count} chunks from {bySource.Count} sources");
        if (named.SourcePath is not null)
            sb.AppendLine($"Source: {named.SourcePath} (indexed at {named.IndexedAt:u})");
        sb.AppendLine();

        foreach (var group in bySource)
            sb.AppendLine($"  • {group.Key} ({group.Count()} chunks, {group.Sum(c => c.TokenCount)} tokens)");
        return sb.ToString();
    }

    [McpServerTool(Name = "koshi_clear_index"), Description(
        "WHEN TO CALL: When the user asks to switch projects, reset retrieval state, or drop a named " +
        "corpus. Default-corpus clear also removes the persisted snapshot.\n" +
        "WHAT IT DOES: Drops the named corpus (default if omitted) from memory. For the default corpus, " +
        "also deletes the KOSHI_INDEX_FILE snapshot if set. Pass corpus='*' to clear everything.\n" +
        "WHAT YOU GIVE IT: corpus (optional — null/'default' for default + snapshot; '*' for all; name " +
        "for a single named corpus).")]
    public static string ClearIndex(
        [Description("Corpus to clear. 'default' (or null) clears the default corpus + snapshot; '*' clears all corpora; any other value clears that named corpus only.")]
        string? corpus = null)
    {
        if (string.Equals(corpus?.Trim(), "*", StringComparison.Ordinal))
        {
            // Clear default + all named.
            var defaultResult = ClearDefaultCorpus();
            var named = _extraCorpora.Keys.ToList();
            foreach (var n in named) _extraCorpora.TryRemove(n, out _);
            var msg = defaultResult;
            if (named.Count > 0) msg += $"\n   Cleared {named.Count} named corpora: {string.Join(", ", named)}";
            return msg;
        }

        if (!IsDefaultCorpus(corpus))
        {
            var name = corpus!.Trim();
            return _extraCorpora.TryRemove(name, out var removed)
                ? $"✅ Cleared named corpus '{name}' ({removed.Chunks.Count} chunks removed)."
                : $"Corpus '{name}' was not indexed. Nothing to clear.";
        }

        return ClearDefaultCorpus();
    }

    private static string ClearDefaultCorpus()
    {
        int previousCount;
        lock (_lock)
        {
            previousCount = _indexedChunks.Count;
            _indexedChunks = [];
            _keywordRetriever = null;
            _indexedFromPath = null;
            _indexedEnumeration = null;
            _isIndexed = false;
            _snapshotLoadAttempted = true; // Don't auto-reload a stale snapshot we just cleared.
            _loadedFromSnapshot = false;
            _snapshotDiscardReason = null;
            _snapshotLoadWarning = null;
            // Reset auto-index retry throttle so the next koshi_search can
            // re-attempt KOSHI_INDEX_PATH immediately (#31).
            _lastAutoIndexAttempt = DateTimeOffset.MinValue;
            _lastAutoIndexFailureMessage = null;
        }

        var deletedSnapshot = _persistence.IsEnabled && File.Exists(_persistence.Path!);
        _persistence.Delete();

        if (previousCount == 0 && !deletedSnapshot)
            return "Default corpus already empty.";

        var msg = previousCount > 0
            ? $"✅ Cleared default corpus ({previousCount} chunks removed)."
            : "✅ Cleared default corpus (in-memory was already empty).";
        if (deletedSnapshot) msg += $"\n   Snapshot deleted → {_persistence.Path}";
        return msg;
    }

    internal static (
        int chunkCount, int sourceCount, string? path, bool indexed,
        bool persistenceEnabled, string? persistencePath, bool loadedFromSnapshot,
        string? snapshotDiscardReason, string? snapshotLoadWarning) GetStatus()
    {
        EnsureCorpusLoaded();
        lock (_lock)
        {
            var sourceCount = _indexedChunks.Count == 0
                ? 0
                : _indexedChunks.GroupBy(c => c.Metadata.Source).Count();
            return (_indexedChunks.Count, sourceCount, _indexedFromPath, _isIndexed,
                    _persistence.IsEnabled, _persistence.Path, _loadedFromSnapshot,
                    _snapshotDiscardReason, _snapshotLoadWarning);
        }
    }

    /// <summary>Per-named-corpus status snapshot for <c>koshi_health</c> (#23).</summary>
    internal static IReadOnlyList<(string name, int chunks, int sources, string? path)> GetNamedCorporaStatus()
    {
        var list = new List<(string, int, int, string?)>(_extraCorpora.Count);
        foreach (var kv in _extraCorpora)
        {
            var sources = kv.Value.Chunks.Select(c => c.Metadata.Source).Distinct().Count();
            list.Add((kv.Key, kv.Value.Chunks.Count, sources, kv.Value.SourcePath));
        }
        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Item1, b.Item1));
        return list;
    }

    /// <summary>
    /// Attempts to populate the in-memory corpus from a persisted snapshot on first access.
    /// No-op when persistence is disabled, no snapshot exists, or the snapshot is stale.
    /// Called from every public entry point that observes index state.
    /// </summary>
    /// <summary>
    /// Attempts to populate the in-memory corpus from a persisted snapshot
    /// on first access. No-op when persistence is disabled, no snapshot
    /// exists, or the snapshot is rejected (stale fingerprint, copied
    /// between projects, or env-explicit path mismatch).
    ///
    /// <para>Holds the master lock for the full duration of the load — this
    /// is one-shot per process and a few seconds of initial blocking is
    /// preferable to the silent "no documents indexed" race that arises if
    /// thread A flips <see cref="_snapshotLoadAttempted"/> before completing
    /// while thread B sees the flag and skips loading.</para>
    /// </summary>
    private static void EnsureCorpusLoaded()
    {
        lock (_lock)
        {
            if (_isIndexed || _snapshotLoadAttempted || !_persistence.IsEnabled) return;

            var envelope = _persistence.LoadOrNull();
            // Mark attempted only after LoadOrNull returns — if it returned
            // because the file simply doesn't exist yet (cold start), a
            // subsequent koshi_index_directory + restart cycle should still
            // see the next load attempt fire. The bool prevents repeated
            // disk reads inside one process, not across processes.
            _snapshotLoadAttempted = true;

            if (envelope is null || envelope.Chunks.Count == 0)
            {
                _snapshotDiscardReason = envelope is null ? null : "snapshot file is empty";
                return;
            }

            if (!IsSnapshotAcceptable(envelope, out var discardReason, out var warning))
            {
                _snapshotDiscardReason = discardReason;
                Console.Error.WriteLine($"[koshi] Discarding index snapshot: {discardReason}");
                return;
            }

            var retriever = new KeywordRetriever();
            retriever.Index(envelope.Chunks);

            _indexedChunks = envelope.Chunks;
            _keywordRetriever = retriever;
            _indexedFromPath = envelope.SourcePath;
            _indexedEnumeration = envelope.Enumeration;
            _isIndexed = true;
            _loadedFromSnapshot = true;
            _snapshotDiscardReason = null;
            _snapshotLoadWarning = warning;
            // Snapshot satisfied the indexed-state requirement; clear any
            // prior auto-index failure cache so the throttle starts fresh
            // if a future koshi_clear_index + retry is needed (#31).
            _lastAutoIndexFailureMessage = null;

            Console.Error.WriteLine(
                $"[koshi] Loaded index snapshot: {envelope.Chunks.Count} chunks from " +
                $"'{envelope.SourcePath ?? "(unknown)"}' (saved {envelope.SavedAt:u}).");
            if (warning is not null)
                Console.Error.WriteLine($"[koshi] {warning}");
        }
    }

    /// <summary>
    /// Decide whether a loaded snapshot should be accepted. Returns true to
    /// load it (optionally with a non-fatal <paramref name="warning"/>) or
    /// false with a populated <paramref name="discardReason"/> to reject it.
    ///
    /// <para>Three rejection paths, in priority order:</para>
    /// <list type="number">
    ///   <item>Env-explicit <c>KOSHI_INDEX_PATH</c> disagrees with the
    ///   snapshot's <c>SourcePath</c> — the user is unambiguously asking
    ///   for a different path, the snapshot is wrong.</item>
    ///   <item>The source-on-disk fingerprint has changed since the
    ///   snapshot was saved — the snapshot is stale, force re-index.</item>
    ///   <item>The snapshot was copied between projects (its recorded
    ///   <c>SnapshotPath</c> differs from where we'd write today AND its
    ///   <c>SourcePath</c> is outside this project's root) — defensively
    ///   discard to avoid silently serving cross-project results. Legacy
    ///   snapshots without <c>SnapshotPath</c> get the same conservative
    ///   treatment so behaviour doesn't change for them.</item>
    /// </list>
    ///
    /// <para>The deliberate fix for issue #62 is that a snapshot whose
    /// <c>SnapshotPath</c> matches our own <c>_persistence.Path</c> is
    /// trusted regardless of containment — that's the user's case
    /// (they indexed <c>C:/work/some-repo</c> from a server with cwd
    /// elsewhere; the snapshot ended up in <c>./.koshi/index.json</c>
    /// pointing at the explicit absolute path they passed). Trust the user.</para>
    /// </summary>
    private static bool IsSnapshotAcceptable(
        IndexEnvelope envelope, out string? discardReason, out string? warning)
    {
        discardReason = null;
        warning = null;

        // In-memory corpora (from koshi_index) have no on-disk source — no
        // containment or fingerprint check applies. Round-trip as-is.
        if (envelope.SourcePath is null || envelope.SourcePath == ContentFingerprint.InMemorySource)
            return true;

        // Path 1: env-explicit KOSHI_INDEX_PATH mismatch (highest priority).
        if (PathConfig.Default.IndexPathFromEnv)
        {
            var resolved = PathConfig.Default.IndexPath;
            if (!string.Equals(resolved, envelope.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                discardReason = $"source path '{envelope.SourcePath}' differs from " +
                                $"KOSHI_INDEX_PATH '{resolved}'";
                return false;
            }
        }

        // Path 2: stale fingerprint.
        if (envelope.ContentFingerprint is not null)
        {
            var current = ContentFingerprint.Compute(envelope.SourcePath, envelope.Enumeration);
            if (current is null || !string.Equals(current, envelope.ContentFingerprint, StringComparison.Ordinal))
            {
                discardReason = $"content fingerprint for '{envelope.SourcePath}' " +
                                $"changed since {envelope.SavedAt:u} — re-run koshi_index_directory";
                return false;
            }
        }

        // Path 3: cross-project copy defense. Only fires when env-var path is
        // unset (when set, path-1 above is authoritative). A snapshot we
        // wrote ourselves has SnapshotPath == _persistence.Path; anything
        // else is either a legacy snapshot or a copy from another project.
        var weWroteThis = envelope.SnapshotPath is not null
            && _persistence.Path is not null
            && string.Equals(
                Path.GetFullPath(envelope.SnapshotPath),
                _persistence.Path,
                StringComparison.OrdinalIgnoreCase);

        if (!PathConfig.Default.IndexPathFromEnv && !weWroteThis)
        {
            var root = PathConfig.Default.ProjectRoot;
            var srcFull = Path.GetFullPath(envelope.SourcePath);
            var underRoot = srcFull.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(srcFull, root, StringComparison.OrdinalIgnoreCase);
            if (!underRoot)
            {
                discardReason = $"snapshot at '{_persistence.Path}' was not written by this " +
                                $"project (recorded SnapshotPath='{envelope.SnapshotPath ?? "(legacy)"}'), " +
                                $"and its source '{srcFull}' is outside project root '{root}'. " +
                                $"If this is intentional, set KOSHI_INDEX_PATH='{srcFull}' to override.";
                return false;
            }
        }

        // Out-of-project but we wrote it ourselves — load with a sanity warning.
        if (weWroteThis)
        {
            var root = PathConfig.Default.ProjectRoot;
            var srcFull = Path.GetFullPath(envelope.SourcePath);
            var underRoot = srcFull.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(srcFull, root, StringComparison.OrdinalIgnoreCase);
            if (!underRoot)
            {
                warning = $"loaded snapshot whose source '{srcFull}' is outside " +
                          $"project root '{root}'. Run koshi_clear_index if unexpected.";
            }
        }

        return true;
    }

    private static void ReplaceIndex(List<Chunk> chunks, string source, IndexEnumerationParams? enumeration)
    {
        var retriever = new KeywordRetriever();
        retriever.Index(chunks);

        var fingerprint = ContentFingerprint.Compute(source, enumeration);

        lock (_lock)
        {
            _indexedChunks = chunks;
            _keywordRetriever = retriever;
            _indexedFromPath = source;
            _indexedEnumeration = enumeration;
            _isIndexed = true;
            _snapshotLoadAttempted = true;
            _loadedFromSnapshot = false;
            _snapshotDiscardReason = null;
            _snapshotLoadWarning = null;
            // ReplaceIndex was called explicitly — clear any prior auto-index
            // failure cache so a future clear + retry isn't blocked by stale
            // throttle state (#31).
            _lastAutoIndexFailureMessage = null;
        }

        _persistence.Save(source, fingerprint, enumeration, chunks);
    }

    /// <summary>
    /// Issue #23: Build/replace a named corpus in <see cref="_extraCorpora"/>.
    /// Named corpora are in-memory only — they do NOT participate in
    /// snapshot persistence or KOSHI_INDEX_PATH auto-indexing (those features
    /// belong to the default corpus).
    /// </summary>
    private static void ReplaceNamedCorpus(string name, List<Chunk> chunks, string? source)
    {
        var retriever = new KeywordRetriever();
        retriever.Index(chunks);
        _extraCorpora[name] = new NamedCorpus(retriever, chunks, source, DateTimeOffset.UtcNow);
    }

    private static string? ResolveIndexPath(string? explicitPath)
    {
        // Explicit caller-supplied path wins. Relative paths resolve against
        // the project root so callers can say
        // koshi_index_directory("src") and have it Just Work in a multi-
        // workspace setup.
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return PathConfig.Default.ResolveUserPath(explicitPath);

        // No explicit path: fall back to PathConfig.IndexPath. When
        // KOSHI_INDEX_PATH is set it wins; otherwise the v0.6.0 default
        // (the project root) is used. Returning null is impossible here —
        // the resolver guarantees a non-null absolute IndexPath.
        return PathConfig.Default.IndexPath;
    }

}
