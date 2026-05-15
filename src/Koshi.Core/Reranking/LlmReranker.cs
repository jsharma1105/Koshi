namespace Koshi.Core.Reranking;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Koshi.Core.Models;
using Koshi.Core.Tokenization;
using Microsoft.Extensions.AI;

/// <summary>
/// LLM-based reranker using a local Ollama chat model.
/// Sends top-N candidates to the model with a relevance-scoring prompt,
/// parses scores, and re-orders results.
/// </summary>
public sealed class LlmReranker : IReranker
{
    private readonly IChatClient _chatClient;
    private readonly TokenCounter _tokenCounter;

    public RerankStats LastStats { get; private set; } = new(0, 0, TimeSpan.Zero, 0);

    public LlmReranker(IChatClient chatClient, TokenCounter tokenCounter)
    {
        _chatClient = chatClient;
        _tokenCounter = tokenCounter;
    }

    public async Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query, IReadOnlyList<SearchResult> candidates, int topK, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return [];
        if (candidates.Count <= topK)
        {
            LastStats = new RerankStats(0, 0, TimeSpan.Zero, candidates.Count);
            return candidates.ToList();
        }

        var sw = Stopwatch.StartNew();

        // Build the scoring prompt
        var prompt = BuildPrompt(query, candidates);
        int inputTokens = _tokenCounter.CountTokens(prompt);

        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct);
        var responseText = response.Text ?? "";
        int outputTokens = _tokenCounter.CountTokens(responseText);

        sw.Stop();
        LastStats = new RerankStats(inputTokens, outputTokens, sw.Elapsed, candidates.Count);

        // Parse scores and reorder
        var scores = ParseScores(responseText, candidates.Count);
        var reranked = candidates
            .Select((c, i) => (Result: c, LlmScore: i < scores.Count ? scores[i] : 0f))
            .OrderByDescending(x => x.LlmScore)
            .Take(topK)
            .Select(x => x.Result with { Score = x.LlmScore, Source = $"{x.Result.Source}+reranked" })
            .ToList();

        return reranked;
    }

    private static string BuildPrompt(string query, IReadOnlyList<SearchResult> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a relevance scoring system. Rate how relevant each passage is to the query.");
        sb.AppendLine("Respond with ONLY a JSON array of scores (0.0 to 1.0), one per passage, in order.");
        sb.AppendLine("Example response: [0.9, 0.3, 0.7, 0.1]");
        sb.AppendLine();
        sb.AppendLine($"Query: {query}");
        sb.AppendLine();

        for (int i = 0; i < candidates.Count; i++)
        {
            var content = candidates[i].Chunk.Content;
            // Truncate long passages to keep prompt manageable
            if (content.Length > 500)
                content = content[..500] + "...";
            sb.AppendLine($"Passage {i + 1}: {content}");
            sb.AppendLine();
        }

        sb.AppendLine("Scores (JSON array only):");
        return sb.ToString();
    }

    private static List<float> ParseScores(string responseText, int expectedCount)
    {
        try
        {
            // Find JSON array in response (model may add extra text)
            var start = responseText.IndexOf('[');
            var end = responseText.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                var json = responseText[start..(end + 1)];
                var parsed = JsonSerializer.Deserialize<List<float>>(json);
                if (parsed is not null)
                    return parsed;
            }
        }
        catch (JsonException)
        {
            // Fall through to default
        }

        // Fallback: return equal scores (preserves original order)
        return Enumerable.Repeat(0.5f, expectedCount).ToList();
    }
}
