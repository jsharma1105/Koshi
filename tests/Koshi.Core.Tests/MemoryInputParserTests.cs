using Koshi.Core.Memory;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// Regression coverage for issue #60: koshi_compile_context fragmented the
/// memories parameter when fed raw koshi_recall output. The parser now
/// recognizes three input shapes: JSON array of structured memories,
/// raw recall output, or plain text (treated as one memory).
/// </summary>
public class MemoryInputParserTests
{
    // ── 1. Issue #60 repro — raw koshi_recall output → exactly one entry ──

    [Fact]
    public void Parse_RawRecallOutput_SingleMemory_ProducesExactlyOneEntry()
    {
        // Exact shape MemoryTools.Recall emits (see MemoryTools.cs:238-250).
        var raw = """
            ═══ Recalled 1 memories for: "auth tokens" ═══

              [Decision] auth tokens (score: 0.99, confidence: 95 %)
                The auth service uses JWT with RS256 and 1h expiry; refresh tokens are rotated.
                Scope: workspace='default' | Source: user | Stored: 12/1/2025 5:30 PM

            """;

        var parsed = MemoryInputParser.Parse(raw);

        Assert.Single(parsed);
        var only = parsed[0];
        Assert.Equal(MemoryType.Decision, only.Type);
        Assert.Equal("auth tokens", only.Subject);
        Assert.Equal(0.95f, only.Confidence!.Value, 0.001f);
        Assert.Contains("The auth service uses JWT with RS256", only.Content);
        Assert.Contains("Scope: workspace='default'", only.Content);
    }

    [Fact]
    public void Parse_RawRecallOutput_ThreeMemories_ProducesThreeEntries()
    {
        var raw = """
            ═══ Recalled 3 memories for: "auth" ═══

              [Decision] auth tokens (score: 0.99, confidence: 95 %)
                JWT RS256, 1h expiry.
                Scope: workspace='default' | Source: user | Stored: 12/1/2025 5:30 PM

              [Fact] auth library (score: 0.71, confidence: 80 %)
                We use Auth0 for SSO.
                Scope: workspace='default' | Source: user | Stored: 12/1/2025 5:31 PM

              [Pattern] session handling (score: 0.65, confidence: 90 %)
                Refresh tokens are rotated on every use.
                Scope: workspace='default' | Source: user | Stored: 12/1/2025 5:32 PM

            """;

        var parsed = MemoryInputParser.Parse(raw);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(MemoryType.Decision, parsed[0].Type);
        Assert.Equal(MemoryType.Fact, parsed[1].Type);
        Assert.Equal(MemoryType.Pattern, parsed[2].Type);
        Assert.Contains("JWT RS256", parsed[0].Content);
        Assert.Contains("Auth0", parsed[1].Content);
        Assert.Contains("Refresh tokens", parsed[2].Content);
    }

    // ── 2. JSON array — preferred structured form ──

    [Fact]
    public void Parse_JsonArray_TwoMemories_ProducesTwoEntries()
    {
        var raw = """
            [
              {"type": "Decision", "subject": "db", "content": "Postgres 16", "confidence": 0.9},
              {"type": "Fact", "subject": "team", "content": "5 backend engineers", "confidence": 0.85}
            ]
            """;

        var parsed = MemoryInputParser.Parse(raw);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(MemoryType.Decision, parsed[0].Type);
        Assert.Equal("db", parsed[0].Subject);
        Assert.Equal("Postgres 16", parsed[0].Content);
        Assert.Equal(0.9f, parsed[0].Confidence!.Value, 0.001f);
        Assert.Equal(MemoryType.Fact, parsed[1].Type);
        Assert.Equal("team", parsed[1].Subject);
    }

    [Fact]
    public void Parse_JsonArray_EmptyContent_IsSkipped()
    {
        var raw = """[{"content": ""}, {"content": "real fact"}, {"content": "   "}]""";

        var parsed = MemoryInputParser.Parse(raw);

        Assert.Single(parsed);
        Assert.Equal("real fact", parsed[0].Content);
    }

    // ── 3. Plain text — one memory, NOT split ──

    [Fact]
    public void Parse_PlainText_SingleLine_ProducesOneEntry()
    {
        var parsed = MemoryInputParser.Parse("We use Postgres 16 for the auth service.");

        Assert.Single(parsed);
        Assert.Equal("We use Postgres 16 for the auth service.", parsed[0].Content);
        Assert.Null(parsed[0].Type);
        Assert.Null(parsed[0].Subject);
    }

    [Fact]
    public void Parse_PlainText_MultiParagraph_NoSplitting()
    {
        // The previous (buggy) implementation split on every newline. The new
        // contract: plain text is ONE memory, preserved verbatim. Power users
        // wanting multiple memories should use JSON.
        var raw = "First paragraph about auth.\n\nSecond paragraph about caching.\n\nThird about deployment.";

        var parsed = MemoryInputParser.Parse(raw);

        Assert.Single(parsed);
        Assert.Equal(raw, parsed[0].Content);
    }

    // ── 4. Empty / whitespace ──

    [Fact]
    public void Parse_Null_ReturnsEmpty() => Assert.Empty(MemoryInputParser.Parse(null));

    [Fact]
    public void Parse_Empty_ReturnsEmpty() => Assert.Empty(MemoryInputParser.Parse(""));

    [Fact]
    public void Parse_Whitespace_ReturnsEmpty() => Assert.Empty(MemoryInputParser.Parse("   \n\t  "));

    // ── 5. Malformed / tricky input falls through to plain text ──

    [Fact]
    public void Parse_StartsWithBracket_ButNotJsonArray_TreatedAsPlainText()
    {
        // Input starts with '[' but the next non-whitespace char is not '{' —
        // recall-format header detection also fails (no MemoryType keyword).
        // Should fall through to plain text, NOT silently fail or split.
        var raw = "[urgent] follow up with security team next week";

        var parsed = MemoryInputParser.Parse(raw);

        Assert.Single(parsed);
        Assert.Equal(raw, parsed[0].Content);
    }

    [Fact]
    public void Parse_MalformedJson_StartsWithBracketBrace_FallsThroughToPlainText()
    {
        // Looks like JSON but is malformed; we preserve the input as a single
        // memory rather than silently dropping it.
        var raw = "[{this is not valid json}";

        var parsed = MemoryInputParser.Parse(raw);

        Assert.Single(parsed);
        Assert.Equal(raw, parsed[0].Content);
    }

    [Fact]
    public void Parse_ContentContainingBracketLine_NotMisidentifiedAsRecallEntry()
    {
        // A content line like "  [0] item" or "  [TODO] fix this" must NOT
        // be misread as a recall entry boundary — only the four real
        // MemoryType keywords with a matching (score:, confidence:) suffix
        // count as a header.
        var raw = """
            Quick note about the array layout:
              [0] header
              [1] body
              [TODO] add footer
            """;

        var parsed = MemoryInputParser.Parse(raw);

        Assert.Single(parsed);
        Assert.Contains("[0] header", parsed[0].Content);
        Assert.Contains("[TODO] add footer", parsed[0].Content);
    }

    [Fact]
    public void Parse_RecallHeader_StrictlyRequiresKnownMemoryType()
    {
        // Looks like a recall entry but the bracketed token is NOT one of the
        // four MemoryType values. Must fall through to plain text.
        var raw = "  [Note] something (score: 0.5, confidence: 50 %)";

        var parsed = MemoryInputParser.Parse(raw);

        Assert.Single(parsed);
        Assert.Equal(raw.Trim(), parsed[0].Content);
        Assert.Null(parsed[0].Type);
    }
}
