namespace Koshi.Core.Configuration;

/// <summary>
/// Configuration for Koshi retrieval engine.
/// Designed for Ollama (local, free) with easy swap to Azure OpenAI later.
/// </summary>
public record KoshiConfig
{
    public string OllamaBaseUrl { get; init; } = "http://localhost:11434";
    public string EmbeddingModel { get; init; } = "nomic-embed-text";
    public string ChatModel { get; init; } = "phi4-mini";
    public int EmbeddingDimensions { get; init; } = 768; // nomic-embed-text default
    public int DefaultChunkSize { get; init; } = 256;
    public int DefaultChunkOverlap { get; init; } = 25;
    public int DefaultTopK { get; init; } = 5;
    public int RerankCandidates { get; init; } = 20;
    public int RrfK { get; init; } = 60;
}
