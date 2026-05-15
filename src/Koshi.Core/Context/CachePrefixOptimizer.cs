namespace Koshi.Core.Context;

using Koshi.Core.Tokenization;

/// <summary>
/// Optimizes the stable prefix of context windows for prompt caching.
/// Prompt caching (Anthropic, OpenAI) saves 50-90% of input cost when
/// the first N tokens are identical across calls. This class ensures
/// that cacheable content forms a stable, consistent prefix.
/// 
/// Key insight: Put STABLE content first (system prompt + team conventions),
/// DYNAMIC content after. The longer the stable prefix, the higher the cache hit rate.
/// </summary>
public sealed class CachePrefixOptimizer
{
    private readonly TokenCounter _tokenCounter;
    private string? _lastPrefix;
    private int _callCount;
    private int _cacheHits;

    public CachePrefixOptimizer(TokenCounter tokenCounter)
    {
        _tokenCounter = tokenCounter;
    }

    /// <summary>Estimated cache hit rate based on prefix stability.</summary>
    public float CacheHitRate => _callCount > 0 ? (float)_cacheHits / _callCount : 0f;

    /// <summary>Total calls tracked.</summary>
    public int CallCount => _callCount;

    /// <summary>
    /// Optimize a compiled context by ensuring the cacheable prefix is stable.
    /// Returns the context unchanged but tracks cache hit metrics.
    /// </summary>
    public CompiledContext OptimizeAndTrack(CompiledContext context)
    {
        _callCount++;
        var currentPrefix = context.CacheablePrefix();

        if (_lastPrefix is not null && currentPrefix == _lastPrefix)
            _cacheHits++;

        _lastPrefix = currentPrefix;
        return context;
    }

    /// <summary>
    /// Build a stable system prompt that maximizes cache hits.
    /// Combines static instructions with stable team context.
    /// </summary>
    public string BuildStablePrefix(string systemPrompt, string? teamContext = null)
    {
        if (teamContext is null) return systemPrompt;

        // Combine into a single cacheable block — order must never change
        return $"{systemPrompt}\n\n---\n\n{teamContext}";
    }

    /// <summary>
    /// Estimate cost savings from prompt caching at a given hit rate.
    /// </summary>
    public CacheSavingsEstimate EstimateSavings(
        int prefixTokens,
        int queriesPerDay,
        decimal inputPricePerMToken = 2.50m, // GPT-4o-mini pricing
        float cacheDiscount = 0.5f)         // 50% discount for cached tokens
    {
        decimal dailyCostWithoutCache = queriesPerDay * prefixTokens / 1_000_000m * inputPricePerMToken;
        decimal dailyCostWithCache = queriesPerDay * prefixTokens / 1_000_000m * inputPricePerMToken *
            (1 - (decimal)(CacheHitRate * cacheDiscount));
        decimal dailySavings = dailyCostWithoutCache - dailyCostWithCache;

        return new CacheSavingsEstimate(
            PrefixTokens: prefixTokens,
            EstimatedCacheHitRate: CacheHitRate,
            DailyCostWithoutCache: dailyCostWithoutCache,
            DailyCostWithCache: dailyCostWithCache,
            DailySavings: dailySavings,
            AnnualSavings: dailySavings * 365);
    }

    /// <summary>
    /// Analyze prefix stability across multiple contexts.
    /// Returns the longest common prefix that remains stable.
    /// </summary>
    public int FindStablePrefixLength(IReadOnlyList<CompiledContext> contexts)
    {
        if (contexts.Count < 2) return 0;

        var prefixes = contexts.Select(c => c.CacheablePrefix()).ToList();
        var first = prefixes[0];
        int stableLength = first.Length;

        for (int i = 1; i < prefixes.Count; i++)
        {
            stableLength = Math.Min(stableLength, prefixes[i].Length);
            for (int j = 0; j < stableLength; j++)
            {
                if (first[j] != prefixes[i][j])
                {
                    stableLength = j;
                    break;
                }
            }
        }

        return _tokenCounter.CountTokens(first[..stableLength]);
    }
}

/// <summary>
/// Estimated savings from prompt caching.
/// </summary>
public sealed record CacheSavingsEstimate(
    int PrefixTokens,
    float EstimatedCacheHitRate,
    decimal DailyCostWithoutCache,
    decimal DailyCostWithCache,
    decimal DailySavings,
    decimal AnnualSavings);
