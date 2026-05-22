using Koshi.Mcp.Tools;

namespace Koshi.Core.Tests;

/// <summary>
/// Phase 1 of issue #23: multi-corpus retrieval.
///
/// Acceptance criteria covered:
/// - Two koshi_index calls with different `corpus` names retain both corpora.
/// - koshi_search routes to the requested corpus; defaults preserve behavior.
/// - koshi_list_indexed() lists every corpus.
/// - koshi_clear_index("&lt;name&gt;") clears one.
/// - koshi_clear_index("*") clears all.
///
/// Tests target the named-corpus path only (in-memory, no persistence
/// side effects on the snapshot file). The default-corpus path goes
/// through the same code as the existing test suite.
/// </summary>
public sealed class MultiCorpusTests : IDisposable
{
    private const string CorpusA = "multi-corpus-test-A";
    private const string CorpusB = "multi-corpus-test-B";

    public void Dispose()
    {
        // Each test must leave the static registry empty so we don't bleed
        // state into unrelated test cases.
        RetrievalTools.ClearIndex(CorpusA);
        RetrievalTools.ClearIndex(CorpusB);
    }

    [Fact]
    public void Two_named_corpora_coexist_independently()
    {
        var docsA = """[{"content":"apple banana cherry","source":"a.md","type":"document"}]""";
        var docsB = """[{"content":"xylophone yam zebra","source":"b.md","type":"document"}]""";

        var resA = RetrievalTools.Index(docsA, corpus: CorpusA);
        var resB = RetrievalTools.Index(docsB, corpus: CorpusB);

        Assert.StartsWith("✅", resA);
        Assert.StartsWith("✅", resB);
        Assert.Contains($"corpus={CorpusA}", resA);
        Assert.Contains($"corpus={CorpusB}", resB);
    }

    [Fact]
    public void Search_routes_to_named_corpus()
    {
        RetrievalTools.Index("""[{"content":"apple banana cherry","source":"a.md","type":"document"}]""", corpus: CorpusA);
        RetrievalTools.Index("""[{"content":"xylophone yam zebra","source":"b.md","type":"document"}]""", corpus: CorpusB);

        var hitA = RetrievalTools.Search("apple", topK: 5, corpus: CorpusA);
        var missA = RetrievalTools.Search("zebra", topK: 5, corpus: CorpusA);
        var hitB = RetrievalTools.Search("zebra", topK: 5, corpus: CorpusB);

        Assert.Contains("a.md", hitA);
        Assert.Contains($"corpus={CorpusA}", hitA);
        Assert.Contains("No results", missA);
        Assert.Contains("b.md", hitB);
    }

    [Fact]
    public void Search_unknown_corpus_returns_helpful_error()
    {
        var res = RetrievalTools.Search("anything", topK: 5, corpus: "no-such-corpus-zzz");
        Assert.StartsWith("❌", res);
        Assert.Contains("Unknown corpus", res);
    }

    [Fact]
    public void List_indexed_shows_named_corpora()
    {
        RetrievalTools.Index("""[{"content":"apple","source":"a.md","type":"document"}]""", corpus: CorpusA);
        RetrievalTools.Index("""[{"content":"zebra","source":"b.md","type":"document"}]""", corpus: CorpusB);

        var list = RetrievalTools.ListIndexed(corpus: null);
        Assert.Contains(CorpusA, list);
        Assert.Contains(CorpusB, list);
    }

    [Fact]
    public void Clear_single_named_corpus_keeps_others()
    {
        RetrievalTools.Index("""[{"content":"apple","source":"a.md","type":"document"}]""", corpus: CorpusA);
        RetrievalTools.Index("""[{"content":"zebra","source":"b.md","type":"document"}]""", corpus: CorpusB);

        var clearA = RetrievalTools.ClearIndex(corpus: CorpusA);
        Assert.Contains(CorpusA, clearA);

        var listAfter = RetrievalTools.ListIndexed(corpus: null);
        Assert.DoesNotContain(CorpusA, listAfter);
        Assert.Contains(CorpusB, listAfter);
    }

    [Fact]
    public void Clear_star_removes_all_named_corpora()
    {
        RetrievalTools.Index("""[{"content":"apple","source":"a.md","type":"document"}]""", corpus: CorpusA);
        RetrievalTools.Index("""[{"content":"zebra","source":"b.md","type":"document"}]""", corpus: CorpusB);

        var clearStar = RetrievalTools.ClearIndex(corpus: "*");
        Assert.Contains("named corpora", clearStar);

        // Both should be gone now.
        Assert.StartsWith("❌", RetrievalTools.Search("apple", corpus: CorpusA));
        Assert.StartsWith("❌", RetrievalTools.Search("zebra", corpus: CorpusB));
    }

    [Fact]
    public void Default_corpus_path_is_unchanged_when_corpus_omitted()
    {
        // Sanity: the existing call shape (no corpus arg) still routes
        // through the default-corpus path. We can't fully exercise it here
        // because it touches snapshot persistence; we just verify the call
        // is accepted and returns the success marker.
        var existing = RetrievalTools.Index("""[{"content":"hello","source":"x.md","type":"document"}]""");
        Assert.StartsWith("✅", existing);
        Assert.Contains($"corpus={RetrievalTools_DefaultCorpusNameProbe.GetDefaultName()}", existing);
    }

    /// <summary>
    /// Tiny helper that reads the internal default-corpus constant via
    /// InternalsVisibleTo so the test stays in sync with the source.
    /// </summary>
    internal static class RetrievalTools_DefaultCorpusNameProbe
    {
        public static string GetDefaultName() => RetrievalTools.DefaultCorpusName;
    }
}
