using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Koshi.Core.Ingestion;
using Koshi.Core.Models;
using Koshi.Core.Retrieval;
using Koshi.Core.Tokenization;
using Koshi.Mcp.Internal;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static Koshi.Mcp.Internal.JsonShapes;

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

    // IndexWatcher for steady-state index freshness (#78 Gap D). Started after
    // ReplaceIndex or snapshot auto-load when KOSHI_INDEX_WATCH is set; torn
    // down by ClearDefaultCorpusCore and ResetForTests. Generation check uses
    // ReferenceEquals(this, _indexWatcher) inside the drain delegate so a
    // late callback from a disposed watcher cannot mutate a newer index.
    private static IndexWatcher? _indexWatcher;
    private static IndexWatchMode _explicitWatchModeOverride; // per-call watch=true on IndexDirectory

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
    // Snapshot-load telemetry surfaced by koshi_health (#70). Captured ONCE
    // at the moment the snapshot is loaded into the corpus; subsequent
    // koshi_index_directory calls do not mutate these — they describe the
    // load-on-startup answer, not the live chunk count.
    private static int _loadedChunkCount;
    private static DateTimeOffset? _loadedAt;

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
    // Dedicated lock for the auto-index throttle state so the "is the retry
    // window still open?" read and the "I'm the thread that's going to retry"
    // write happen atomically. Without it two concurrent Search() calls can
    // both observe a stale failure message, both record themselves as the
    // current attempt, and both re-run the (potentially slow) directory
    // scan. (Sonnet multi-model review S2.)
    private static readonly object _autoIndexLock = new();

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
            _loadedChunkCount = 0;
            _loadedAt = null;
            _lastAutoIndexAttempt = DateTimeOffset.MinValue;
            _lastAutoIndexFailureMessage = null;
            DisposeIndexWatcher();
            _explicitWatchModeOverride = IndexWatchMode.Off;
        }
    }

    [McpServerTool(Name = "koshi_index"), Description(
        "WHEN TO CALL: When you have in-memory documents (parsed JSON, fetched URLs, generated text) " +
        "to make searchable. For files already on disk, use koshi_index_directory instead.\n" +
        "WHAT IT DOES: Chunks and BM25-indexes the documents under a corpus name. Replaces any prior " +
        "corpus of the same name. Only the default corpus is persisted via KOSHI_INDEX_FILE.\n" +
        "WHAT YOU GIVE IT: documents (JSON array of {content, source, type}); optional maxTokens / " +
        "overlapTokens / corpus name. Pass format=\"json\" for a parseable envelope (#66).")]
    public static string Index(
        [Description("JSON array of documents: [{\"content\": \"...\", \"source\": \"filename.md\", \"type\": \"documentation\"}]")]
        string documents,
        [Description("Optional chunker max tokens per chunk (default 512, range 64-2048). Falls back to KOSHI_CHUNK_MAX_TOKENS env var.")]
        int? maxTokens = null,
        [Description("Optional chunker overlap between chunks (default 50, range 0-256, must be < maxTokens/2). Falls back to KOSHI_CHUNK_OVERLAP_TOKENS env var.")]
        int? overlapTokens = null,
        [Description("Optional named corpus to write into. Defaults to 'default'. Named corpora are in-memory only — only the default corpus is persisted via KOSHI_INDEX_FILE.")]
        string? corpus = null,
        [Description("Output mode: 'text' (default, human-readable) or 'json' (stable structured envelope, issue #66).")]
        string? format = null)
    {
        const string ToolName = "koshi_index";
        var fmt = OutputFormatting.Resolve(format, out var fmtErr);
        if (fmtErr is not null)
            return IndexError(fmt, OutputErrorCodes.InvalidFormat, fmtErr);

        List<DocInput>? docs;
        try
        {
            docs = JsonSerializer.Deserialize(documents, KoshiJsonContext.Default.ListDocInput);
        }
        catch (JsonException ex)
        {
            return IndexError(fmt, OutputErrorCodes.InvalidJson, $"Invalid JSON: {ex.Message}");
        }

        if (docs is null || docs.Count == 0)
            return IndexError(fmt, OutputErrorCodes.NoDocuments, "No documents provided.");

        var cfg = ChunkerConfig.Resolve(maxTokens, overlapTokens);
        var chunker = new FixedSizeChunker(TokenCounters.Shared, cfg.MaxTokens, cfg.OverlapTokens);
        var allChunks = new List<Chunk>();

        foreach (var doc in docs)
        {
            var chunks = chunker.Chunk(doc.Content, doc.Source ?? "unknown", doc.Type ?? "document");
            allChunks.AddRange(chunks);
        }

        if (allChunks.Count > MaxChunks)
            return IndexError(fmt, OutputErrorCodes.TooManyChunks,
                $"Too many chunks ({allChunks.Count} > {MaxChunks}). Reduce document count or size.");

        var corpusName = IsDefaultCorpus(corpus) ? DefaultCorpusName : corpus!.Trim();
        var isDefault = IsDefaultCorpus(corpusName);
        if (isDefault)
        {
            ReplaceIndex(allChunks, source: ContentFingerprint.InMemorySource, enumeration: null);
        }
        else
        {
            ReplaceNamedCorpus(corpusName, allChunks, source: ContentFingerprint.InMemorySource);
        }

        if (fmt == OutputFormat.Json)
        {
            var data = new IndexResultData(
                Corpus: corpusName,
                IsNamedCorpus: !isDefault,
                Documents: docs.Count,
                Chunks: allChunks.Count,
                Tokens: allChunks.Sum(c => c.TokenCount),
                ChunkerMaxTokens: cfg.MaxTokens,
                ChunkerOverlapTokens: cfg.OverlapTokens,
                ChunkerWarning: cfg.Warning);
            return OutputFormatting.Ok(
                ToolName, data,
                KoshiOutputJsonContext.Default.JsonEnvelopeIndexResultData);
        }

        var msg = $"✅ Indexed {docs.Count} documents → {allChunks.Count} chunks ({allChunks.Sum(c => c.TokenCount)} tokens) — {cfg.Describe()} [corpus={corpusName}]";
        if (cfg.Warning is not null) msg += $"\n   ⚠ {cfg.Warning}";
        return msg;
    }

    private static string IndexError(OutputFormat fmt, string code, string message)
    {
        return fmt == OutputFormat.Json
            ? OutputFormatting.Error<IndexResultData>(
                "koshi_index", code, message,
                KoshiOutputJsonContext.Default.JsonEnvelopeIndexResultData)
            : $"❌ {message}";
    }

    [McpServerTool(Name = "koshi_index_directory"), Description(
        "WHEN TO CALL: When the user asks to make a project/folder searchable, or once at session start " +
        "if KOSHI_INDEX_PATH is set and nothing is yet indexed. Required before koshi_search returns " +
        "useful results on disk content.\n" +
        "WHAT IT DOES: Recursively indexes supported text files under path (or $KOSHI_INDEX_PATH). " +
        "Excludes secrets (.env*, *.pem, *.key, secrets.*), build outputs (bin/obj/dist/node_modules/.git), " +
        "and files over the size limit. Persists to KOSHI_INDEX_FILE if set. Replaces the corpus. " +
        "Streams '[indexing] N/M file (pct%, ETA)' progress on stderr (suppress with KOSHI_INDEX_VERBOSE=0) " +
        "and emits MCP notifications/progress when the client supplies a progressToken.\n" +
        "WHAT YOU GIVE IT: path (optional — defaults to $KOSHI_INDEX_PATH); pattern (optional glob); " +
        "maxFileSizeKb (default 256); maxFiles (default 5000); optional chunker tuning; optional corpus. " +
        "Pass format=\"json\" for a parseable envelope (#66).")]
    public static async Task<string> IndexDirectory(
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
        string? corpus = null,
        [Description("Output mode: 'text' (default, human-readable) or 'json' (stable structured envelope, issue #66).")]
        string? format = null,
        [Description("If true, start a background watcher (#78 Gap D) on the indexed directory so edits during this session refresh the index automatically. Honours KOSHI_INDEX_WATCH=on|poll when set; overrides env-off when explicitly true. Default: env policy.")]
        bool? watch = null,
        RequestContext<CallToolRequestParams>? context = null,
        CancellationToken cancellationToken = default)
    {
        const string ToolName = "koshi_index_directory";
        var fmt = OutputFormatting.Resolve(format, out var fmtErr);
        if (fmtErr is not null)
            return IndexDirectoryError(fmt, OutputErrorCodes.InvalidFormat, fmtErr);

        // #78 Gap D: per-call watch=true|false is threaded as a parameter
        // (not mutated into process-global state) so concurrent or
        // overlapping IndexDirectory calls cannot stomp each other's
        // intent. Env-watch / env-poll still wins on mode (more conservative
        // fallback for network mounts); see ResolveWatchMode.
        IndexWatchMode? perCallWatch = null;
        if (watch == true) perCallWatch = IndexWatchMode.Watch;
        else if (watch == false) perCallWatch = IndexWatchMode.Off;

        return await IndexDirectoryCore(
            ToolName, fmt, path, pattern, maxFileSizeKb, maxFiles,
            maxTokens, overlapTokens, corpus, perCallWatch, context, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> IndexDirectoryCore(
        string toolName,
        OutputFormat fmt,
        string? path,
        string? pattern,
        int maxFileSizeKb,
        int maxFiles,
        int? maxTokens,
        int? overlapTokens,
        string? corpus,
        IndexWatchMode? perCallWatchOverride,
        RequestContext<CallToolRequestParams>? context,
        CancellationToken cancellationToken)
    {
        var dirPath = ResolveIndexPath(path);
        if (dirPath is null)
        {
            return IndexDirectoryError(fmt, OutputErrorCodes.EmptyPath,
                "No path provided and KOSHI_INDEX_PATH is not set. " +
                "Either pass an absolute path or set the KOSHI_INDEX_PATH environment variable in your MCP client config.");
        }

        if (!Directory.Exists(dirPath))
            return IndexDirectoryError(fmt, OutputErrorCodes.DirectoryNotFound, $"Directory not found: {dirPath}");

        if (maxFileSizeKb < 1) maxFileSizeKb = DefaultMaxFileSizeKb;
        if (maxFiles < 1) maxFiles = DefaultMaxFiles;
        long maxBytes = maxFileSizeKb * 1024L;

        var enumeration = new IndexEnumerationParams
        {
            Pattern = string.IsNullOrWhiteSpace(pattern) ? null : pattern,
            MaxFileSizeBytes = maxBytes,
            MaxFiles = maxFiles,
        };

        // Wire up progress sinks (stderr always-on unless KOSHI_INDEX_VERBOSE=0;
        // MCP notifications/progress only when the caller sent a progressToken).
        var sinks = new List<IIndexProgressSink>(capacity: 2);
        if (StderrProgressEnabled()) sinks.Add(new StderrIndexProgressSink());
        if (context is not null
            && context.Params?.ProgressToken is { } token
            && context.Server is { } server)
        {
            sinks.Add(new McpIndexProgressSink(server, token));
        }

        IEnumerable<string> files;
        try
        {
            // Emit an "enumerating…" heartbeat BEFORE materialising the list:
            // on network mounts or huge trees the enumeration itself can be the
            // longest pause and the user otherwise sees pure silence.
            if (sinks.Count > 0)
            {
                var preEnum = new IndexProgressReporter(total: 0, sinks);
                await preEnum.ReportEnumeratingAsync(dirPath, cancellationToken).ConfigureAwait(false);
            }
            files = SafeFileEnumerator.EnumerateIndexableFiles(dirPath, enumeration.Pattern, maxBytes, maxFiles);
        }
        catch (UnauthorizedAccessException ex)
        {
            return IndexDirectoryError(fmt, OutputErrorCodes.AccessDenied, $"Access denied to directory: {ex.Message}");
        }
        catch (Exception ex) when (
            ex is IOException or NotSupportedException or PathTooLongException
                or System.Security.SecurityException or ArgumentException)
        {
            return IndexDirectoryError(fmt, OutputErrorCodes.IoError, $"Failed to enumerate directory: {ex.Message}");
        }

        var fileList = files.ToList();
        if (fileList.Count == 0)
            return IndexDirectoryError(fmt, OutputErrorCodes.NoFiles, $"No supported, readable files found in: {dirPath}");

        var chunkerCfg = ChunkerConfig.Resolve(maxTokens, overlapTokens);
        var chunker = new FixedSizeChunker(TokenCounters.Shared, chunkerCfg.MaxTokens, chunkerCfg.OverlapTokens);
        var allChunks = new List<Chunk>(capacity: fileList.Count * 4);
        int skipped = 0;

        var reporter = new IndexProgressReporter(fileList.Count, sinks);
        int processed = 0;

        foreach (var file in fileList)
        {
            // OperationCanceledException intentionally propagates out of the tool
            // even in JSON mode: the MCP host treats OCE as cancellation
            // (RequestCancellation), not as a tool response — so the envelope
            // must NEVER be written for cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            int chunkCountForFile = 0;
            string relativePath = Path.GetRelativePath(dirPath, file);
            try
            {
                var content = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(content))
                {
                    skipped++;
                }
                else
                {
                    var ext = Path.GetExtension(file).TrimStart('.');
                    var chunks = chunker.Chunk(content, relativePath, string.IsNullOrEmpty(ext) ? "document" : ext);
                    chunkCountForFile = chunks.Count;
                    allChunks.AddRange(chunks);

                    if (allChunks.Count > MaxChunks)
                        return IndexDirectoryError(fmt, OutputErrorCodes.TooManyChunks,
                            $"Too many chunks ({allChunks.Count} > {MaxChunks}). Use a pattern filter to reduce scope.");
                }
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                    or NotSupportedException or PathTooLongException)
            {
                _ = ex;
                skipped++;
            }
            finally
            {
                // Always report — including skips and errors — so the throttle's
                // "final emit at processed==total" invariant cannot desync if the
                // very last file happens to be empty or unreadable. Awaiting here
                // (rather than fire-and-forget) keeps the final notification
                // ordered before the tool response (rubber-duck catch).
                await reporter.ReportAsync(processed, relativePath, chunkCountForFile, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (allChunks.Count == 0)
            return IndexDirectoryError(fmt, OutputErrorCodes.AllEmpty, "All files were empty or unreadable.");

        var corpusName = IsDefaultCorpus(corpus) ? DefaultCorpusName : corpus!.Trim();
        var isDefault = IsDefaultCorpus(corpusName);
        bool snapshotSaved;
        if (isDefault)
        {
            snapshotSaved = ReplaceIndex(allChunks, source: dirPath, enumeration: enumeration, perCallWatchOverride: perCallWatchOverride);
        }
        else
        {
            ReplaceNamedCorpus(corpusName, allChunks, source: dirPath);
            snapshotSaved = false;
        }

        // snapshotSaved is now ground truth — true only when persistence is
        // enabled AND the write actually succeeded. snapshotPath is reported
        // whenever persistence is enabled (so the user knows where it would
        // have gone even if the write failed and a stderr warning was logged).
        var snapshotEligible = isDefault && _persistence.IsEnabled;
        var snapshotPath = snapshotEligible ? _persistence.Path : null;

        if (fmt == OutputFormat.Json)
        {
            var data = new IndexDirectoryResultData(
                Corpus: corpusName,
                IsNamedCorpus: !isDefault,
                Path: dirPath,
                Pattern: enumeration.Pattern,
                MaxFileSizeKb: maxFileSizeKb,
                MaxFiles: maxFiles,
                FilesIndexed: fileList.Count - skipped,
                FilesSkipped: skipped,
                Chunks: allChunks.Count,
                Tokens: allChunks.Sum(c => c.TokenCount),
                ChunkerMaxTokens: chunkerCfg.MaxTokens,
                ChunkerOverlapTokens: chunkerCfg.OverlapTokens,
                ChunkerWarning: chunkerCfg.Warning,
                SnapshotPath: snapshotPath,
                SnapshotSaved: snapshotSaved);
            return OutputFormatting.Ok(
                toolName, data,
                KoshiOutputJsonContext.Default.JsonEnvelopeIndexDirectoryResultData);
        }

        var msg = $"✅ Indexed {fileList.Count - skipped} files from '{dirPath}' → {allChunks.Count} chunks ({allChunks.Sum(c => c.TokenCount)} tokens) — {chunkerCfg.Describe()} [corpus={corpusName}]";
        if (chunkerCfg.Warning is not null) msg += $"\n   ⚠ {chunkerCfg.Warning}";
        if (skipped > 0) msg += $" ({skipped} skipped)";
        if (snapshotSaved) msg += $"\n   Snapshot saved → {_persistence.Path}";
        else if (snapshotEligible) msg += $"\n   ⚠ Snapshot write failed (see stderr) — index is in-memory only.";
        if (!isDefault) msg += "\n   (named corpus — not persisted to disk)";
        return msg;
    }

    private static string IndexDirectoryError(OutputFormat fmt, string code, string message)
    {
        return fmt == OutputFormat.Json
            ? OutputFormatting.Error<IndexDirectoryResultData>(
                "koshi_index_directory", code, message,
                KoshiOutputJsonContext.Default.JsonEnvelopeIndexDirectoryResultData)
            : $"❌ {message}";
    }

    /// <summary>
    /// Honour <c>KOSHI_INDEX_VERBOSE</c>: any of <c>0</c>/<c>off</c>/<c>false</c>/<c>no</c>
    /// (case-insensitive) suppresses stderr progress lines. Default ON because
    /// the throttle caps emissions at ~2 lines/sec and stderr is already the
    /// canonical Koshi logging channel (Program.cs:71).
    /// </summary>
    internal static bool StderrProgressEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("KOSHI_INDEX_VERBOSE");
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var v = raw.Trim();
        return !(v.Equals("0", StringComparison.Ordinal)
              || v.Equals("off", StringComparison.OrdinalIgnoreCase)
              || v.Equals("false", StringComparison.OrdinalIgnoreCase)
              || v.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    [McpServerTool(Name = "koshi_search"), Description(
        "WHEN TO CALL: BEFORE answering any code or documentation question about the indexed project. " +
        "Cheap — always preferable to guessing or asking the user to paste files. Call early in the " +
        "turn, ideally in parallel with koshi_recall.\n" +
        "WHAT IT DOES: BM25 keyword search over the indexed corpus. Auto-loads from KOSHI_INDEX_FILE " +
        "if a snapshot exists; auto-indexes KOSHI_INDEX_PATH once on first use if neither is loaded.\n" +
        "WHAT YOU GIVE IT: query (required); topK (1-50, default 5); corpus (optional — see " +
        "koshi_list_indexed for names). Pass format=\"json\" for a parseable envelope (#66).")]
    public static string Search(
        [Description("The search query")] string query,
        [Description("Number of results to return (1-50, default: 5)")] int topK = 5,
        [Description("Optional named corpus to search. Defaults to 'default'. Use koshi_list_indexed() to see available corpora.")]
        string? corpus = null,
        [Description("Output mode: 'text' (default, human-readable) or 'json' (stable structured envelope, issue #66).")]
        string? format = null)
    {
        var fmt = OutputFormatting.Resolve(format, out var fmtErr);
        if (fmtErr is not null)
            return SearchError(fmt, OutputErrorCodes.InvalidFormat, fmtErr);

        if (string.IsNullOrWhiteSpace(query))
            return SearchError(fmt, OutputErrorCodes.EmptyQuery, "Query must not be empty.");

        topK = Math.Clamp(topK < 1 ? 5 : topK, 1, MaxTopK);

        var corpusName = IsDefaultCorpus(corpus) ? DefaultCorpusName : corpus!.Trim();

        // Named-corpus path bypasses auto-index / snapshot loading entirely —
        // those features are scoped to the default corpus only (issue #23).
        if (!IsDefaultCorpus(corpusName))
        {
            if (!_extraCorpora.TryGetValue(corpusName, out var named))
                return SearchError(fmt, OutputErrorCodes.UnknownCorpus,
                    $"Unknown corpus '{corpusName}'. Call koshi_list_indexed() to see available corpora.");

            var namedResults = named.Retriever
                .SearchAsync(query, new RetrievalOptions(TopK: topK))
                .GetAwaiter().GetResult();
            return FormatSearchResults(fmt, query, namedResults, corpusName);
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
                return SearchError(fmt, OutputErrorCodes.NoIndex,
                    "No documents indexed. Call koshi_index_directory(path) first, " +
                    "or set the KOSHI_INDEX_PATH / KOSHI_INDEX_FILE environment variable in your MCP client config.");
            }

            var envPath = PathConfig.Default.IndexPath;

            // Throttle: if the most recent attempt failed and the retry window
            // hasn't elapsed, surface the cached failure WITHOUT re-running
            // the (potentially slow) directory enumeration. Outside the
            // window, retry — this is the #31 fix vs. the old one-shot block.
            // All reads/writes of the throttle state happen under
            // _autoIndexLock to keep the check-and-update atomic.
            string? throttledMessage = null;
            TimeSpan throttledRetryIn = TimeSpan.Zero;
            bool shouldRunAutoIndex = false;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (_autoIndexLock)
            {
                var sinceLastAttempt = now - _lastAutoIndexAttempt;
                if (_lastAutoIndexFailureMessage is not null && sinceLastAttempt < AutoIndexRetryAfter)
                {
                    throttledMessage = _lastAutoIndexFailureMessage;
                    throttledRetryIn = AutoIndexRetryAfter - sinceLastAttempt;
                }
                else
                {
                    _lastAutoIndexAttempt = now;
                    shouldRunAutoIndex = true;
                }
            }

            if (throttledMessage is not null)
            {
                var textMsg = throttledMessage +
                       $"\n(next auto-retry in {Math.Max(1, (int)Math.Ceiling(throttledRetryIn.TotalSeconds))}s; " +
                       $"call koshi_index_directory(\"{envPath}\") to retry immediately)";
                if (fmt == OutputFormat.Json)
                    return OutputFormatting.Error<SearchResultData>(
                        "koshi_search",
                        OutputErrorCodes.AutoIndexFailed,
                        OutputFormatting.StripTextDecorations(textMsg),
                        KoshiOutputJsonContext.Default.JsonEnvelopeSearchResultData);
                return textMsg;
            }

            // Defensive: must have set shouldRunAutoIndex above.
            if (!shouldRunAutoIndex) throw new InvalidOperationException("auto-index throttle state inconsistent");

            Console.Error.WriteLine($"[koshi] auto-indexing from KOSHI_INDEX_PATH='{envPath}'");
            // IndexDirectory was made async in #69 to support awaited progress
            // notifications; the synchronous auto-index path stays sync to keep
            // Search()'s public signature unchanged. ConfigureAwait(false) keeps
            // the continuation off any captured context. Force format:"text" so
            // a global KOSHI_OUTPUT_FORMAT=json env var cannot turn the recursive
            // call's response into a JSON envelope that the legacy '❌' StartsWith
            // probe below would never recognise.
            var auto = IndexDirectory(envPath, format: "text").ConfigureAwait(false).GetAwaiter().GetResult();
            if (auto.StartsWith('❌'))
            {
                var failureMsg = "❌ No documents indexed. Auto-index from KOSHI_INDEX_PATH failed:\n" + auto;
                lock (_autoIndexLock) { _lastAutoIndexFailureMessage = failureMsg; }
                var withRetry = failureMsg +
                       $"\n(next auto-retry in {AutoIndexRetryAfter.TotalSeconds:F0}s; " +
                       $"call koshi_index_directory(\"{envPath}\") to retry immediately)";
                if (fmt == OutputFormat.Json)
                    return OutputFormatting.Error<SearchResultData>(
                        "koshi_search",
                        OutputErrorCodes.AutoIndexFailed,
                        OutputFormatting.StripTextDecorations(withRetry),
                        KoshiOutputJsonContext.Default.JsonEnvelopeSearchResultData);
                return withRetry;
            }

            // Success: clear the failure cache so subsequent re-clears + retries start fresh.
            lock (_autoIndexLock) { _lastAutoIndexFailureMessage = null; }
        }

        KeywordRetriever retriever;
        lock (_lock)
        {
            if (!_isIndexed || _keywordRetriever is null)
                return SearchError(fmt, OutputErrorCodes.NoIndex,
                    "No documents indexed. Call koshi_index_directory or koshi_index first.");
            retriever = _keywordRetriever;
        }

        var options = new RetrievalOptions(TopK: topK);
        var results = retriever.SearchAsync(query, options).GetAwaiter().GetResult();

        return FormatSearchResults(fmt, query, results, DefaultCorpusName);
    }

    private static string SearchError(OutputFormat fmt, string code, string message)
        => fmt == OutputFormat.Json
            ? OutputFormatting.Error<SearchResultData>("koshi_search", code, message,
                KoshiOutputJsonContext.Default.JsonEnvelopeSearchResultData)
            : "❌ " + message;

    private static string FormatSearchResults(
        OutputFormat fmt, string query, IReadOnlyList<SearchResult> results, string corpusName)
    {
        if (fmt == OutputFormat.Json)
        {
            var hits = new List<SearchHitData>(results.Count);
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                hits.Add(new SearchHitData(
                    Rank: i + 1,
                    Score: r.Score,
                    Source: r.Chunk.Metadata.Source,
                    ChunkId: r.Chunk.Id,
                    Content: r.Chunk.Content));
            }
            var payload = new SearchResultData(query, corpusName, results.Count, hits);
            return OutputFormatting.Ok("koshi_search", payload,
                KoshiOutputJsonContext.Default.JsonEnvelopeSearchResultData);
        }

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
        "for per-source chunk-level detail. Pass format=\"json\" for a parseable envelope (#66).\n" +
        "WHAT YOU GIVE IT: corpus (optional — omit for summary of all corpora; name for detail).")]
    public static string ListIndexed(
        [Description("Optional corpus name. When omitted, lists every corpus (default + named). When provided, shows per-source chunk counts for that corpus only.")]
        string? corpus = null,
        [Description("Output mode: 'text' (default, human-readable) or 'json' (stable structured envelope, issue #66).")]
        string? format = null)
    {
        var fmt = OutputFormatting.Resolve(format, out var fmtErr);
        if (fmtErr is not null)
            return fmt == OutputFormat.Json
                ? OutputFormatting.Error<ListIndexedResultData>("koshi_list_indexed", OutputErrorCodes.InvalidFormat, fmtErr,
                    KoshiOutputJsonContext.Default.JsonEnvelopeListIndexedResultData)
                : "❌ " + fmtErr;

        EnsureCorpusLoaded();

        // Single-corpus detailed view.
        if (!string.IsNullOrWhiteSpace(corpus))
        {
            return fmt == OutputFormat.Json
                ? BuildListIndexedDetailJson(corpus!.Trim())
                : (IsDefaultCorpus(corpus)
                    ? DescribeDefaultCorpusDetail()
                    : DescribeNamedCorpusDetail(corpus!.Trim()));
        }

        // Multi-corpus summary.
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

        if (fmt == OutputFormat.Json)
        {
            var entries = corpora
                .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new IndexedCorpusEntry(c.name, c.chunks, c.sources, c.path))
                .ToList();
            var payload = new ListIndexedResultData(
                Mode: "summary",
                Corpus: null,
                TotalChunks: corpora.Sum(c => c.chunks),
                TotalSources: corpora.Sum(c => c.sources),
                Corpora: entries,
                Sources: new List<IndexedSourceEntry>());
            return OutputFormatting.Ok("koshi_list_indexed", payload,
                KoshiOutputJsonContext.Default.JsonEnvelopeListIndexedResultData);
        }

        if (corpora.Count == 0)
            return "No corpora indexed yet. Call koshi_index_directory or koshi_index first.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{corpora.Count} corpus{(corpora.Count == 1 ? "" : "es")} indexed:");
        foreach (var c in corpora.OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append($"  • {c.name}: {c.chunks} chunks from {c.sources} sources");
            if (c.path is not null) sb.Append($" ({c.path}{(c.snapshot ? ", from snapshot" : "")})");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildListIndexedDetailJson(string corpusName)
    {
        List<Chunk>? chunks = null;
        string? path = null;

        if (IsDefaultCorpus(corpusName))
        {
            lock (_lock)
            {
                if (_isIndexed && _indexedChunks.Count > 0)
                {
                    chunks = _indexedChunks.ToList();
                    path = _indexedFromPath;
                    corpusName = DefaultCorpusName;
                }
            }
        }
        else if (_extraCorpora.TryGetValue(corpusName, out var named))
        {
            chunks = named.Chunks.ToList();
            path = named.SourcePath;
        }
        else
        {
            return OutputFormatting.Error<ListIndexedResultData>("koshi_list_indexed", OutputErrorCodes.UnknownCorpus,
                $"Unknown corpus '{corpusName}'. Call koshi_list_indexed() to see available corpora.",
                KoshiOutputJsonContext.Default.JsonEnvelopeListIndexedResultData);
        }

        if (chunks is null || chunks.Count == 0)
        {
            var emptyPayload = new ListIndexedResultData(
                Mode: "detail",
                Corpus: corpusName,
                TotalChunks: 0,
                TotalSources: 0,
                Corpora: new List<IndexedCorpusEntry>(),
                Sources: new List<IndexedSourceEntry>());
            return OutputFormatting.Ok("koshi_list_indexed", emptyPayload,
                KoshiOutputJsonContext.Default.JsonEnvelopeListIndexedResultData);
        }

        var bySource = chunks
            .GroupBy(c => c.Metadata.Source)
            .OrderBy(g => g.Key)
            .Select(g => new IndexedSourceEntry(
                Source: g.Key,
                Chunks: g.Count(),
                Type: g.First().Metadata.DocumentType))
            .ToList();

        var payload = new ListIndexedResultData(
            Mode: "detail",
            Corpus: corpusName,
            TotalChunks: chunks.Count,
            TotalSources: bySource.Count,
            Corpora: new List<IndexedCorpusEntry>
            {
                new(corpusName, chunks.Count, bySource.Count, path),
            },
            Sources: bySource);
        return OutputFormatting.Ok("koshi_list_indexed", payload,
            KoshiOutputJsonContext.Default.JsonEnvelopeListIndexedResultData);
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
        "also deletes the KOSHI_INDEX_FILE snapshot if set. Pass corpus='*' to clear everything. " +
        "Idempotent: clearing an unknown named corpus reports success with chunksRemoved=0.\n" +
        "WHAT YOU GIVE IT: corpus (optional — null/'default' for default + snapshot; '*' for all; name " +
        "for a single named corpus). Pass format=\"json\" for a parseable envelope (#66).")]
    public static string ClearIndex(
        [Description("Corpus to clear. 'default' (or null) clears the default corpus + snapshot; '*' clears all corpora; any other value clears that named corpus only.")]
        string? corpus = null,
        [Description("Output mode: 'text' (default, human-readable) or 'json' (stable structured envelope, issue #66).")]
        string? format = null)
    {
        const string ToolName = "koshi_clear_index";
        var fmt = OutputFormatting.Resolve(format, out var fmtErr);
        if (fmtErr is not null)
            return ClearIndexError(fmt, OutputErrorCodes.InvalidFormat, fmtErr);

        if (string.Equals(corpus?.Trim(), "*", StringComparison.Ordinal))
        {
            int defaultChunks;
            bool snapshotDeleted;
            (defaultChunks, snapshotDeleted) = ClearDefaultCorpusCore();
            // Build the cleared list from successful TryRemove calls only —
            // a concurrent caller racing on the same name should not get
            // double-counted, and we never report a name we didn't actually
            // remove.
            var candidateNames = _extraCorpora.Keys.ToList();
            var actuallyCleared = new List<string>(candidateNames.Count);
            int namedChunkSum = 0;
            foreach (var n in candidateNames)
            {
                if (_extraCorpora.TryRemove(n, out var removed))
                {
                    actuallyCleared.Add(n);
                    namedChunkSum += removed.Chunks.Count;
                }
            }

            if (fmt == OutputFormat.Json)
            {
                var data = new ClearIndexResultData(
                    CorpusResolved: "*",
                    Mode: "all",
                    ChunksRemoved: defaultChunks + namedChunkSum,
                    SnapshotDeleted: snapshotDeleted,
                    SnapshotPath: snapshotDeleted ? _persistence.Path : null,
                    NamedCorporaCleared: actuallyCleared);
                return OutputFormatting.Ok(
                    ToolName, data,
                    KoshiOutputJsonContext.Default.JsonEnvelopeClearIndexResultData);
            }

            var msg = FormatDefaultCleared(defaultChunks, snapshotDeleted);
            if (actuallyCleared.Count > 0)
                msg += $"\n   Cleared {actuallyCleared.Count} named corpora: {string.Join(", ", actuallyCleared)}";
            return msg;
        }

        if (!IsDefaultCorpus(corpus))
        {
            var name = corpus!.Trim();
            var present = _extraCorpora.TryRemove(name, out var removed);
            var removedCount = present ? removed!.Chunks.Count : 0;

            if (fmt == OutputFormat.Json)
            {
                var data = new ClearIndexResultData(
                    CorpusResolved: name,
                    Mode: "named",
                    ChunksRemoved: removedCount,
                    SnapshotDeleted: false,
                    SnapshotPath: null,
                    NamedCorporaCleared: present ? new List<string> { name } : new List<string>());
                return OutputFormatting.Ok(
                    ToolName, data,
                    KoshiOutputJsonContext.Default.JsonEnvelopeClearIndexResultData);
            }

            return present
                ? $"✅ Cleared named corpus '{name}' ({removedCount} chunks removed)."
                : $"✅ Named corpus '{name}' was not indexed — nothing to clear.";
        }

        var (chunks, snapshotDeletedDefault) = ClearDefaultCorpusCore();
        if (fmt == OutputFormat.Json)
        {
            var data = new ClearIndexResultData(
                CorpusResolved: "default",
                Mode: "default",
                ChunksRemoved: chunks,
                SnapshotDeleted: snapshotDeletedDefault,
                SnapshotPath: snapshotDeletedDefault ? _persistence.Path : null,
                NamedCorporaCleared: new List<string>());
            return OutputFormatting.Ok(
                ToolName, data,
                KoshiOutputJsonContext.Default.JsonEnvelopeClearIndexResultData);
        }
        return FormatDefaultCleared(chunks, snapshotDeletedDefault);
    }

    private static string ClearIndexError(OutputFormat fmt, string code, string message)
    {
        return fmt == OutputFormat.Json
            ? OutputFormatting.Error<ClearIndexResultData>(
                "koshi_clear_index", code, message,
                KoshiOutputJsonContext.Default.JsonEnvelopeClearIndexResultData)
            : $"❌ {message}";
    }

    private static (int chunksRemoved, bool snapshotDeleted) ClearDefaultCorpusCore()
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
            _loadedChunkCount = 0;
            _loadedAt = null;
            // Reset auto-index retry throttle so the next koshi_search can
            // re-attempt KOSHI_INDEX_PATH immediately (#31).
            _lastAutoIndexAttempt = DateTimeOffset.MinValue;
            _lastAutoIndexFailureMessage = null;
            DisposeIndexWatcher();
        }

        // _persistence.Delete returns the ground-truth result (true iff a
        // file was actually removed). Prior code did a pre-delete
        // File.Exists() probe which raced with concurrent deletion and could
        // mis-report.
        var deletedSnapshot = _persistence.Delete();
        return (previousCount, deletedSnapshot);
    }

    private static string FormatDefaultCleared(int previousCount, bool deletedSnapshot)
    {
        if (previousCount == 0 && !deletedSnapshot)
            return "Default corpus already empty.";

        var msg = previousCount > 0
            ? $"✅ Cleared default corpus ({previousCount} chunks removed)."
            : "✅ Cleared default corpus (in-memory was already empty).";
        if (deletedSnapshot) msg += $"\n   Snapshot deleted → {_persistence.Path}";
        return msg;
    }

    private static string ClearDefaultCorpus()
    {
        var (chunks, snapshotDeleted) = ClearDefaultCorpusCore();
        return FormatDefaultCleared(chunks, snapshotDeleted);
    }

    internal static (
        int chunkCount, int sourceCount, string? path, bool indexed,
        bool persistenceEnabled, string? persistencePath, bool loadedFromSnapshot,
        string? snapshotDiscardReason, string? snapshotLoadWarning,
        int loadedChunkCount, DateTimeOffset? loadedAt) GetStatus()
    {
        EnsureCorpusLoaded();
        lock (_lock)
        {
            var sourceCount = _indexedChunks.Count == 0
                ? 0
                : _indexedChunks.GroupBy(c => c.Metadata.Source).Count();
            return (_indexedChunks.Count, sourceCount, _indexedFromPath, _isIndexed,
                    _persistence.IsEnabled, _persistence.Path, _loadedFromSnapshot,
                    _snapshotDiscardReason, _snapshotLoadWarning,
                    _loadedChunkCount, _loadedAt);
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
            _loadedChunkCount = envelope.Chunks.Count;
            _loadedAt = DateTimeOffset.UtcNow;
            // Snapshot satisfied the indexed-state requirement; clear any
            // prior auto-index failure cache so the throttle starts fresh
            // if a future koshi_clear_index + retry is needed (#31).
            _lastAutoIndexFailureMessage = null;

            Console.Error.WriteLine(
                $"[koshi] Loaded index snapshot: {envelope.Chunks.Count} chunks from " +
                $"'{envelope.SourcePath ?? "(unknown)"}' (saved {envelope.SavedAt:u}).");
            if (warning is not null)
                Console.Error.WriteLine($"[koshi] {warning}");

            // #78 Gap D: auto-start the watcher if the snapshot was loaded
            // from an on-disk source and the env says to watch.
            if (envelope.SourcePath is not null
                && envelope.SourcePath != ContentFingerprint.InMemorySource
                && Directory.Exists(envelope.SourcePath))
            {
                MaybeStartIndexWatcher(envelope.SourcePath, envelope.Enumeration);
            }
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

    /// <summary>
    /// Replaces the default in-memory corpus and (if persistence is enabled)
    /// writes a new snapshot to disk. Returns the snapshot-write result
    /// (<c>true</c>=persisted; <c>false</c>=disabled OR write failed).
    /// Tool envelopes that report <c>snapshot_saved</c> must rely on this
    /// return value, not on <c>_persistence.IsEnabled</c> alone — IO can
    /// fail and the user deserves an honest answer.
    /// </summary>
    private static bool ReplaceIndex(List<Chunk> chunks, string source, IndexEnumerationParams? enumeration, IndexWatchMode? perCallWatchOverride = null)
    {
        var retriever = new KeywordRetriever();
        retriever.Index(chunks);

        var fingerprint = ContentFingerprint.Compute(source, enumeration);
        bool savedOk;

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
            _loadedChunkCount = 0;
            _loadedAt = null;
            // ReplaceIndex was called explicitly — clear any prior auto-index
            // failure cache so a future clear + retry isn't blocked by stale
            // throttle state (#31).
            _lastAutoIndexFailureMessage = null;

            // #78 Gap D: snapshot save under the same lock as the in-memory
            // swap so that concurrent watcher drains and manual re-indexes
            // cannot leave memory and disk pointing at different generations.
            // The vault watcher uses the simpler dirty-bit pattern because its
            // memory is per-file; ours is corpus-wide and must stay coherent.
            savedOk = _persistence.Save(source, fingerprint, enumeration, chunks);

            MaybeStartIndexWatcher(source, enumeration, perCallWatchOverride);
        }

        return savedOk;
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

    // ---------------------------------------------------------------------
    // #78 Gap D — IndexWatcher integration
    // ---------------------------------------------------------------------

    /// <summary>
    /// Defaults for the watcher's debounce + poll cadence. Both overridable
    /// via env vars (<c>KOSHI_INDEX_WATCH_DEBOUNCE_MS</c>,
    /// <c>KOSHI_INDEX_WATCH_POLL_SECONDS</c>) for users on slow disks /
    /// network mounts where the defaults misbehave.
    /// </summary>
    internal const int DefaultWatchDebounceMs = 2000;
    internal const int DefaultWatchPollSeconds = 30;

    /// <summary>
    /// Decide whether to start a watcher and (if so) of which mode. The
    /// per-call <c>watch=true</c> on <c>koshi_index_directory</c> overrides
    /// env-off for the duration of the resulting index; env-poll is preserved
    /// even when per-call says only "watch=on" (env wins on the mode choice
    /// for the more conservative poll fallback). The override is passed as a
    /// parameter — not via process-global state — so concurrent IndexDirectory
    /// calls cannot stomp each other's intent (rubber-duck #78 round-2).
    /// </summary>
    private static IndexWatchMode ResolveWatchMode(IndexWatchMode? perCallWatchOverride = null)
    {
        var envMode = IndexWatcher.ParseModeFromEnv(Environment.GetEnvironmentVariable("KOSHI_INDEX_WATCH"));
        if (envMode != IndexWatchMode.Off) return envMode;
        if (perCallWatchOverride.HasValue) return perCallWatchOverride.Value;
        return _explicitWatchModeOverride;
    }

    /// <summary>
    /// (Re)start the watcher for the given root. Caller MUST hold <see cref="_lock"/>.
    /// Disposes any previously-running watcher first so old callbacks cannot
    /// mutate the new corpus (the drain delegate also generation-checks via
    /// <c>ReferenceEquals(this, _indexWatcher)</c> for defence in depth).
    /// </summary>
    private static void MaybeStartIndexWatcher(string source, IndexEnumerationParams? enumeration, IndexWatchMode? perCallWatchOverride = null)
    {
        var mode = ResolveWatchMode(perCallWatchOverride);

        DisposeIndexWatcher();

        if (mode == IndexWatchMode.Off) return;
        if (string.IsNullOrEmpty(source) || source == ContentFingerprint.InMemorySource) return;
        if (!Directory.Exists(source)) return;

        var debounce = TimeSpan.FromMilliseconds(
            ReadIntEnv("KOSHI_INDEX_WATCH_DEBOUNCE_MS", DefaultWatchDebounceMs, min: 50, max: 60_000));
        var pollInterval = TimeSpan.FromSeconds(
            ReadIntEnv("KOSHI_INDEX_WATCH_POLL_SECONDS", DefaultWatchPollSeconds, min: 1, max: 3600));

        // Resolve enumeration knobs once so the watcher's predicates match
        // exactly what the initial enumeration would have produced.
        var pattern = enumeration?.Pattern;
        var maxBytes = enumeration?.MaxFileSizeBytes ?? (DefaultMaxFileSizeKb * 1024L);
        var maxFiles = enumeration?.MaxFiles ?? DefaultMaxFiles;
        var rootFull = Path.GetFullPath(source);
        var snapshotPath = _persistence.Path;

        // Path predicate: drop events for the snapshot file itself and for
        // anything excluded by SafeFileEnumerator. This is the path-based
        // predicate — it correctly admits deleted paths so their chunks can
        // be removed, leaving the file-state check to the drain reader.
        bool PathPredicate(string fullPath)
        {
            if (snapshotPath is not null
                && string.Equals(Path.GetFullPath(fullPath), snapshotPath, StringComparison.OrdinalIgnoreCase))
                return false;
            if (snapshotPath is not null
                && string.Equals(Path.GetFullPath(fullPath), snapshotPath + ".tmp", StringComparison.OrdinalIgnoreCase))
                return false;
            return SafeFileEnumerator.IsPathLikelyIndexed(fullPath, pattern, rootFull);
        }

        IEnumerable<string> PollEnumerator()
        {
            try { return SafeFileEnumerator.EnumerateIndexableFiles(rootFull, pattern, maxBytes, maxFiles).ToList(); }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                    or NotSupportedException or PathTooLongException or ArgumentException)
            {
                Console.Error.WriteLine($"[koshi] Index watcher poll-enumerate failed: {ex.Message}");
                return [];
            }
        }

        IndexWatcher? created = null;
        Task<IndexWatcherDrainResult> Drain(IReadOnlyList<string> pendingRel, CancellationToken ct)
        {
            return Task.FromResult(ApplyWatcherDrain(created!, rootFull, pattern, maxBytes, pendingRel, ct));
        }

        created = new IndexWatcher(
            rootPath: rootFull,
            mode: mode,
            debounce: debounce,
            pollInterval: pollInterval,
            pathPredicate: PathPredicate,
            pollEnumerator: PollEnumerator,
            onDrain: Drain);

        _indexWatcher = created;
    }

    private static int ReadIntEnv(string name, int fallback, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw.Trim(), out var v)) return fallback;
        return Math.Clamp(v, min, max);
    }

    private static void DisposeIndexWatcher()
    {
        var w = _indexWatcher;
        _indexWatcher = null;
        try { w?.Dispose(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException) { _ = ex; }
    }

    /// <summary>
    /// Drain callback shared by Watch and Poll modes. Applies the pending
    /// path set to the in-memory corpus and writes a new snapshot. Returns
    /// a <see cref="IndexWatcherDrainResult"/> describing the outcome so the
    /// watcher can update its telemetry and degraded state.
    ///
    /// <para>Reads happen OUTSIDE <see cref="_lock"/> (file IO + chunking can
    /// be slow) and the result is published under the lock with a generation
    /// check (<c>ReferenceEquals(self, _indexWatcher)</c>). If a manual
    /// re-index or a clear runs between the read and the publish, the drain
    /// is dropped silently and a future event will retry.</para>
    /// </summary>
    private static IndexWatcherDrainResult ApplyWatcherDrain(
        IndexWatcher self,
        string rootFull,
        string? pattern,
        long maxBytes,
        IReadOnlyList<string> pendingRel,
        CancellationToken ct)
    {
        if (pendingRel.Count == 0) return new IndexWatcherDrainResult(false, false, null);

        // Snapshot existing chunks under the lock so we don't read a list
        // that's concurrently mutated. Generation check ensures we don't
        // process events for a corpus that has since been replaced.
        List<Chunk> currentChunks;
        IndexEnumerationParams? enumeration;
        lock (_lock)
        {
            if (!ReferenceEquals(self, _indexWatcher)) return new IndexWatcherDrainResult(false, false, "stale watcher");
            if (_indexedFromPath is null || !string.Equals(Path.GetFullPath(_indexedFromPath), rootFull, StringComparison.OrdinalIgnoreCase))
                return new IndexWatcherDrainResult(false, false, "root changed");
            currentChunks = [.. _indexedChunks];
            enumeration = _indexedEnumeration;
        }

        var chunkerCfg = ChunkerConfig.Resolve(enumeration is null ? null : (int?)null, null);
        var chunker = new FixedSizeChunker(TokenCounters.Shared, chunkerCfg.MaxTokens, chunkerCfg.OverlapTokens);
        var maxFiles = enumeration?.MaxFiles ?? DefaultMaxFiles;

        // Build a working copy keyed by normalized relpath so chunk replacement is O(N).
        var byPath = currentChunks.GroupBy(c => Normalize(c.Metadata.Source))
                                  .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        bool anyError = false;
        string? firstErrorMessage = null;

        foreach (var relOrFull in pendingRel)
        {
            ct.ThrowIfCancellationRequested();

            // The watcher may emit relative paths (normal case) or absolute
            // paths (root-sentinel case from error handler). Normalise.
            string rel;
            string fullPath;
            if (Path.IsPathRooted(relOrFull))
            {
                fullPath = Path.GetFullPath(relOrFull);
                try { rel = Path.GetRelativePath(rootFull, fullPath).Replace('\\', '/'); }
                catch (ArgumentException) { continue; }
            }
            else
            {
                rel = relOrFull.Replace('\\', '/');
                // Use Path.Join (not Combine) — Combine would silently drop rootFull
                // if rel were rooted. !Path.IsPathRooted(relOrFull) is already true,
                // but Path.Join is footgun-free regardless.
                fullPath = Path.GetFullPath(Path.Join(rootFull, rel));
            }

            // Root sentinel: full reconciliation. Clear all chunks so deleted
            // files no longer linger; the directory-event branch below will
            // re-enumerate the entire tree and repopulate. Without this clear
            // the per-chunk dictionary still holds removed-file entries
            // because prefix-match on "./" never matches "foo.md".
            var isRootSentinel = string.Equals(rel, ".", StringComparison.Ordinal)
                              || string.Equals(fullPath, rootFull, StringComparison.OrdinalIgnoreCase);
            if (isRootSentinel) byPath.Clear();

            // Directory event: prefix-remove all chunks under it, then
            // enumerate the subtree for adds (only if it currently exists).
            if (Directory.Exists(fullPath))
            {
                if (!isRootSentinel)
                {
                    var prefix = rel.TrimEnd('/') + "/";
                    foreach (var key in byPath.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                        byPath.Remove(key);
                }

                try
                {
                    foreach (var child in SafeFileEnumerator.EnumerateIndexableFiles(fullPath, pattern, maxBytes, maxFiles))
                    {
                        var childRel = Path.GetRelativePath(rootFull, child).Replace('\\', '/');
                        if (TryReadAndChunk(child, childRel, chunker, out var newChunks, out var err))
                            byPath[Normalize(childRel)] = newChunks;
                        else if (err is not null) { anyError = true; firstErrorMessage ??= err; }
                    }
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                        or NotSupportedException or PathTooLongException or ArgumentException)
                {
                    anyError = true; firstErrorMessage ??= ex.Message;
                }
                continue;
            }

            // File event: remove existing chunks for this path.
            byPath.Remove(Normalize(rel));

            // If file exists and is currently indexable, re-read and add chunks.
            if (File.Exists(fullPath))
            {
                if (!SafeFileEnumerator.IsCurrentlyIndexable(fullPath, pattern, maxBytes, rootPath: rootFull)) continue;
                if (TryReadAndChunk(fullPath, rel, chunker, out var fileChunks, out var fileErr))
                    byPath[Normalize(rel)] = fileChunks;
                else if (fileErr is not null) { anyError = true; firstErrorMessage ??= fileErr; }
                continue;
            }

            // Path doesn't exist as file or directory: could be a deleted
            // file (handled by the byPath.Remove above) OR the old side of
            // a directory rename / a deleted directory whose child events
            // never fired (some FSW back-ends, network mounts). Also do a
            // subtree prefix-remove so any chunks under the old directory
            // are reclaimed (rubber-duck #78 round-2 #2). Idempotent: if
            // no chunks share this prefix the dictionary scan is a no-op.
            var ghostPrefix = rel.TrimEnd('/') + "/";
            foreach (var key in byPath.Keys.Where(k => k.StartsWith(ghostPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
                byPath.Remove(key);
        }

        var rebuilt = byPath.Values.SelectMany(x => x).ToList();
        if (rebuilt.Count > MaxChunks)
        {
            return new IndexWatcherDrainResult(false, true,
                $"incremental update would exceed MaxChunks ({rebuilt.Count} > {MaxChunks}); kept previous index");
        }

        // Don't publish a corrupt snapshot if any file failed to read — the
        // previous in-memory index is still serving correct results, and the
        // user will see the degraded reason in koshi_health.
        if (anyError)
        {
            return new IndexWatcherDrainResult(false, true,
                $"transient read failure: {firstErrorMessage} (snapshot not updated)");
        }

        var retriever = new KeywordRetriever();
        retriever.Index(rebuilt);
        var fingerprint = ContentFingerprint.Compute(rootFull, enumeration);

        lock (_lock)
        {
            if (!ReferenceEquals(self, _indexWatcher)) return new IndexWatcherDrainResult(false, false, "stale watcher");
            if (_indexedFromPath is null || !string.Equals(Path.GetFullPath(_indexedFromPath), rootFull, StringComparison.OrdinalIgnoreCase))
                return new IndexWatcherDrainResult(false, false, "root changed");
            _indexedChunks = rebuilt;
            _keywordRetriever = retriever;
            _persistence.Save(rootFull, fingerprint, enumeration, rebuilt);
        }

        return new IndexWatcherDrainResult(true, false, null);

        static string Normalize(string s) => s.Replace('\\', '/');
    }

    private static bool TryReadAndChunk(
        string fullPath, string relPath, FixedSizeChunker chunker,
        out List<Chunk> chunks, out string? error)
    {
        chunks = [];
        error = null;
        try
        {
            var content = File.ReadAllText(fullPath);
            if (string.IsNullOrWhiteSpace(content)) return true;
            var ext = Path.GetExtension(fullPath).TrimStart('.');
            chunks = chunker.Chunk(content, relPath, string.IsNullOrEmpty(ext) ? "document" : ext).ToList();
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or NotSupportedException or PathTooLongException or ArgumentException)
        {
            error = $"{Path.GetFileName(fullPath)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Telemetry snapshot for <c>koshi_health</c> (#78 Gap D).</summary>
    internal static (string mode, string status, string? root, bool degraded, string? degradedReason,
                     int pendingEvents, int totalRebuilds, DateTimeOffset? lastEventAt, DateTimeOffset? lastRebuildAt,
                     int debounceMs, int pollIntervalSeconds) GetIndexWatcherStatus()
    {
        var w = _indexWatcher;
        if (w is null)
            return ("off", "disabled (mode=off)", null, false, null, 0, 0, null, null,
                    DefaultWatchDebounceMs, DefaultWatchPollSeconds);
        return (
            w.Mode.ToString().ToLowerInvariant(),
            w.Status,
            w.RootPath,
            w.IsDegraded,
            w.DegradedReason,
            w.PendingEvents,
            w.TotalRebuilds,
            w.LastEventAt,
            w.LastRebuildAt,
            (int)w.Debounce.TotalMilliseconds,
            (int)w.PollInterval.TotalSeconds);
    }

    /// <summary>Test seam: synchronously drain the watcher one cycle.</summary>
    internal static Task<int> DrainIndexWatcherForTest(CancellationToken ct = default) =>
        _indexWatcher?.DrainNowForTest(ct) ?? Task.FromResult(0);

    /// <summary>Test seam: synthesise an event without touching the filesystem.</summary>
    internal static void RaiseIndexWatcherEventForTest(string fullPath) =>
        _indexWatcher?.RaiseForTest(fullPath);

    /// <summary>Test seam: set the per-call watch override before calling IndexDirectory.</summary>
    internal static void SetExplicitWatchOverrideForTest(IndexWatchMode mode) =>
        _explicitWatchModeOverride = mode;

}
