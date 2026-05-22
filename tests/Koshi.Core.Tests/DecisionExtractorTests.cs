using Koshi.Core.Memory;

namespace Koshi.Core.Tests;

/// <summary>
/// Coverage for <see cref="DecisionExtractor"/> (issue #46).
///
/// The extractor backs the <c>koshi_capture_turn</c> MCP tool, so these
/// tests double as the contract spec: any new pattern category should
/// add both a positive theory case and a negative theory case here.
/// </summary>
public sealed class DecisionExtractorTests
{
    [Fact]
    public void Extract_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(DecisionExtractor.Extract(""));
        Assert.Empty(DecisionExtractor.Extract("   \n\n   "));
        Assert.Empty(DecisionExtractor.Extract(null!));
    }

    [Fact]
    public void Extract_ZeroMaxCandidates_ReturnsEmpty()
    {
        var result = DecisionExtractor.Extract(
            "We chose retry-with-backoff over circuit-breaker because dep recovers fast.",
            maxCandidates: 0);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("Decision: switch to Dapper for the hot path.", "explicit-marker")]
    [InlineData("We chose retry-with-backoff over circuit-breaker because dep recovers fast.", "comparative-with-rationale")]
    [InlineData("We decided to use managed identities for all linked services.", "first-person-decision")]
    [InlineData("The team chose Logseq for the team-shared vault.", "first-person-decision")]
    [InlineData("Fixed by upgrading the Azure SDK to 9.0.313.", "resolution")]
    [InlineData("Resolved by reverting commit abc123 and bumping the cache TTL.", "resolution")]
    [InlineData("Decided to roll back the migration until the schema lock issue is fixed.", "decided-to")]
    public void Extract_KnownDecisionPatterns_AreDetected(string sentence, string expectedPattern)
    {
        var result = DecisionExtractor.Extract(sentence);
        Assert.NotEmpty(result);
        Assert.Equal(expectedPattern, result[0].MatchedPattern);
    }

    [Theory]
    [InlineData("How should we handle retries for the foo service?")]
    [InlineData("Should we choose X over Y for the cache layer?")]
    [InlineData("Could we have decided to use a different SDK here?")]
    [InlineData("It's a sunny day in Seattle today.")]
    [InlineData("The function takes two arguments and returns a Result.")]
    [InlineData("Hi.")]
    [InlineData("Yes.")]
    [InlineData("Looking at the logs.")]
    public void Extract_NonDecisionSentences_AreIgnored(string sentence)
    {
        Assert.Empty(DecisionExtractor.Extract(sentence));
    }

    [Fact]
    public void Extract_MixedTurnSummary_OnlyReturnsDecisions()
    {
        var input =
            "Spent the morning chasing a deadlock in module-foo. " +
            "Reproduced it with a stress test against the staging dep. " +
            "We chose retry-with-backoff over circuit-breaker because the dep recovers within 5s. " +
            "Updated the unit tests and opened PR #1234.";

        var result = DecisionExtractor.Extract(input);

        Assert.Single(result);
        Assert.Contains("retry-with-backoff", result[0].Body);
        Assert.Equal("comparative-with-rationale", result[0].MatchedPattern);
    }

    [Fact]
    public void Extract_MultipleDecisionsInOneTurn_AreAllReturned()
    {
        var input =
            "Decision: split the migration into two phases. " +
            "We chose Dapper over EF Core because the read path is hot. " +
            "Fixed by upgrading the SDK to 9.0.313.";

        var result = DecisionExtractor.Extract(input);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Extract_RespectsMaxCandidates()
    {
        var input =
            "Decision: pick option A for the cache layer. " +
            "We chose B over C because the latency budget is tight. " +
            "Fixed by upgrading the dependency to 2.1.0. " +
            "Decided to use option E for the queue retries. " +
            "Decision: pick F for the index rebuild strategy.";

        var result = DecisionExtractor.Extract(input, maxCandidates: 2);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Extract_SortsByConfidenceDescending()
    {
        var input =
            "Decided to retry the call. " +
            "Decision: switch to retry-with-backoff. " +
            "We chose X over Y because Z is faster.";

        var result = DecisionExtractor.Extract(input);

        Assert.True(result.Count >= 2);
        for (int i = 1; i < result.Count; i++)
            Assert.True(result[i - 1].Confidence >= result[i].Confidence,
                $"Candidates not sorted by confidence: [{i - 1}]={result[i - 1].Confidence} < [{i}]={result[i].Confidence}");
    }

    [Fact]
    public void Extract_ExplicitMarker_StripsPrefixFromSubject()
    {
        var result = DecisionExtractor.Extract("Decision: switch to Dapper for the hot path.");
        Assert.Single(result);
        Assert.StartsWith("switch to Dapper", result[0].Subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Decision:", result[0].Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_LongSentence_TruncatesSubject()
    {
        var longBody = "We chose " + new string('x', 200) + " because reasons here.";
        var result = DecisionExtractor.Extract(longBody);

        Assert.NotEmpty(result);
        Assert.True(result[0].Subject.Length <= 85,
            $"Subject was {result[0].Subject.Length} chars; expected ≤85");
        Assert.EndsWith("...", result[0].Subject);
    }

    [Fact]
    public void Extract_HighestConfidencePatternWins_PerSentence()
    {
        // Sentence matches BOTH "decided to" (0.70) AND "comparative-with-rationale" (0.90)
        // — comparative should win.
        var input = "We decided to use Dapper over EF Core because the read path is hot.";
        var result = DecisionExtractor.Extract(input);

        Assert.Single(result);
        Assert.Equal("comparative-with-rationale", result[0].MatchedPattern);
    }

    [Fact]
    public void Extract_QuestionsAreFilteredOut_EvenWithDecisionVerbs()
    {
        // Each of these contains a decision verb, but the trailing ? is a strong signal
        // it's not a decision yet.
        var input =
            "Should we have chosen X over Y? " +
            "Could we decide to use a different cache? " +
            "What if we picked an alternative SDK?";

        Assert.Empty(DecisionExtractor.Extract(input));
    }

    // ─── Opus deep-review fixes ─────────────────────────────────────────

    [Theory]
    [InlineData("We did NOT choose Dapper over EF Core because we hate performance.")]
    [InlineData("We didn't choose Dapper over EF Core because of cost.")]
    [InlineData("We did not pick Dapper over EF Core because the team is fluent in EF.")]
    [InlineData("If we had chosen Dapper over EF Core because of speed, latency would drop.")]
    [InlineData("Last week we should have chosen X over Y because of perf, but we didn't.")]
    [InlineData("If only we could have decided to use Redis here.")]
    [InlineData("Per the docs, you choose X over Y because of throughput.")]
    [InlineData("According to the article, the team chose X over Y because of latency.")]
    public void Extract_NegatedOrHypothetical_IsIgnored(string sentence)
    {
        // Negation / counterfactual / aspirational / quotation forms must not
        // capture as a decision the team actually made. Without this filter
        // these sentences match comparative-with-rationale at 0.9 and silently
        // persist as Decision memories.
        var result = DecisionExtractor.Extract(sentence);
        Assert.Empty(result);
    }

    [Fact]
    public void Extract_LogLineLookalike_IsIgnored()
    {
        // "Decision: 200 OK ..." — agent pastes a log fragment whose first
        // token starts with "Decision:" but the tail is not English prose.
        // The explicit-marker pattern now requires an English-looking tail
        // (≥3 letters) so this no longer captures as a 0.95-confidence
        // garbage Decision.
        var input = "Decision: 200 OK was returned from the upstream service.";
        var result = DecisionExtractor.Extract(input);
        Assert.Empty(result);
    }

    [Fact]
    public void Extract_BulletListSummary_ReturnsAllDecisions()
    {
        // Real-world agent summaries are usually bullet lists. Before the
        // splitter fix, a multi-bullet list collapsed into one "sentence"
        // and only the highest-confidence pattern survived — silently
        // dropping the other decisions in the list.
        var input =
            "Today's outcome:\n" +
            "- We chose retry-with-backoff over circuit-breaker because the dep recovers fast\n" +
            "- Fixed by upgrading to 1.2.3\n" +
            "- We decided to use Redis for the session cache";

        var result = DecisionExtractor.Extract(input);

        Assert.Equal(3, result.Count);
        Assert.Contains(result, c => c.MatchedPattern == "comparative-with-rationale");
        Assert.Contains(result, c => c.MatchedPattern == "resolution");
        Assert.Contains(result, c => c.MatchedPattern == "first-person-decision");
    }

    [Fact]
    public void Extract_NumberedListSummary_ReturnsAllDecisions()
    {
        // Same as bullet lists but with numbered markers.
        var input =
            "Outcomes:\n" +
            "1. We chose Postgres over MySQL because of JSONB support and full text search\n" +
            "2. Fixed by raising the connection pool to 50";

        var result = DecisionExtractor.Extract(input);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Extract_BulletListSubject_StripsLeadingBulletMarker()
    {
        // When a bullet item is its own sentence the leading "- " must not
        // appear in the derived subject. Otherwise the same decision written
        // by two agents (one with a bullet, one without) dedupes as two
        // distinct memories.
        var withBullet = "- We chose Dapper over EF Core because the read path is hot.";
        var withoutBullet = "We chose Dapper over EF Core because the read path is hot.";

        var a = DecisionExtractor.Extract(withBullet);
        var b = DecisionExtractor.Extract(withoutBullet);

        Assert.Single(a);
        Assert.Single(b);
        Assert.Equal(b[0].Subject, a[0].Subject);
        Assert.DoesNotContain("-", a[0].Subject[..1]);
    }

    [Fact]
    public void Extract_CollapsesInternalWhitespaceInSubject()
    {
        // Two agents with the same decision but different whitespace
        // (tabs / multiple spaces / line wrap) must produce identical
        // subject strings so dedupe-at-write actually catches duplicates.
        var clean = "We chose Dapper over EF Core because the read path is hot.";
        var messy = "We   chose\tDapper  over\tEF Core  because the read   path is hot.";

        var a = DecisionExtractor.Extract(clean);
        var b = DecisionExtractor.Extract(messy);

        Assert.Single(a);
        Assert.Single(b);
        Assert.Equal(a[0].Subject, b[0].Subject);
    }
}
