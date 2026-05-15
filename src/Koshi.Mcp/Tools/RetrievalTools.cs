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
    private static readonly Lazy<TokenCounter> _tokenCounter = new(() =>
        TokenCounter.CreateAsync("gpt-4").GetAwaiter().GetResult());

    private static KeywordRetriever? _keywordRetriever;
    private static List<Chunk> _indexedChunks = [];
    private static string? _indexedFromPath;
    private static bool _isIndexed;
    private static bool _autoIndexAttempted;

    [McpServerTool(Name = "koshi_index"), Description(
        "Index a list of in-memory documents for BM25 retrieval. " +
        "Replaces any previously indexed corpus. " +
        "For indexing files on disk, use koshi_index_directory instead.")]
    public static string Index(
        [Description("JSON array of documents: [{\"content\": \"...\", \"source\": \"filename.md\", \"type\": \"documentation\"}]")]
        string documents)
    {
        List<DocInput>? docs;
        try
        {
            docs = JsonSerializer.Deserialize<List<DocInput>>(documents,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return $"❌ Invalid JSON: {ex.Message}";
        }

        if (docs is null || docs.Count == 0)
            return "❌ No documents provided.";

        var chunker = new FixedSizeChunker(_tokenCounter.Value, maxTokens: 512, overlapTokens: 50);
        var allChunks = new List<Chunk>();

        foreach (var doc in docs)
        {
            var chunks = chunker.Chunk(doc.Content, doc.Source ?? "unknown", doc.Type ?? "document");
            allChunks.AddRange(chunks);
        }

        if (allChunks.Count > MaxChunks)
            return $"❌ Too many chunks ({allChunks.Count} > {MaxChunks}). Reduce document count or size.";

        ReplaceIndex(allChunks, source: "in-memory");
        return $"✅ Indexed {docs.Count} documents → {allChunks.Count} chunks ({allChunks.Sum(c => c.TokenCount)} tokens)";
    }

    [McpServerTool(Name = "koshi_index_directory"), Description(
        "Index supported text files from a directory recursively for BM25 retrieval. " +
        "If no path is provided, falls back to the KOSHI_INDEX_PATH environment variable. " +
        "Excludes secrets (.env*, *.pem, *.key, *.pfx, secrets.*), build output (bin/obj/dist/node_modules/.git), " +
        "and files larger than the configured limit. " +
        "This replaces any previously indexed corpus.")]
    public static string IndexDirectory(
        [Description("Absolute directory path to index (defaults to $KOSHI_INDEX_PATH)")]
        string? path = null,
        [Description("Glob pattern to filter files (e.g. '*.md'). If empty, all supported text file types are indexed.")]
        string? pattern = null,
        [Description("Maximum file size in KB to index (default: 256)")] int maxFileSizeKb = DefaultMaxFileSizeKb,
        [Description("Maximum number of files to index (default: 5000)")] int maxFiles = DefaultMaxFiles)
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

        IEnumerable<string> files;
        try
        {
            files = SafeFileEnumerator.EnumerateIndexableFiles(dirPath, pattern, maxBytes, maxFiles);
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

        var chunker = new FixedSizeChunker(_tokenCounter.Value, maxTokens: 512, overlapTokens: 50);
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

        ReplaceIndex(allChunks, source: dirPath);

        var msg = $"✅ Indexed {fileList.Count - skipped} files from '{dirPath}' → {allChunks.Count} chunks ({allChunks.Sum(c => c.TokenCount)} tokens)";
        if (skipped > 0) msg += $" ({skipped} skipped)";
        return msg;
    }

    [McpServerTool(Name = "koshi_search"), Description(
        "Search the indexed corpus using BM25 keyword retrieval. " +
        "Returns the most relevant chunks for the query. " +
        "If no corpus is indexed and KOSHI_INDEX_PATH is set, the path will be auto-indexed once on first use.")]
    public static string Search(
        [Description("The search query")] string query,
        [Description("Number of results to return (1-50, default: 5)")] int topK = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "❌ Query must not be empty.";

        topK = Math.Clamp(topK < 1 ? 5 : topK, 1, MaxTopK);

        bool needsIndex;
        lock (_lock) { needsIndex = !_isIndexed || _keywordRetriever is null; }

        if (needsIndex && !_autoIndexAttempted)
        {
            _autoIndexAttempted = true;
            var envPath = Environment.GetEnvironmentVariable("KOSHI_INDEX_PATH");
            if (!string.IsNullOrEmpty(envPath))
            {
                var auto = IndexDirectory(envPath);
                if (auto.StartsWith('❌'))
                {
                    return "❌ No documents indexed. Auto-index from KOSHI_INDEX_PATH failed:\n" + auto;
                }
            }
            else
            {
                return "❌ No documents indexed. Call koshi_index_directory(path) first, " +
                       "or set the KOSHI_INDEX_PATH environment variable in your MCP client config.";
            }
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
                sb.AppendLine($"Source: {_indexedFromPath}");
            sb.AppendLine();

            foreach (var group in bySource)
            {
                sb.AppendLine($"  • {group.Key} ({group.Count()} chunks, {group.Sum(c => c.TokenCount)} tokens)");
            }

            return sb.ToString();
        }
    }

    [McpServerTool(Name = "koshi_clear_index"), Description(
        "Clear the indexed corpus. Useful when switching between projects without restarting the server.")]
    public static string ClearIndex()
    {
        int previousCount;
        lock (_lock)
        {
            previousCount = _indexedChunks.Count;
            _indexedChunks = [];
            _keywordRetriever = null;
            _indexedFromPath = null;
            _isIndexed = false;
            _autoIndexAttempted = false;
        }
        return previousCount == 0
            ? "Index already empty."
            : $"✅ Cleared index ({previousCount} chunks removed).";
    }

    internal static (int chunkCount, int sourceCount, string? path, bool indexed) GetStatus()
    {
        lock (_lock)
        {
            var sourceCount = _indexedChunks.Count == 0
                ? 0
                : _indexedChunks.GroupBy(c => c.Metadata.Source).Count();
            return (_indexedChunks.Count, sourceCount, _indexedFromPath, _isIndexed);
        }
    }

    private static void ReplaceIndex(List<Chunk> chunks, string source)
    {
        var retriever = new KeywordRetriever();
        retriever.Index(chunks);

        lock (_lock)
        {
            _indexedChunks = chunks;
            _keywordRetriever = retriever;
            _indexedFromPath = source;
            _isIndexed = true;
            _autoIndexAttempted = true;
        }
    }

    private static string? ResolveIndexPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);

        var env = Environment.GetEnvironmentVariable("KOSHI_INDEX_PATH");
        return string.IsNullOrWhiteSpace(env) ? null : Path.GetFullPath(env);
    }

    private sealed record DocInput
    {
        public string Content { get; init; } = "";
        public string? Source { get; init; }
        public string? Type { get; init; }
    }
}
