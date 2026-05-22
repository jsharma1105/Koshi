using System.Text.RegularExpressions;

namespace Koshi.Core.Memory;

/// <summary>
/// Heuristic extractor for decision-shape sentences in a turn summary.
///
/// Pattern-based and deterministic (no LLM), so it stays AOT-friendly
/// and predictable. Designed for the <c>koshi_capture_turn</c> MCP tool
/// (see GitHub issue #46) — the agent passes a paragraph summary at the
/// end of a meaningful turn, and this extractor pulls out the sentences
/// that look like decisions so they can be persisted as
/// <see cref="MemoryType.Decision"/> records.
///
/// Patterns are intentionally high-precision (we'd rather miss a decision
/// than auto-capture a question or a piece of chit-chat). Recall can be
/// raised in a follow-up either by adding patterns or by layering an
/// LLM-based extractor on top.
/// </summary>
public static class DecisionExtractor
{
    /// <summary>One decision-shape sentence found in a turn summary.</summary>
    public sealed record DecisionCandidate(
        string Subject,
        string Body,
        float Confidence,
        string MatchedPattern);

    // Order matters: patterns are evaluated top-to-bottom, and a sentence
    // keeps the highest-confidence pattern that matched. Each regex must
    // include word boundaries / anchors so we don't pick up substrings
    // inside identifiers or URLs.
    private static readonly (Regex Pattern, float Confidence, string Name)[] _patterns =
    [
        // "Decision: <text>" — explicit, strongest signal.
        (new Regex(@"(?im)^\s*decision\s*:\s*\S",
            RegexOptions.CultureInvariant | RegexOptions.Compiled), 0.95f, "explicit-marker"),

        // "<X> over <Y> because <Z>" — comparative with rationale.
        (new Regex(@"(?i)\b\w[\w\-\.]*\s+over\s+\w[\w\-\.]*\b[^.!?]{0,120}\bbecause\b",
            RegexOptions.CultureInvariant | RegexOptions.Compiled), 0.9f, "comparative-with-rationale"),

        // "we / I / the team + chose / decided / picked / went with / opted for / going with"
        (new Regex(@"(?i)\b(?:we|i|the\s+team|team)\b\s+(?:chose|decided|picked|went\s+with|opted\s+for|going\s+with|are\s+going\s+with)\b\s+\S",
            RegexOptions.CultureInvariant | RegexOptions.Compiled), 0.8f, "first-person-decision"),

        // "fixed by / resolved by / worked around with" — resolution memory.
        (new Regex(@"(?i)\b(?:fixed|resolved|patched|worked\s+around)\s+by\b\s+\S",
            RegexOptions.CultureInvariant | RegexOptions.Compiled), 0.75f, "resolution"),

        // Plain "decided to ..." / "chose to ..." — verb-led, no subject required.
        (new Regex(@"(?i)\b(?:decided|chose|picked|opted)\s+to\b\s+\S",
            RegexOptions.CultureInvariant | RegexOptions.Compiled), 0.7f, "decided-to"),
    ];

    // Sentence boundary on .!? followed by whitespace, OR blank-line gaps.
    // The lookbehind keeps the terminator with the prior sentence so the
    // "?" suffix check below works.
    private static readonly Regex _sentenceSplitter = new(
        @"(?<=[.!?])\s+|(?:\r?\n){2,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex _decisionMarkerStrip = new(
        @"(?i)^\s*decision\s*:\s*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Extract decision-shape candidates from a turn summary, ordered by
    /// confidence descending. Returns at most <paramref name="maxCandidates"/>
    /// entries. Empty input -> empty list.
    /// </summary>
    public static IReadOnlyList<DecisionCandidate> Extract(string turnSummary, int maxCandidates = 5)
    {
        if (string.IsNullOrWhiteSpace(turnSummary) || maxCandidates <= 0)
            return Array.Empty<DecisionCandidate>();

        var sentences = _sentenceSplitter
            .Split(turnSummary)
            .Select(s => s.Trim())
            .Where(IsCandidateSentence);

        var candidates = new List<DecisionCandidate>();
        foreach (var sentence in sentences)
        {
            DecisionCandidate? best = null;
            foreach (var (pattern, conf, name) in _patterns)
            {
                if (!pattern.IsMatch(sentence)) continue;
                if (best is null || conf > best.Confidence)
                    best = new DecisionCandidate(DeriveSubject(sentence), sentence, conf, name);
            }
            if (best is not null) candidates.Add(best);
        }

        return candidates
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.Body, StringComparer.Ordinal)
            .Take(maxCandidates)
            .ToList();
    }

    private static bool IsCandidateSentence(string sentence)
    {
        // Reject very short fragments and questions outright. Questions
        // sometimes contain decision verbs ("should we go with X?") but
        // they aren't decisions yet, and capturing them would mislead a
        // future reader.
        if (sentence.Length < 20) return false;
        if (sentence.EndsWith('?')) return false;
        return true;
    }

    private static string DeriveSubject(string sentence)
    {
        var clean = _decisionMarkerStrip.Replace(sentence, "").TrimEnd('.', ',', ';', ' ');
        const int maxSubjectLength = 80;
        if (clean.Length <= maxSubjectLength) return clean;
        return clean[..maxSubjectLength].TrimEnd() + "...";
    }
}
