namespace Koshi.Core.Tokenization;

/// <summary>
/// Light Porter-style English stemmer for BM25 indexing (issue #27).
/// <para>
/// Implements the classic Porter (1980) algorithm steps that give the
/// biggest recall lift on programmer-prose corpora: plurals, past tense,
/// nominalization (-ation/-tion/-ment), and final-e cleanup. Deliberately
/// stops short of Porter2's exceptional-word table to keep the code small,
/// allocation-free, and trivially AOT-safe.
/// </para>
/// <para>
/// Both the indexing side and query side of <c>KeywordRetriever</c> share
/// this implementation so a query for <c>authentication</c> will match
/// indexed terms <c>authenticate</c>, <c>authenticating</c>, <c>authenticated</c>,
/// <c>authentications</c>, etc.
/// </para>
/// <para>Opt-out: set <c>KOSHI_BM25_STEMMING=off</c>.</para>
/// </summary>
public static class EnglishStemmer
{
    /// <summary>
    /// Stem an already-lowercased ASCII word. Words shorter than 3 chars
    /// and words containing non-ASCII-letter characters are returned as-is.
    /// </summary>
    public static string Stem(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 3)
            return word;

        for (int i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (c < 'a' || c > 'z') return word;
        }

        var b = new char[word.Length + 1];
        word.CopyTo(0, b, 0, word.Length);
        int k = word.Length - 1;

        k = Step1a(b, k);
        k = Step1b(b, k);
        k = Step1c(b, k);
        k = Step2(b, k);
        k = Step3(b, k);
        k = Step4(b, k);
        k = Step5(b, k);

