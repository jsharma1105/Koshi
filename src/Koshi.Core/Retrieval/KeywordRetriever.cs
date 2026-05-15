namespace Koshi.Core.Retrieval;

using Koshi.Core.Models;

/// <summary>
/// BM25 keyword search implementation.
/// Handles exact matches that embeddings miss: error codes, config keys, identifiers.
/// </summary>
public sealed class KeywordRetriever : IRetriever
{
    private readonly Lock _lock = new();
    private List<Chunk> _corpus = [];
    private Dictionary<string, double> _idf = [];
    private Dictionary<string, Dictionary<string, double>> _tf = [];
    private Dictionary<string, int> _docLengths = [];
    private double _avgDocLength;
    private bool _isIndexed;

    private const double K1 = 1.5;
    private const double B = 0.75;

    public void Index(IReadOnlyList<Chunk> chunks)
    {
        if (chunks.Count == 0) return;

        lock (_lock)
        {
            // Clear all state to prevent corruption on re-index
            _corpus = [.. chunks];
            _idf = [];
            _tf = [];
            _docLengths = [];

            var docFreq = new Dictionary<string, int>();
            var totalDocs = _corpus.Count;

            foreach (var chunk in _corpus)
            {
                var terms = Tokenize(chunk.Content);
                var rawCounts = new Dictionary<string, double>();
                foreach (var term in terms)
                    rawCounts[term] = rawCounts.GetValueOrDefault(term) + 1;

                // Store RAW term counts (not normalized — BM25 handles normalization)
                _tf[chunk.Id] = rawCounts;
                _docLengths[chunk.Id] = terms.Count > 0 ? terms.Count : 1;

                foreach (var term in rawCounts.Keys)
                    docFreq[term] = docFreq.GetValueOrDefault(term) + 1;
            }

            foreach (var (term, df) in docFreq)
                _idf[term] = Math.Log((totalDocs - df + 0.5) / (df + 0.5) + 1);

            _avgDocLength = _corpus.Count > 0
                ? _corpus.Average(c => (double)_docLengths[c.Id])
                : 1.0;

            _isIndexed = true;
        }
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, RetrievalOptions options, CancellationToken ct = default)
    {
        List<Chunk> corpus;
        Dictionary<string, double> idf;
        Dictionary<string, Dictionary<string, double>> tf;
        Dictionary<string, int> docLengths;
        double avgDocLength;

        // Snapshot state under lock for thread-safe reads
        lock (_lock)
        {
            if (!_isIndexed)
                return Task.FromResult<IReadOnlyList<SearchResult>>([]);

            corpus = _corpus;
            idf = _idf;
            tf = _tf;
            docLengths = _docLengths;
            avgDocLength = _avgDocLength;
        }

        var queryTerms = Tokenize(query);
        var scores = new List<SearchResult>();

        foreach (var chunk in corpus)
        {
            ct.ThrowIfCancellationRequested();

            if (options.DocumentTypeFilter is not null &&
                chunk.Metadata.DocumentType != options.DocumentTypeFilter)
                continue;

            double score = 0;
            var chunkTf = tf.GetValueOrDefault(chunk.Id) ?? [];
            var docLen = docLengths.GetValueOrDefault(chunk.Id, 1);

            foreach (var term in queryTerms)
            {
                if (!idf.TryGetValue(term, out var idfVal)) continue;
                var termFreq = chunkTf.GetValueOrDefault(term);

                // Standard BM25 with raw term frequency
                var numerator = termFreq * (K1 + 1);
                var denominator = termFreq + K1 * (1 - B + B * docLen / avgDocLength);
                score += idfVal * (numerator / denominator);
            }

            if (score > 0)
                scores.Add(new SearchResult(chunk, (float)score, "keyword"));
        }

        var results = scores
            .OrderByDescending(r => r.Score)
            .Take(options.TopK)
            .ToList();

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    private static List<string> Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '.', ',', '(', ')', '{', '}', '[', ']', ':', ';', '"', '\'', '/', '\\', '-', '_', '=', '>', '<', '!', '?', '#', '*', '|', '`'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1)
            .ToList();
}
