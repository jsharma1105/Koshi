using Koshi.Core.Tokenization;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for <see cref="TokenCounters"/> — the process-wide shared counter
/// that consolidated duplicate <c>Lazy&lt;TokenCounter&gt;</c> fields in
/// the MCP layer (issue #29).
/// </summary>
public sealed class TokenCountersTests
{
    [Fact]
    public void Shared_returns_a_usable_counter()
    {
        // Smoke test: real cl100k_base vocab loads, tokens come out non-zero.
        var c = TokenCounters.Shared;
        Assert.NotNull(c);
        Assert.True(c.CountTokens("hello world") > 0);
    }

    [Fact]
    public void Shared_is_singleton_across_calls()
    {
        // Both accesses must return the same instance — that's the whole
        // point of the consolidation in issue #29.
        var a = TokenCounters.Shared;
        var b = TokenCounters.Shared;
        Assert.Same(a, b);
    }

    [Fact]
    public void ModelName_defaults_to_gpt_4_when_env_unset()
    {
        // Process env var should not be polluted; the default applies.
        var prior = Environment.GetEnvironmentVariable("KOSHI_TOKENIZER_MODEL");
        try
        {
            Environment.SetEnvironmentVariable("KOSHI_TOKENIZER_MODEL", null);
            Assert.Equal("gpt-4", TokenCounters.ModelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KOSHI_TOKENIZER_MODEL", prior);
        }
    }

    [Fact]
    public void ModelName_reads_env_var_when_set()
    {
        var prior = Environment.GetEnvironmentVariable("KOSHI_TOKENIZER_MODEL");
        try
        {
            Environment.SetEnvironmentVariable("KOSHI_TOKENIZER_MODEL", "gpt-4o");
            Assert.Equal("gpt-4o", TokenCounters.ModelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KOSHI_TOKENIZER_MODEL", prior);
        }
    }
}
