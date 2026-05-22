using Koshi.Core.Tokenization;

namespace Koshi.Core.Tests;

/// <summary>
/// Coverage for <see cref="EnglishStemmer"/>. The cases below are the
/// well-known Porter (1980) test vectors plus extras for vocabulary that
/// shows up in programmer prose (auth/run/index/etc.).
/// </summary>
public sealed class EnglishStemmerTests
{
    [Theory]
    // plurals
    [InlineData("caresses", "caress")]
    [InlineData("ponies", "poni")]
    [InlineData("ties", "ti")]
    [InlineData("caress", "caress")]
    [InlineData("cats", "cat")]
    // -ed / -ing
    [InlineData("feed", "feed")]
    [InlineData("agreed", "agre")]
    [InlineData("plastered", "plaster")]
    [InlineData("bled", "bled")]
    [InlineData("motoring", "motor")]
    [InlineData("sing", "sing")]
    // -ize / -ate / -ble fixup
    [InlineData("conflated", "conflat")]
    [InlineData("troubled", "troubl")]
    [InlineData("sized", "size")]
    [InlineData("hopping", "hop")]
    [InlineData("tanned", "tan")]
    [InlineData("falling", "fall")]
    [InlineData("hissing", "hiss")]
    [InlineData("fizzed", "fizz")]
    [InlineData("failing", "fail")]
    [InlineData("filing", "file")]
    // y → i
    [InlineData("happy", "happi")]
    [InlineData("sky", "sky")]
    // step 2 / 3 / 4
    [InlineData("relational", "relat")]
    [InlineData("conditional", "condit")]
    [InlineData("rational", "ration")]
    [InlineData("valenci", "valenc")]
    [InlineData("hesitanci", "hesit")]
    [InlineData("digitizer", "digit")]
    [InlineData("conformabli", "conform")]
    [InlineData("radicalli", "radic")]
    [InlineData("differentli", "differ")]
    [InlineData("vileli", "vile")]
    [InlineData("analogousli", "analog")]
    [InlineData("vietnamization", "vietnam")]
    [InlineData("predication", "predic")]
    [InlineData("operator", "oper")]
    [InlineData("feudalism", "feudal")]
    [InlineData("decisiveness", "decis")]
    [InlineData("hopefulness", "hope")]
    [InlineData("callousness", "callous")]
    [InlineData("formaliti", "formal")]
    [InlineData("sensitiviti", "sensit")]
    [InlineData("sensibiliti", "sensibl")]
    // engineering-flavored vocab
    [InlineData("running", "run")]
    [InlineData("runs", "run")]
    [InlineData("indexing", "index")]
    [InlineData("indexed", "index")]
    [InlineData("indexes", "index")]
    [InlineData("retrieve", "retriev")]
    [InlineData("retrieving", "retriev")]
    [InlineData("retrieves", "retriev")]
    [InlineData("retrieval", "retriev")]
    public void Stem_known_pairs(string input, string expected)
    {
        Assert.Equal(expected, EnglishStemmer.Stem(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    public void Short_words_pass_through(string input)
    {
        Assert.Equal(input, EnglishStemmer.Stem(input));
    }

    [Theory]
    [InlineData("CamelCase")]   // has uppercase — out of scope, return as-is
    [InlineData("hello-world")] // hyphen — pre-split by tokenizer; stemmer leaves alone
    [InlineData("foo123")]      // digit — return as-is
    public void Non_lowercase_or_non_ascii_returns_input(string input)
    {
        Assert.Equal(input, EnglishStemmer.Stem(input));
    }

    [Fact]
    public void Authentication_family_collapses()
    {
        // Issue #27 acceptance: queries for "authentication" should hit
        // documents that say "authenticate" / "authenticating".
        var authentication = EnglishStemmer.Stem("authentication");
        var authenticate = EnglishStemmer.Stem("authenticate");
        var authenticating = EnglishStemmer.Stem("authenticating");
        var authenticated = EnglishStemmer.Stem("authenticated");

        // They all share the same root after stemming — exact form not asserted
        // (Porter does not guarantee linguistic correctness, only equivalence).
        Assert.Equal(authentication, authenticate);
        Assert.Equal(authentication, authenticating);
        Assert.Equal(authentication, authenticated);
    }
}
