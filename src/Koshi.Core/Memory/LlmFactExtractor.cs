namespace Koshi.Core.Memory;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Koshi.Core.Retrieval;
using Koshi.Core.Tokenization;
using Microsoft.Extensions.AI;

/// <summary>
/// Uses a local LLM (Ollama) to extract structured facts from interaction text.
/// Includes a quality gate: validates schema, enforces confidence threshold,
/// deduplicates against existing memories by embedding similarity.
/// </summary>
public sealed class LlmFactExtractor : IFactExtractor
{
    private readonly IChatClient _chatClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IMemoryStore _memoryStore;
    private readonly TokenCounter _tokenCounter;
    private readonly float _confidenceThreshold;
    private readonly float _dedupeThreshold;
    private readonly string _embeddingModel;

    public LlmFactExtractor(
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IMemoryStore memoryStore,
        TokenCounter tokenCounter,
        string embeddingModel = "nomic-embed-text",
        float confidenceThreshold = 0.6f,
        float dedupeThreshold = 0.92f)
    {
        _chatClient = chatClient;
        _embeddingGenerator = embeddingGenerator;
        _memoryStore = memoryStore;
        _tokenCounter = tokenCounter;
        _embeddingModel = embeddingModel;
        _confidenceThreshold = confidenceThreshold;
        _dedupeThreshold = dedupeThreshold;
    }

    public async Task<ExtractionResult> ExtractAsync(
        string interaction, MemoryScope scope, string source, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Step 1: Ask LLM to extract structured facts
        var prompt = BuildExtractionPrompt(interaction);
        int inputTokens = _tokenCounter.CountTokens(prompt);

        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: ct);
        var responseText = response.Text ?? "";
        int outputTokens = _tokenCounter.CountTokens(responseText);

        // Step 2: Parse response into raw facts
        var rawFacts = ParseExtractedFacts(responseText);
        var rejectionReasons = new List<string>();

        // Step 3: Quality gate — validate, score, deduplicate
        var accepted = new List<MemoryRecord>();

        foreach (var raw in rawFacts)
        {
            ct.ThrowIfCancellationRequested();

            // Gate 1: Schema validation — must have content and subject
            if (string.IsNullOrWhiteSpace(raw.Content) || string.IsNullOrWhiteSpace(raw.Subject))
            {
                rejectionReasons.Add($"Empty content/subject: '{(raw.Content ?? "")[..Math.Min(30, (raw.Content ?? "").Length)]}'");
                continue;
            }

            // Gate 2: Length validation — skip trivial or excessive extractions
            if (raw.Content.Length < 10)
            {
                rejectionReasons.Add($"Too short: '{raw.Content}'");
                continue;
            }
            if (raw.Content.Length > 2000)
            {
                rejectionReasons.Add($"Too long ({raw.Content.Length} chars): '{raw.Content[..40]}'");
                continue;
            }

            // Gate 3: Confidence threshold
            if (raw.Confidence < _confidenceThreshold)
            {
                rejectionReasons.Add($"Low confidence ({raw.Confidence:F2}): '{raw.Content[..Math.Min(40, raw.Content.Length)]}'");
                continue;
            }

            // Gate 4: Generate embedding and check for duplicates
            var embeddingResult = await _embeddingGenerator.GenerateAsync(
                [raw.Content], cancellationToken: ct);
            var embedding = embeddingResult[0].Vector.ToArray();

            bool isDuplicate = await CheckDuplicateAsync(embedding, scope, ct);
            if (isDuplicate)
            {
                rejectionReasons.Add($"Duplicate: '{raw.Content[..Math.Min(40, raw.Content.Length)]}'");
                continue;
            }

            // Passed all gates — create memory record
            var memory = new MemoryRecord
            {
                Id = $"mem-{Guid.NewGuid():N}",
                Type = raw.Type,
                Content = raw.Content,
                Subject = raw.Subject,
                Scope = scope,
                Source = source,
                Confidence = raw.Confidence,
                CreatedAt = DateTimeOffset.UtcNow,
                LastAccessedAt = DateTimeOffset.UtcNow,
                AccessCount = 0,
                Tier = MemoryTier.Hot,
                Embedding = embedding,
                EmbeddingModel = _embeddingModel,
                EmbeddingDimensions = embedding.Length,
            };

            accepted.Add(memory);
        }

        // Step 4: Persist accepted memories
        if (accepted.Count > 0)
            await _memoryStore.StoreAsync(accepted, ct);

        sw.Stop();

        return new ExtractionResult
        {
            Extracted = accepted,
            Accepted = accepted.Count,
            Rejected = rawFacts.Count - accepted.Count,
            RejectionReasons = rejectionReasons,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Duration = sw.Elapsed,
        };
    }

    private async Task<bool> CheckDuplicateAsync(float[] embedding, MemoryScope scope, CancellationToken ct)
    {
        var existing = await _memoryStore.SearchByEmbeddingAsync(embedding, scope, topK: 3, ct: ct);
        foreach (var candidate in existing)
        {
            if (candidate.Embedding is null) continue;
            float similarity = Similarity.Cosine(embedding, candidate.Embedding);
            if (similarity >= _dedupeThreshold)
                return true;
        }
        return false;
    }

    private static string BuildExtractionPrompt(string interaction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            You are a knowledge extraction system. Extract important facts, decisions, patterns, and preferences from the interaction below.

            For each extracted item, provide:
            - "type": one of "Fact", "Decision", "Pattern", "Preference"
            - "subject": short topic/entity this is about (e.g., "API architecture", "database choice", "coding style")
            - "content": the extracted knowledge as a clear, standalone statement
            - "confidence": 0.0 to 1.0 how confident you are this is accurate and important

            Rules:
            - Only extract genuinely useful knowledge that would help in future interactions
            - Skip greetings, acknowledgments, filler, and questions
            - Each item should be a self-contained statement (understandable without the original context)
            - Prefer fewer high-quality extractions over many low-quality ones
            - If nothing is worth extracting, return an empty array []

            Respond with ONLY a JSON array. Example:
            [
              {"type": "Fact", "subject": "API design", "content": "The team uses stored procedures exclusively, no EF Core or raw SQL in controllers", "confidence": 0.95},
              {"type": "Decision", "subject": "database migration", "content": "Decided to migrate from EF Core to Dapper for performance reasons", "confidence": 0.85}
            ]

            Interaction:
            """);
        sb.AppendLine(interaction);
        sb.AppendLine();
        sb.AppendLine("Extracted knowledge (JSON array only):");
        return sb.ToString();
    }

    private static List<RawExtractedFact> ParseExtractedFacts(string responseText)
    {
        try
        {
            var start = responseText.IndexOf('[');
            var end = responseText.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                var json = responseText[start..(end + 1)];
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
                };
                var parsed = JsonSerializer.Deserialize<List<RawExtractedFact>>(json, options);
                return parsed ?? [];
            }
        }
        catch (JsonException)
        {
            // Fall through
        }

        return [];
    }

    private sealed record RawExtractedFact
    {
        public MemoryType Type { get; init; }
        public string Subject { get; init; } = "";
        public string Content { get; init; } = "";
        public float Confidence { get; init; }
    }
}
