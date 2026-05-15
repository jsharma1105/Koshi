namespace Koshi.Core.Tokenization;

using Microsoft.ML.Tokenizers;

/// <summary>
/// Token counter using BPE tokenization. Wraps Microsoft.ML.Tokenizers
/// to provide token counting for budget management.
/// </summary>
public sealed class TokenCounter
{
    private readonly Tokenizer _tokenizer;

    private TokenCounter(Tokenizer tokenizer) => _tokenizer = tokenizer;

    public static Task<TokenCounter> CreateAsync(string modelName = "gpt-4")
    {
        // Map model names to encodings explicitly
        var encoding = modelName.ToLowerInvariant() switch
        {
            "gpt-4o" or "gpt-4o-mini" => "o200k_base",
            _ => "cl100k_base" // GPT-4, GPT-3.5, embeddings
        };
        var tokenizer = TiktokenTokenizer.CreateForEncoding(encoding);
        return Task.FromResult(new TokenCounter(tokenizer));
    }

    public int CountTokens(string text) => _tokenizer.CountTokens(text);

    public IReadOnlyList<string> Tokenize(string text)
    {
        var encoded = _tokenizer.EncodeToTokens(text, out _);
        return encoded.Select(t => t.Value).ToList();
    }

    /// <summary>
    /// Shows token breakdown with IDs — useful for understanding cost.
    /// </summary>
    public IReadOnlyList<(string Token, int Id)> TokenizeWithIds(string text)
    {
        var encoded = _tokenizer.EncodeToTokens(text, out _);
        return encoded.Select(t => (t.Value, t.Id)).ToList();
    }

    /// <summary>
    /// Calculates the token-to-character ratio — higher means more "expensive" text.
    /// Prose ~0.25, Code ~0.40, JSON ~0.50
    /// </summary>
    public double TokenCharRatio(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (double)CountTokens(text) / text.Length;
    }
}
