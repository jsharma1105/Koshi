using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Parses the <c>memories</c> argument supplied to <c>koshi_compile_context</c>.
///
/// Three input shapes are recognized, in priority order:
///
/// <list type="number">
/// <item>
///   <description>
///     <b>JSON array</b> of <c>{type, subject, content, confidence}</c> objects —
///     the preferred structured form. Each element becomes one memory section.
///   </description>
/// </item>
/// <item>
///   <description>
///     <b>Raw <c>koshi_recall</c> output</b> — automatically detected by the
///     presence of one or more lines matching the recall entry header shape
///     <c>"  [Type] Subject (score: X, confidence: Y%)"</c>. Each entry
///     (header line plus its following continuation lines, up to the next
///     entry header) becomes one memory section.
///   </description>
/// </item>
/// <item>
///   <description>
///     <b>Plain text</b> — anything else is treated as <b>one</b> memory
///     section verbatim. Embedded newlines are preserved. This deliberately
///     does not split on blank lines: callers who need multiple distinct
///     memories should use the JSON form or pass the raw recall output.
///   </description>
/// </item>
/// </list>
///
/// Fixes issue #60: the previous implementation split on every newline,
/// fragmenting recall output into one section per line.
/// </summary>
internal static class MemoryInputParser
{
    /// <summary>
    /// Recognizes a single <c>koshi_recall</c> entry header anchored to the
    /// start of a line. The four known memory types are enumerated explicitly
    /// so a content line like <c>"  [0] value"</c> or <c>"  [TODO] fix it"</c>
    /// will not be misinterpreted as an entry boundary.
    /// </summary>
    private static readonly Regex s_recallEntryHeader = new(
        @"^  \[(Fact|Decision|Pattern|Preference)\] .+ \(score: [^)]+, confidence: [^)]+\)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Parse <paramref name="raw"/> into one or more memory blocks. Never
    /// returns <c>null</c>. Returns an empty list when the input is null,
    /// whitespace, or contains no parseable content.
    /// </summary>
    public static List<ParsedMemory> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        // 1. JSON array of structured memory inputs.
        if (TryParseJson(raw, out var fromJson))
            return fromJson;

        // 2. Raw koshi_recall output.
        if (TryParseRecallOutput(raw, out var fromRecall))
            return fromRecall;

        // 3. Plain text — whole input is one memory.
        return [new ParsedMemory(raw.Trim(), null, null, null)];
    }

    private static bool TryParseJson(string raw, out List<ParsedMemory> result)
    {
        result = [];
        var trimmed = raw.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '[')
            return false;

        // Only attempt JSON parse when the first non-whitespace char after '['
        // is '{' — guards against recall lines like "[Decision] foo" being
        // misread as malformed JSON. If it starts with '[{' but is genuinely
        // malformed, we still fall through to plain-text fallback so the user
        // sees their input preserved as one section rather than silently
        // disappearing.
        var afterBracket = trimmed.AsSpan(1).TrimStart();
        if (afterBracket.Length == 0 || afterBracket[0] != '{')
            return false;

        try
        {
            var items = JsonSerializer.Deserialize(
                trimmed,
                KoshiJsonContext.Default.ListParsedMemoryInput);
            if (items is null || items.Count == 0)
                return false;
            foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.Content)))
            {
                result.Add(new ParsedMemory(
                    Content: item.Content!.Trim(),
                    Type: item.Type,
                    Subject: item.Subject,
                    Confidence: item.Confidence));
            }
            return result.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseRecallOutput(string raw, out List<ParsedMemory> result)
    {
        result = [];
        var matches = s_recallEntryHeader.Matches(raw);
        if (matches.Count == 0)
            return false;

        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;
            int end = (i + 1 < matches.Count) ? matches[i + 1].Index : raw.Length;
            var block = raw[start..end].TrimEnd();
            if (block.Length == 0) continue;

            // Pull Type / Subject / confidence out of the header for the
            // structured fields. If the regex matched we know the header
            // exists at the start of `block`.
            var firstLineEnd = block.IndexOf('\n');
            var headerLine = firstLineEnd >= 0 ? block[..firstLineEnd] : block;
            var (type, subject, confidence) = ExtractHeaderFields(headerLine);

            result.Add(new ParsedMemory(
                Content: block,
                Type: type,
                Subject: subject,
                Confidence: confidence));
        }
        return result.Count > 0;
    }

    private static (MemoryType? Type, string? Subject, float? Confidence) ExtractHeaderFields(string headerLine)
    {
        var m = Regex.Match(headerLine,
            @"^\s*\[(?<type>Fact|Decision|Pattern|Preference)\]\s+(?<subject>.+?)\s+\(score:\s*[^,]+,\s*confidence:\s*(?<conf>[\d.]+)\s*%\)");
        if (!m.Success) return (null, null, null);

        MemoryType? type = Enum.TryParse<MemoryType>(m.Groups["type"].Value, out var t) ? t : null;
        string subject = m.Groups["subject"].Value.Trim();
        float? confidence = float.TryParse(m.Groups["conf"].Value, System.Globalization.CultureInfo.InvariantCulture, out var c)
            ? c / 100f
            : null;
        return (type, subject, confidence);
    }
}

/// <summary>
/// Parsed memory section ready to be packed into a context window.
/// </summary>
/// <param name="Content">The body of the memory as it should appear in the prompt. For recall-format input this is the full multi-line block (header + content + scope line).</param>
/// <param name="Type">Structured type when known (recall-format or JSON input); null for plain text.</param>
/// <param name="Subject">Structured subject when known; null for plain text.</param>
/// <param name="Confidence">Confidence in [0,1] when known; null for plain text.</param>
internal sealed record ParsedMemory(string Content, MemoryType? Type, string? Subject, float? Confidence);

/// <summary>JSON-deserialized memory input used by <see cref="MemoryInputParser"/>.</summary>
internal sealed class ParsedMemoryInput
{
    public MemoryType? Type { get; set; }
    public string? Subject { get; set; }
    public string Content { get; set; } = "";
    public float? Confidence { get; set; }
}
