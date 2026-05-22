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

    private static readonly IndexPersistence _persistence;

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
        _persistence = new IndexPersistence(PathConfig.Default.IndexFile);
    }

    [McpServerTool(Name = "koshi_index"), Description(
        "Index a list of in-memory documents for BM25 retrieval. " +
        "Replaces any previously indexed corpus of the same name. " +
        "Pass a non-default corpus name to keep multiple indexes alive simultaneously (issue #23). " +
        "For indexing files on disk, use koshi_index_directory instead.")]
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
        "Index supported text files from a directory recursively for BM25 retrieval. " +
        "If no path is provided, falls back to the KOSHI_INDEX_PATH environment variable. " +
        "Excludes secrets (.env*, *.pem, *.key, *.pfx, secrets.*), build output (bin/obj/dist/node_modules/.git), " +
        "and files larger than the configured limit. " +
        "When KOSHI_INDEX_FILE is set, the resulting BM25 corpus is also persisted to disk so the next " +
        "server start can serve searches without re-indexing. " +
        "This replaces any previously indexed corpus.")]
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
            catch
            {
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
        "Search the indexed corpus using BM25 keyword retrieval. " +
        "Returns the most relevant chunks for the query. " +
        "If no corpus is indexed and KOSHI_INDEX_FILE points to a valid snapshot, it is loaded automatically. " +
        "Otherwise, if KOSHI_INDEX_PATH is set, the path will be auto-indexed once on first use. " +
        "Pass corpus='<name>' to search a non-default named corpus (issue #23).")]
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
        "List indexed corpora. Pass corpus=null (default) to see all corpora; pass a name to see chunk-level detail for a single corpus.")]
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
        "Clear an indexed corpus. Useful when switching between projects without restarting the server. " +
        "Defaults to the 'default' corpus (which also removes the persisted snapshot file when KOSHI_INDEX_FILE is set). " +
        "Pass corpus='*' to clear every corpus (default and named). " +
        "Pass corpus='<name>' to clear a single named corpus.")]
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

    internal static (int chunkCount, int sourceCount, string? path, bool indexed, bool persistenceEnabled, string? persistencePath, bool loadedFromSnapshot) GetStatus()
    {
        EnsureCorpusLoaded();
        lock (_lock)
        {
            var sourceCount = _indexedChunks.Count == 0
                ? 0
                : _indexedChunks.GroupBy(c => c.Metadata.Source).Count();
            return (_indexedChunks.Count, sourceCount, _indexedFromPath, _isIndexed,
                    _persistence.IsEnabled, _persistence.Path, _loadedFromSnapshot);
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
    private static void EnsureCorpusLoaded()
    {
        lock (_lock)
        {
            if (_isIndexed || _snapshotLoadAttempted || !_persistence.IsEnabled) return;
            _snapshotLoadAttempted = true;
        }

        var envelope = _persistence.LoadOrNull();
        if (envelope is null || envelope.Chunks.Count == 0) return;

        // Validate fingerprint when the snapshot has an on-disk source.
        if (envelope.SourcePath is not null
            && envelope.SourcePath != ContentFingerprint.InMemorySource
            && envelope.ContentFingerprint is not null)
        {
            // Cross-check against KOSHI_INDEX_PATH when explicitly set — if the
            // user pointed the server at a different directory than the
            // snapshot came from, we must not silently serve stale results.
            // We only check when the env var was explicitly set, to avoid
            // false positives from the v0.6.0 project-root default.
            if (PathConfig.Default.IndexPathFromEnv)
            {
                var resolved = PathConfig.Default.IndexPath;
                if (!string.Equals(resolved, envelope.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(
                        $"[koshi] Discarding index snapshot: source path '{envelope.SourcePath}' " +
                        $"differs from KOSHI_INDEX_PATH '{resolved}'.");
                    return;
                }
            }
            else
            {
                // Containment safety: when KOSHI_INDEX_PATH is unset (using
                // the v0.6.0 defaults), we only accept snapshots whose source
                // is under the project root. Prevents a stray .koshi/
                // copied between projects from silently serving results
                // sourced from a totally different directory.
                var root = PathConfig.Default.ProjectRoot;
                var srcFull = Path.GetFullPath(envelope.SourcePath);
                if (!srcFull.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(srcFull, root, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(
                        $"[koshi] Discarding index snapshot: source path '{srcFull}' " +
                        $"is outside project root '{root}'. Set KOSHI_INDEX_PATH explicitly to override.");
                    return;
                }
            }

            var current = ContentFingerprint.Compute(envelope.SourcePath, envelope.Enumeration);
            if (current is null || !string.Equals(current, envelope.ContentFingerprint, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"[koshi] Discarding index snapshot for '{envelope.SourcePath}': " +
                    $"content fingerprint changed since {envelope.SavedAt:u}.");
                return;
            }
        }

        var retriever = new KeywordRetriever();
        retriever.Index(envelope.Chunks);

        lock (_lock)
        {
            _indexedChunks = envelope.Chunks;
            _keywordRetriever = retriever;
            _indexedFromPath = envelope.SourcePath;
            _indexedEnumeration = envelope.Enumeration;
            _isIndexed = true;
            _loadedFromSnapshot = true;
            // Snapshot satisfied the indexed-state requirement; clear any
            // prior auto-index failure cache so the throttle starts fresh
            // if a future koshi_clear_index + retry is needed (#31).
            _lastAutoIndexFailureMessage = null;
        }

        Console.Error.WriteLine(
            $"[koshi] Loaded index snapshot: {envelope.Chunks.Count} chunks from " +
            $"'{envelope.SourcePath ?? "(unknown)"}' (saved {envelope.SavedAt:u}).");
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
