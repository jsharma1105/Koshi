using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

public sealed class ChunkerConfigTests
{
    private static Func<string, string?> Env(IDictionary<string, string?> map)
        => key => map.TryGetValue(key, out var v) ? v : null;

    [Fact]
    public void Uses_built_in_defaults_when_no_args_no_env()
    {
        var cfg = ChunkerConfig.Resolve(null, null, Env(new Dictionary<string, string?>()));
        Assert.Equal(ChunkerConfig.BuiltinMaxTokens, cfg.MaxTokens);
        Assert.Equal(ChunkerConfig.BuiltinOverlapTokens, cfg.OverlapTokens);
        Assert.Null(cfg.Warning);
        Assert.Contains("max:default", cfg.Source);
        Assert.Contains("overlap:default", cfg.Source);
    }

    [Fact]
    public void Explicit_args_win_over_env()
    {
        var cfg = ChunkerConfig.Resolve(
            maxTokens: 1024,
            overlapTokens: 80,
            envReader: Env(new Dictionary<string, string?>
            {
                ["KOSHI_CHUNK_MAX_TOKENS"] = "256",
                ["KOSHI_CHUNK_OVERLAP_TOKENS"] = "20",
            }));

        Assert.Equal(1024, cfg.MaxTokens);
        Assert.Equal(80, cfg.OverlapTokens);
        Assert.Contains("max:arg", cfg.Source);
        Assert.Contains("overlap:arg", cfg.Source);
    }

    [Fact]
    public void Env_picked_when_arg_null()
    {
        var cfg = ChunkerConfig.Resolve(
            maxTokens: null,
            overlapTokens: null,
            envReader: Env(new Dictionary<string, string?>
            {
                ["KOSHI_CHUNK_MAX_TOKENS"] = "256",
                ["KOSHI_CHUNK_OVERLAP_TOKENS"] = "20",
            }));

        Assert.Equal(256, cfg.MaxTokens);
        Assert.Equal(20, cfg.OverlapTokens);
        Assert.Contains("max:env", cfg.Source);
    }

    [Fact]
    public void Unparseable_env_falls_back_to_default()
    {
        var cfg = ChunkerConfig.Resolve(null, null, Env(new Dictionary<string, string?>
        {
            ["KOSHI_CHUNK_MAX_TOKENS"] = "not-a-number",
        }));
        Assert.Equal(ChunkerConfig.BuiltinMaxTokens, cfg.MaxTokens);
    }

    [Fact]
    public void Max_tokens_below_floor_is_clamped()
    {
        var cfg = ChunkerConfig.Resolve(maxTokens: 10, overlapTokens: 0, envReader: Env(new Dictionary<string, string?>()));
        Assert.Equal(ChunkerConfig.MinMaxTokens, cfg.MaxTokens);
        Assert.Contains("clamped", cfg.Warning ?? "");
    }

    [Fact]
    public void Max_tokens_above_ceiling_is_clamped()
    {
        var cfg = ChunkerConfig.Resolve(maxTokens: 99_999, overlapTokens: 0, envReader: Env(new Dictionary<string, string?>()));
        Assert.Equal(ChunkerConfig.MaxMaxTokens, cfg.MaxTokens);
        Assert.Contains("clamped", cfg.Warning ?? "");
    }

    [Fact]
    public void Overlap_above_ceiling_is_clamped()
    {
        var cfg = ChunkerConfig.Resolve(maxTokens: 2048, overlapTokens: 1024, envReader: Env(new Dictionary<string, string?>()));
        Assert.Equal(ChunkerConfig.MaxOverlapTokens, cfg.OverlapTokens);
        Assert.Contains("clamped", cfg.Warning ?? "");
    }

    [Fact]
    public void Overlap_must_be_less_than_half_max()
    {
        // overlap=200, max=300 → overlap >= 150, must clamp to 149
        var cfg = ChunkerConfig.Resolve(maxTokens: 300, overlapTokens: 200, envReader: Env(new Dictionary<string, string?>()));
        Assert.Equal(300, cfg.MaxTokens);
        Assert.True(cfg.OverlapTokens < cfg.MaxTokens / 2);
        Assert.Contains("must be < maxTokens/2", cfg.Warning ?? "");
    }

    [Fact]
    public void Describe_contains_effective_values()
    {
        var cfg = ChunkerConfig.Resolve(maxTokens: 768, overlapTokens: 40, envReader: Env(new Dictionary<string, string?>()));
        var desc = cfg.Describe();
        Assert.Contains("768", desc);
        Assert.Contains("40", desc);
        Assert.Contains("max:arg", desc);
    }
}
