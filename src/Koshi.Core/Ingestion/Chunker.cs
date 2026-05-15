namespace Koshi.Core.Ingestion;

using Koshi.Core.Models;
using Koshi.Core.Tokenization;

public interface IChunker
{
    IReadOnlyList<Chunk> Chunk(string content, string source, string documentType);
}

/// <summary>
/// Fixed-size chunker that splits by token count with overlap.
/// The baseline strategy — simple but effective for prose/docs.
/// </summary>
public sealed class FixedSizeChunker : IChunker
{
    private readonly TokenCounter _tokenCounter;
    private readonly int _maxTokens;
    private readonly int _overlapTokens;

    public FixedSizeChunker(TokenCounter tokenCounter, int maxTokens = 256, int overlapTokens = 32)
    {
        _tokenCounter = tokenCounter;
        _maxTokens = maxTokens;
        _overlapTokens = overlapTokens;
    }

    public IReadOnlyList<Chunk> Chunk(string content, string source, string documentType)
    {
        var chunks = new List<Chunk>();
        var lines = content.Split('\n');
        var currentChunk = new List<string>();
        int currentTokens = 0;
        int chunkStartLine = 0;
        int lineOffset = 0;

        foreach (var line in lines)
        {
            int lineTokens = _tokenCounter.CountTokens(line);

            if (currentTokens + lineTokens > _maxTokens && currentChunk.Count > 0)
            {
                var chunkText = string.Join('\n', currentChunk);
                chunks.Add(new Chunk(
                    Id: $"{source}:chunk-{chunks.Count}",
                    Content: chunkText,
                    Metadata: new ChunkMetadata(
                        Source: source,
                        DocumentType: documentType,
                        StartOffset: chunkStartLine,
                        EndOffset: lineOffset - 1,
                        IngestedAt: DateTimeOffset.UtcNow))
                {
                    TokenCount = _tokenCounter.CountTokens(chunkText)
                });

                // Keep overlap lines
                int overlapTokenCount = 0;
                var overlapLines = new List<string>();
                for (int i = currentChunk.Count - 1; i >= 0; i--)
                {
                    int lt = _tokenCounter.CountTokens(currentChunk[i]);
                    if (overlapTokenCount + lt > _overlapTokens) break;
                    overlapLines.Insert(0, currentChunk[i]);
                    overlapTokenCount += lt;
                }

                currentChunk = overlapLines;
                currentTokens = overlapTokenCount;
                // Next chunk starts at current line minus overlap lines
                chunkStartLine = lineOffset - overlapLines.Count;
            }

            currentChunk.Add(line);
            currentTokens += lineTokens;
            lineOffset++;
        }

        // Final chunk
        if (currentChunk.Count > 0)
        {
            var chunkText = string.Join('\n', currentChunk);
            chunks.Add(new Chunk(
                Id: $"{source}:chunk-{chunks.Count}",
                Content: chunkText,
                Metadata: new ChunkMetadata(
                    Source: source,
                    DocumentType: documentType,
                    StartOffset: chunkStartLine,
                    EndOffset: lineOffset - 1,
                    IngestedAt: DateTimeOffset.UtcNow))
            {
                TokenCount = _tokenCounter.CountTokens(chunkText)
            });
        }

        return chunks;
    }
}
