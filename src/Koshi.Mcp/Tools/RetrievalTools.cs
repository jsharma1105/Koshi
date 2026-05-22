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

    private static readonly Lock _lock = new();

    // Token counter: was a per-class Lazy<TokenCounter> (cl100k_base ~20 MB).
    // Issue #29 — moved to Koshi.Core.Tokenization.TokenCounters.Shared so
    // RetrievalTools + ContextTools share a single instance and a single env
    // var (KOSHI_TOKENIZER_MODEL) selects the encoding for both.

    private static readonly IndexPersistence _persistence;

    private static KeywordRetriever? _keywordRetriever;
    private static List<Chunk> _indexedChunks = [];
    private static string? _indexedFromPath;
    private static IndexEnumerationParams? _indexedEnumeration;
    private static bool _isIndexed;
    private static bool _snapshotLoadAttempted;
    private static bool _loadedFromSnapshot;

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
        "Replaces any previously indexed corpus. " +
        "For indexing files on disk, use koshi_index_directory instead.")]
    public static string Index(
        [Description("JSON array of documents: [{\"content\": \"...\", \"source\": \"filename.md\", \"type\": \"documentation\"}]")]
        string documents,
        [Description("Optional chunker max tokens per chunk (default 512, range 64-2048). Falls back to KOSHI_CHUNK_MAX_TOKENS env var.")]
        int? maxTokens = null,
        [Description("Optional chunker overlap between chunks (default 50, range 0-256, must be < maxTokens/2). Falls back to KOSHI_CHUNK_OVERLAP_TOKENS env var.")]
        int? overlapTokens = null)
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

        ReplaceIndex(allChunks, source: ContentFingerprint.InMemorySource, enumeration: null);

        var msg = $"✅ Indexed {docs.Count} documents → {allChunks.Count} chunks ({allChunks.Sum(c => c.TokenCount)} tokens) — {cfg.Describe()}";
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
        int? overlapTokens = null)
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

        ReplaceIndex(allChunks, source: dirPath, enumeration: enumeration);

        var msg = $"✅ Indexed {fileList.Count - skipped} files from '{dirPath}' → {allChunks.Count} chunks ({allChunks.Sum(c => c.TokenCount)} tokens) — {chunkerCfg.Describe()}";
        if (chunkerCfg.Warning is not null) msg += $"\n   ⚠ {chunkerCfg.Warning}";
        if (skipped > 0) msg += $" ({skipped} skipped)";
        if (_persistence.IsEnabled) msg += $"\n   Snapshot saved → {_persistence.Path}";
        return msg;
    }

    [McpServerTool(Name = "koshi_search"), Description(
        "Search the indexed corpus using BM25 keyword retrieval. " +
        "Returns the most relevant chunks for the query. " +
        "If no corpus is indexed and KOSHI_INDEX_FILE points to a valid snapshot, it is loaded automatically. " +
        "Otherwise, if KOSHI_INDEX_PATH is set, the path will be auto-indexed once on first use.")]
    public static string Search(
        [Description("The search query")] string query,
        [Description("Number of results to return (1-50, default: 5)")] int topK = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "❌ Query must not be empty.";

        topK = Math.Clamp(topK < 1 ? 5 : topK, 1, MaxTopK);

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

        if (results.Count == 0)
            return $"No results found for: \"{query}\"";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Found {results.Count} results for: \"{query}\"\n");

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
        "List all indexed documents grouped by source, with chunk counts and token totals.")]
    public static string ListIndexed()
    {
        EnsureCorpusLoaded();

        lock (_lock)
        {
            if (!_isIndexed || _indexedChunks.Count == 0)
                return "No documents indexed yet. Call koshi_index_directory or koshi_index first.";

            var bySource = _indexedChunks
                .GroupBy(c => c.Metadata.Source)
                .OrderBy(g => g.Key)
                .ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Indexed corpus: {_indexedChunks.Count} chunks from {bySource.Count} sources");
            if (_indexedFromPath is not null)
                sb.AppendLine($"Source: {_indexedFromPath}{(_loadedFromSnapshot ? " (loaded from snapshot)" : "")}");
            sb.AppendLine();

            foreach (var group in bySource)
            {
                sb.AppendLine($"  • {group.Key} ({group.Count()} chunks, {group.Sum(c => c.TokenCount)} tokens)");
            }

            return sb.ToString();
        }
    }

    [McpServerTool(Name = "koshi_clear_index"), Description(
        "Clear the indexed corpus. Useful when switching between projects without restarting the server. " +
        "Also removes the persisted snapshot file when KOSHI_INDEX_FILE is set.")]
    public static string ClearIndex()
    {
        int previousCount;
        bool deletedSnapshot;
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

        deletedSnapshot = _persistence.IsEnabled && File.Exists(_persistence.Path!);
        _persistence.Delete();

        if (previousCount == 0 && !deletedSnapshot)
            return "Index already empty.";

        var msg = previousCount > 0
            ? $"✅ Cleared index ({previousCount} chunks removed)."
            : "✅ Cleared index (in-memory was already empty).";
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