        return new string(b, 0, k + 1);
    }

    private static bool IsVowel(char[] b, int i)
    {
        var c = b[i];
        if (c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u') return true;
        if (c != 'y') return false;
        return i == 0 || !IsVowel(b, i - 1);
    }

    /// <summary>True if b[0..k] contains a vowel.</summary>
    private static bool ContainsVowel(char[] b, int k)
    {
        for (int i = 0; i <= k; i++)
            if (IsVowel(b, i)) return true;
        return false;
    }

    /// <summary>True if b[k-1] and b[k] are the same consonant.</summary>
    private static bool DoubleConsonant(char[] b, int k)
    {
        if (k < 1 || b[k] != b[k - 1]) return false;
        return !IsVowel(b, k);
    }

    /// <summary>Measures the number of consonant sequences between 0 and k.</summary>
    private static int M(char[] b, int k)
    {
        int n = 0;
        int i = 0;
        while (true)
        {
            if (i > k) return n;
            if (!IsVowel(b, i)) i++;
            else break;
        }
        i++;
        while (true)
        {
            while (true)
            {
                if (i > k) return n;
                if (IsVowel(b, i)) i++;
                else break;
            }
            i++;
            n++;
            while (true)
            {
                if (i > k) return n;
                if (!IsVowel(b, i)) i++;
                else break;
            }
            i++;
        }
    }

    /// <summary>cvc pattern: consonant-vowel-consonant where last consonant is not w, x, or y.</summary>
    private static bool Cvc(char[] b, int k)
    {
        if (k < 2 || IsVowel(b, k) || IsVowel(b, k - 1) is false && k == 0) return false;
        if (k < 2) return false;
        if (IsVowel(b, k)) return false;
        if (!IsVowel(b, k - 1)) return false;
        if (IsVowel(b, k - 2)) return false;
        var c = b[k];
        if (c == 'w' || c == 'x' || c == 'y') return false;
        return true;
    }

    private static bool EndsWith(char[] b, int k, string s)
    {
        var len = s.Length;
        if (len > k + 1) return false;
        var off = k - len + 1;
        for (int i = 0; i < len; i++)
            if (b[off + i] != s[i]) return false;
        return true;
    }

    private static int Replace(char[] b, int k, string suffix, string replacement)
    {
        var off = k - suffix.Length + 1;
        for (int i = 0; i < replacement.Length; i++)
            b[off + i] = replacement[i];
        return off + replacement.Length - 1;
    }

    private static int Step1a(char[] b, int k)
    {
        if (b[k] == 's')
        {
            if (EndsWith(b, k, "sses")) k -= 2;        // caresses -> caress
            else if (EndsWith(b, k, "ies")) k -= 2;    // ponies   -> poni
            else if (k > 0 && b[k - 1] != 's') k -= 1; // cats     -> cat
        }
        return k;
    }

    private static int Step1b(char[] b, int k)
    {
        bool removedEdIng = false;

        if (EndsWith(b, k, "eed"))
        {
            if (M(b, k - 3) > 0) k -= 1;               // feed -> feed; agreed -> agree
        }
        else if (EndsWith(b, k, "ed") && ContainsVowel(b, k - 2))
        {
            k -= 2;
            removedEdIng = true;
        }
        else if (EndsWith(b, k, "ing") && ContainsVowel(b, k - 3))
        {
            k -= 3;
            removedEdIng = true;
        }

        if (removedEdIng)
        {
            if (EndsWith(b, k, "at")) { b[k + 1] = 'e'; k += 1; }            // conflat(ed) -> conflate
            else if (EndsWith(b, k, "bl")) { b[k + 1] = 'e'; k += 1; }       // troubl(ed)  -> trouble
            else if (EndsWith(b, k, "iz")) { b[k + 1] = 'e'; k += 1; }       // siz(ed)     -> size
            else if (DoubleConsonant(b, k))
            {
                var c = b[k];
                if (c != 'l' && c != 's' && c != 'z') k -= 1;                // hopp(ing)   -> hop
            }
            else if (M(b, k) == 1 && Cvc(b, k))
            {
                b[k + 1] = 'e'; k += 1;                                      // fail(ed)    -> fail; hop -> hope? -> handled above
            }
        }
        return k;
    }

    private static int Step1c(char[] b, int k)
    {
        if (k > 0 && b[k] == 'y' && ContainsVowel(b, k - 1))
            b[k] = 'i';
        return k;
    }

    private static int TryR(char[] b, int k, string suffix, string replacement)
    {
        if (!EndsWith(b, k, suffix)) return k;
        var off = k - suffix.Length;
        if (M(b, off) <= 0) return k;
        return Replace(b, k, suffix, replacement);
    }

    private static int Step2(char[] b, int k)
    {
        if (k < 1) return k;
        switch (b[k - 1])
        {
            case 'a':
                if ((k = TryR(b, k, "ational", "ate")) != k) break;
                k = TryR(b, k, "tional", "tion"); break;
            case 'c':
                if ((k = TryR(b, k, "enci", "ence")) != k) break;
                k = TryR(b, k, "anci", "ance"); break;
            case 'e':
                k = TryR(b, k, "izer", "ize"); break;
            case 'l':
                if ((k = TryR(b, k, "bli", "ble")) != k) break;
                if ((k = TryR(b, k, "alli", "al")) != k) break;
                if ((k = TryR(b, k, "entli", "ent")) != k) break;
                if ((k = TryR(b, k, "eli", "e")) != k) break;
                k = TryR(b, k, "ousli", "ous"); break;
            case 'o':
                if ((k = TryR(b, k, "ization", "ize")) != k) break;
                if ((k = TryR(b, k, "ation", "ate")) != k) break;
                k = TryR(b, k, "ator", "ate"); break;
            case 's':
                if ((k = TryR(b, k, "alism", "al")) != k) break;
                if ((k = TryR(b, k, "iveness", "ive")) != k) break;
                if ((k = TryR(b, k, "fulness", "ful")) != k) break;
                k = TryR(b, k, "ousness", "ous"); break;
            case 't':
                if ((k = TryR(b, k, "aliti", "al")) != k) break;
                if ((k = TryR(b, k, "iviti", "ive")) != k) break;
                k = TryR(b, k, "biliti", "ble"); break;
        }
        return k;
    }

    private static int Step3(char[] b, int k)
    {
        switch (b[k])
        {
            case 'e':
                if ((k = TryR(b, k, "icate", "ic")) != k) break;
                if ((k = TryR(b, k, "ative", "")) != k) break;
                k = TryR(b, k, "alize", "al"); break;
            case 'i':
                k = TryR(b, k, "iciti", "ic"); break;
            case 'l':
                if ((k = TryR(b, k, "ical", "ic")) != k) break;
                k = TryR(b, k, "ful", ""); break;
            case 's':
                k = TryR(b, k, "ness", ""); break;
        }
        return k;
    }

    private static int Step4Try(char[] b, int k, string suffix)
    {
        if (!EndsWith(b, k, suffix)) return k;
        var off = k - suffix.Length;
        if (M(b, off) <= 1) return k;
        return off;
    }

    private static int Step4(char[] b, int k)
    {
        if (k < 1) return k;
        switch (b[k - 1])
        {
            case 'a':
                k = Step4Try(b, k, "al"); break;
            case 'c':
                if ((k = Step4Try(b, k, "ance")) != k) break;
                k = Step4Try(b, k, "ence"); break;
            case 'e':
                k = Step4Try(b, k, "er"); break;
            case 'i':
                k = Step4Try(b, k, "ic"); break;
            case 'l':
                if ((k = Step4Try(b, k, "able")) != k) break;
                k = Step4Try(b, k, "ible"); break;
            case 'n':
                if ((k = Step4Try(b, k, "ant")) != k) break;
                if ((k = Step4Try(b, k, "ement")) != k) break;
                if ((k = Step4Try(b, k, "ment")) != k) break;
                k = Step4Try(b, k, "ent"); break;
            case 'o':
                if (EndsWith(b, k, "ion") && k >= 3 && (b[k - 3] == 's' || b[k - 3] == 't'))
                {
                    var off = k - 3;
                    if (M(b, off) > 1) k = off;
                }
                else
                {
                    k = Step4Try(b, k, "ou");
                }
                break;
            case 's':
                k = Step4Try(b, k, "ism"); break;
            case 't':
                if ((k = Step4Try(b, k, "ate")) != k) break;
                k = Step4Try(b, k, "iti"); break;
            case 'u':
                k = Step4Try(b, k, "ous"); break;
            case 'v':
                k = Step4Try(b, k, "ive"); break;
            case 'z':
                k = Step4Try(b, k, "ize"); break;
        }
        return k;
    }

    private static int Step5(char[] b, int k)
    {
        if (k > 0 && b[k] == 'e')
        {
            var a = M(b, k);
            if (a > 1 || (a == 1 && !Cvc(b, k - 1))) k -= 1;
        }
        if (k > 0 && b[k] == 'l' && DoubleConsonant(b, k) && M(b, k) > 1)
            k -= 1;
        return k;
    }
}
