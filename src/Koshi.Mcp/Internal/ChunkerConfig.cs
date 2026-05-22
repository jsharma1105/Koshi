namespace Koshi.Mcp.Internal;

/// <summary>
/// Resolves chunker token settings (<c>maxTokens</c>, <c>overlapTokens</c>)
/// from explicit tool args → env-var defaults → built-in defaults, with
/// validation and clamping (issue #26).
///
/// <para>
/// Precedence per setting:
/// </para>
/// <list type="number">
///   <item>Explicit tool argument (when not <c>null</c>).</item>
///   <item><c>KOSHI_CHUNK_MAX_TOKENS</c> / <c>KOSHI_CHUNK_OVERLAP_TOKENS</c> env vars (when parseable).</item>
///   <item>Built-in defaults (<see cref="BuiltinMaxTokens"/>, <see cref="BuiltinOverlapTokens"/>).</item>
/// </list>
///
/// <para>
/// Out-of-range values are <em>clamped</em> rather than rejected — the
/// chunker still does the indexing run, but a one-line warning is included
/// in the resolver result so callers can surface it to the user.
/// </para>
///
/// <para>
/// Hard limits:
/// </para>
/// <list type="bullet">
///   <item><c>maxTokens</c> ∈ <c>[64, 2048]</c></item>
///   <item><c>overlapTokens</c> ∈ <c>[0, 256]</c> AND <c>&lt; maxTokens / 2</c></item>
/// </list>
/// </summary>
internal sealed record ChunkerConfig(int MaxTokens, int OverlapTokens, string Source, string? Warning)
{
    public const int BuiltinMaxTokens = 512;
    public const int BuiltinOverlapTokens = 50;

    public const int MinMaxTokens = 64;
    public const int MaxMaxTokens = 2048;
    public const int MinOverlapTokens = 0;
    public const int MaxOverlapTokens = 256;

    /// <summary>
    /// Resolve chunker settings. Caller-supplied <c>null</c> means "use env
    /// var or default for this knob" — pass concrete values to force them.
    /// </summary>
    public static ChunkerConfig Resolve(int? maxTokens, int? overlapTokens, Func<string, string?>? envReader = null)
    {
        envReader ??= Environment.GetEnvironmentVariable;
        var warnings = new List<string>();
        var sources = new List<string>();

        var (rawMax, maxSource) = ResolveOne(maxTokens, "KOSHI_CHUNK_MAX_TOKENS", BuiltinMaxTokens, envReader);
        var (rawOverlap, overlapSource) = ResolveOne(overlapTokens, "KOSHI_CHUNK_OVERLAP_TOKENS", BuiltinOverlapTokens, envReader);

        var effMax = rawMax;
        if (effMax < MinMaxTokens)
        {
            warnings.Add($"maxTokens={rawMax} too small (min {MinMaxTokens}); clamped to {MinMaxTokens}.");
            effMax = MinMaxTokens;
        }
        else if (effMax > MaxMaxTokens)
        {
            warnings.Add($"maxTokens={rawMax} too large (max {MaxMaxTokens}); clamped to {MaxMaxTokens}.");
            effMax = MaxMaxTokens;
        }

        var effOverlap = rawOverlap;
        if (effOverlap < MinOverlapTokens)
        {
            warnings.Add($"overlapTokens={rawOverlap} too small; clamped to {MinOverlapTokens}.");
            effOverlap = MinOverlapTokens;
        }
        else if (effOverlap > MaxOverlapTokens)
        {
            warnings.Add($"overlapTokens={rawOverlap} too large (max {MaxOverlapTokens}); clamped to {MaxOverlapTokens}.");
            effOverlap = MaxOverlapTokens;
        }

        var overlapCap = effMax / 2;
        if (effOverlap >= overlapCap)
        {
            warnings.Add($"overlapTokens={effOverlap} must be < maxTokens/2 ({overlapCap}); clamped.");
            effOverlap = Math.Max(0, overlapCap - 1);
        }

        sources.Add($"max:{maxSource}");
        sources.Add($"overlap:{overlapSource}");

        return new ChunkerConfig(
            effMax,
            effOverlap,
            string.Join(",", sources),
            warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    private static (int Value, string Source) ResolveOne(int? explicitVal, string envName, int builtin, Func<string, string?> envReader)
    {
        if (explicitVal is int v)
            return (v, "arg");

        var raw = envReader(envName);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var parsed))
            return (parsed, "env");

        return (builtin, "default");
    }

    /// <summary>One-line summary suitable for tool response output.</summary>
    public string Describe() => $"chunker(maxTokens={MaxTokens}, overlapTokens={OverlapTokens}, source={Source})";
}
