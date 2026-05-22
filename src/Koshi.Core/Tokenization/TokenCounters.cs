namespace Koshi.Core.Tokenization;

/// <summary>
/// Process-wide shared accessor for the <see cref="TokenCounter"/>.
///
/// <para>
/// Eliminates the duplicate <c>Lazy&lt;TokenCounter&gt;</c> fields that
/// previously lived in <c>RetrievalTools</c> and <c>ContextTools</c> — each
/// of those held its own copy of the cl100k_base vocab (~20 MB), doubling
/// startup memory + risking silent drift if one was upgraded to a new
/// encoding without the other (issue #29).
/// </para>
///
/// <para>
/// The encoding family is selected by the <c>KOSHI_TOKENIZER_MODEL</c> env
/// var at first access:
/// </para>
/// <list type="bullet">
///   <item><c>gpt-4o</c>, <c>gpt-4o-mini</c> → <c>o200k_base</c></item>
///   <item>everything else (including unset) → <c>cl100k_base</c> (GPT-4 / GPT-3.5)</item>
/// </list>
///
/// <para>
/// Init runs the first time anyone reads <see cref="Shared"/>; subsequent
/// callers reuse the same instance. Tests may use
/// <see cref="ForceForTesting(TokenCounter)"/> to inject a fake without
/// going through the real tokenizer.
/// </para>
/// </summary>
public static class TokenCounters
{
    private static Lazy<TokenCounter> _shared = new(BuildDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The shared <see cref="TokenCounter"/> for the running process.</summary>
    public static TokenCounter Shared => _shared.Value;

    /// <summary>
    /// The model name selected for the shared counter — surfaced in
    /// diagnostics so users can see which encoding is in use.
    /// </summary>
    public static string ModelName =>
        Environment.GetEnvironmentVariable("KOSHI_TOKENIZER_MODEL") is { Length: > 0 } m ? m : "gpt-4";

    private static TokenCounter BuildDefault() =>
        TokenCounter.CreateAsync(ModelName).GetAwaiter().GetResult();

    /// <summary>
    /// Test-only escape hatch: swap the shared counter with a stub. The
    /// next access to <see cref="Shared"/> returns the supplied instance.
    /// </summary>
    internal static void ForceForTesting(TokenCounter counter)
    {
        _shared = new Lazy<TokenCounter>(() => counter, LazyThreadSafetyMode.ExecutionAndPublication);
    }
}
