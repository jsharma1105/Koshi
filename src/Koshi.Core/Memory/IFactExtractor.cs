namespace Koshi.Core.Memory;

/// <summary>
/// Extracts structured facts from interaction text.
/// </summary>
public interface IFactExtractor
{
    Task<ExtractionResult> ExtractAsync(
        string interaction, MemoryScope scope, string source, CancellationToken ct = default);
}
