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
        // "Decision: <text>" — explicit, strongest signal. Require an
        // English-looking tail (≥3 letters) so log lines like
        // "Decision: 200 OK was returned ..." don't capture as 0.95 garbage.
        (new Regex(@"(?im)^\s*decision\s*:\s*[A-Za-z]{3,}",
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

    // Sentence boundary on .!? followed by whitespace, OR blank-line gaps,
    // OR a newline followed by a list marker (-, •, *, 1., 1)). Real-world
    // agent summaries are usually bullet lists, and without splitting on
    // list markers a multi-bullet summary collapses into one "sentence"
    // and only the highest-confidence pattern survives, silently dropping
    // the other decisions in the list.
    private static readonly Regex _sentenceSplitter = new(
        @"(?<=[.!?])\s+|(?:\r?\n){2,}|(?:\r?\n)\s*(?:[\-•*]|\d+[.)])\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex _decisionMarkerStrip = new(
        @"(?i)^\s*decision\s*:\s*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Negation / hypothetical / aspirational / quotation cues. When any of
    // these is near the decision verb, the sentence is *not* expressing a
    // decision the team made — it's a counterfactual ("if we had chosen X"),
    // a regret ("we should have chosen X"), a denial ("we did NOT choose X"),
    // or a reference to external prose ("per the docs, you choose X"). The
    // comparative-with-rationale pattern would otherwise capture these as
    // 0.9 confidence Decisions.
    private static readonly Regex _negationOrHypothetical = new(
        @"(?i)\b(?:did\s+not|didn'?t|do\s+not|don'?t|doesn'?t|won'?t|wouldn'?t|never|" +
        @"not\s+(?:choose|chose|chosen|pick|picked|decide|decided|opt|opted)|" +
        @"should\s+have|could\s+have|would\s+have|" +
        @"if\s+(?:we|i|the\s+team|they|you)\s+(?:had|were|chose|choose|pick|picked|decide|decided)|" +
        @"might\s+have|may\s+have|hypothetically|" +
        @"per\s+the\s+docs?|the\s+docs?\s+say|the\s+article\s+says|" +
        @"according\s+to)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Leading list-marker / bullet glyph at start of a sentence after splitting.
    // The splitter break the line *at* the marker, but if it's at the very
    // start of the input (no preceding newline) it survives.
    private static readonly Regex _leadingBullet = new(
        @"^\s*(?:[\-•*]|\d+[.)])\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex _whitespaceRun = new(
        @"\s+",
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
            .Select(s => _leadingBullet.Replace(s.Trim(), ""))
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
        // Reject negated / hypothetical / quotation forms. "We did NOT
        // choose X over Y because of Z" matches comparative-with-rationale
        // at 0.9 confidence, but it's a denial, not a decision.
        if (_negationOrHypothetical.IsMatch(sentence)) return false;
        return true;
    }

    private static string DeriveSubject(string sentence)
    {
        // Order matters: strip the leading bullet/list marker that the
        // splitter may have left in place, then strip an explicit
        // "Decision:" prefix, then collapse runs of internal whitespace
        // so two agents writing the same decision with different
        // whitespace produce identical subject strings (dedupe stability).
        var clean = _leadingBullet.Replace(sentence, "");
        clean = _decisionMarkerStrip.Replace(clean, "");
        clean = _whitespaceRun.Replace(clean, " ").TrimEnd('.', ',', ';', ' ');
        const int maxSubjectLength = 80;
        if (clean.Length <= maxSubjectLength) return clean;
        return clean[..maxSubjectLength].TrimEnd() + "...";
    }
}
