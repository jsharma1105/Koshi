using System.Globalization;
using System.Security;
using System.Text;
using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Read/write a vault-mode memory file: one Markdown document per memory with a
/// strict <c>koshi:</c> YAML frontmatter block plus a free-form body.
/// </summary>
/// <remarks>
/// This is NOT a general-purpose YAML parser. It parses only the precise subset of
/// keys Koshi writes inside the <c>koshi:</c> block and rejects files with unknown
/// keys in that block (so we never silently lose data on rewrite). Everything
/// outside the <c>koshi:</c> block is preserved verbatim as opaque text — including
/// user-defined frontmatter keys like <c>tags:</c>, <c>aliases:</c>, <c>cssclass:</c>.
/// </remarks>
internal sealed class VaultDocument
{
    public required MemoryRecord Record { get; init; }
    public required string OtherFrontmatterText { get; init; }

    // Required koshi: keys for a v0.6.0 file
    private static readonly HashSet<string> KoshiTopLevelKnown = new(StringComparer.Ordinal)
    {
        "id", "type", "scope", "source", "confidence",
        "created-at", "updated-at", "last-accessed-at",
        "access-count", "tier",
        "superseded-by", "contradiction-note",
    };

    private static readonly HashSet<string> KoshiScopeKnown = new(StringComparer.Ordinal)
    {
        "user", "workspace", "thread",
    };

    public static VaultDocument? Read(string path)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or SecurityException
                or NotSupportedException)
        {
            Console.Error.WriteLine($"[koshi] Failed to read '{path}': {ex.Message}");
            return null;
        }

        if (!TrySplitFrontmatter(text, out var fmText, out var body))
            return null; // No frontmatter at all → treat as unmanaged.

        if (!TryExtractKoshiBlock(fmText, out var koshiBlock, out var otherFmText))
            return null; // No koshi: block → unmanaged user note.

        if (!TryParseKoshiBlock(koshiBlock, path, out var data))
            return null; // Malformed; warning already emitted.

        var subject = ExtractH1(body) ?? FilenameToSubjectFallback(path);
        var content = StripFirstH1(body).Trim();

        try
        {
            var record = data.ToRecord(subject, content);
            return new VaultDocument { Record = record, OtherFrontmatterText = otherFmText };
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or FormatException
                or OverflowException)
        {
            Console.Error.WriteLine($"[koshi] Failed to materialize record from '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Atomically write <paramref name="record"/> to <paramref name="path"/>, preserving
    /// the given non-<c>koshi:</c> frontmatter block (which may be empty).
    /// </summary>
    public static void Write(string path, MemoryRecord record, string otherFrontmatterText)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.Append("---\n");
        AppendKoshiBlock(sb, record);
        if (!string.IsNullOrEmpty(otherFrontmatterText))
        {
            sb.Append(otherFrontmatterText);
            if (!otherFrontmatterText.EndsWith('\n')) sb.Append('\n');
        }
        sb.Append("---\n");
        sb.Append("# ").Append(record.Subject).Append('\n');
        sb.Append('\n');
        sb.Append(record.Content);
        if (record.Content.Length > 0 && !record.Content.EndsWith('\n')) sb.Append('\n');

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, sb.ToString());
        File.Move(tempPath, path, overwrite: true);
    }

    // ---------- frontmatter splitting ----------

    private static bool TrySplitFrontmatter(string text, out string frontmatter, out string body)
    {
        frontmatter = string.Empty;
        body = text;

        if (!text.StartsWith("---\n") && !text.StartsWith("---\r\n"))
            return false;

        int startOffset = text.StartsWith("---\r\n") ? 5 : 4;
        // Find the next line that is exactly "---" (followed by \n or end of file).
        int searchFrom = startOffset;
        while (searchFrom < text.Length)
        {
            int lineStart = searchFrom;
            int lineEnd = text.IndexOf('\n', lineStart);
            string line = lineEnd < 0
                ? text[lineStart..]
                : text[lineStart..lineEnd];
            // Strip trailing \r
            if (line.EndsWith('\r')) line = line[..^1];

            if (line == "---")
            {
                frontmatter = text.Substring(startOffset, lineStart - startOffset);
                body = lineEnd < 0 ? "" : text[(lineEnd + 1)..];
                return true;
            }

            if (lineEnd < 0) break;
            searchFrom = lineEnd + 1;
        }

        return false;
    }

    // ---------- koshi: block extraction ----------

    private static bool TryExtractKoshiBlock(string frontmatter, out List<string> koshiLines, out string otherFmText)
    {
        koshiLines = [];
        var other = new StringBuilder();

        var lines = SplitLines(frontmatter);
        int i = 0;
        bool foundKoshi = false;

        // Walk pre-koshi lines into other.
        while (i < lines.Count)
        {
            var line = lines[i].Content;
            if (line == "koshi:")
            {
                foundKoshi = true;
                koshiLines.Add(line);
                i++;
                break;
            }
            other.Append(lines[i].Raw);
            i++;
        }

        if (!foundKoshi)
        {
            otherFmText = "";
            return false;
        }

        // Walk indented lines into koshi block.
        while (i < lines.Count)
        {
            var line = lines[i].Content;
            if (line.Length == 0)
            {
                // Blank lines: treat as koshi-block-continuation only if they're not at the END
                // of the block. We'll handle by lookahead.
                int j = i + 1;
                while (j < lines.Count && lines[j].Content.Length == 0) j++;
                if (j < lines.Count && (lines[j].Content.StartsWith(' ') || lines[j].Content.StartsWith('\t')))
                {
                    // Blank line inside koshi block → keep as part of block.
                    koshiLines.Add(line);
                    i++;
                    continue;
                }
                // Trailing blanks belong to "other".
                break;
            }
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                koshiLines.Add(line);
                i++;
            }
            else break;
        }

        // Walk post-koshi lines into other.
        while (i < lines.Count)
        {
            other.Append(lines[i].Raw);
            i++;
        }

        otherFmText = other.ToString();
        return true;
    }

    // ---------- koshi: block parser ----------

    private sealed class KoshiData
    {
        public string Id = "";
        public string Type = "";
        public string ScopeUser = "";
        public string ScopeWorkspace = "default";
        public string? ScopeThread;
        public string Source = "";
        public float Confidence;
        public DateTimeOffset CreatedAt;
        public DateTimeOffset? UpdatedAt;
        public DateTimeOffset LastAccessedAt;
        public int AccessCount;
        public string Tier = "Hot";
        public string? SupersededBy;
        public string? ContradictionNote;

        public MemoryRecord ToRecord(string subject, string content)
        {
            return new MemoryRecord
            {
                Id = Id,
                Type = Enum.Parse<MemoryType>(Type, ignoreCase: true),
                Content = content,
                Subject = subject,
                Scope = new MemoryScope(ScopeUser, ScopeWorkspace, ScopeThread),
                Source = Source,
                Confidence = Confidence,
                CreatedAt = CreatedAt,
                LastAccessedAt = LastAccessedAt,
                AccessCount = AccessCount,
                Tier = Enum.Parse<MemoryTier>(Tier, ignoreCase: true),
                SupersededBy = SupersededBy,
                ContradictionNote = ContradictionNote,
            };
        }
    }

    private static bool TryParseKoshiBlock(List<string> lines, string path, out KoshiData data)
    {
        data = new KoshiData();
        if (lines.Count == 0 || lines[0] != "koshi:")
        {
            Warn(path, "missing 'koshi:' header");
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool inScope = false;
        var scopeSeen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;

            int indent = CountLeadingSpaces(line);
            var rest = line[indent..];

            if (indent == 2)
            {
                inScope = false;
                if (rest == "scope:")
                {
                    if (!seen.Add("scope"))
                    {
                        Warn(path, "duplicate 'scope:' key");
                        return false;
                    }
                    inScope = true;
                    continue;
                }

                int colon = rest.IndexOf(':');
                if (colon < 0)
                {
                    Warn(path, $"expected 'key: value' at line {i + 1}");
                    return false;
                }

                var key = rest[..colon];
                var raw = rest[(colon + 1)..].TrimStart();
                if (!KoshiTopLevelKnown.Contains(key))
                {
                    Warn(path, $"unknown koshi key '{key}' — refusing to rewrite this file");
                    return false;
                }
                if (!seen.Add(key))
                {
                    Warn(path, $"duplicate koshi key '{key}'");
                    return false;
                }

                if (!ApplyTopLevel(data, key, raw, path)) return false;
            }
            else if (indent == 4 && inScope)
            {
                int colon = rest.IndexOf(':');
                if (colon < 0)
                {
                    Warn(path, $"expected 'key: value' in scope at line {i + 1}");
                    return false;
                }
                var key = rest[..colon];
                var raw = rest[(colon + 1)..].TrimStart();
                if (!KoshiScopeKnown.Contains(key))
                {
                    Warn(path, $"unknown scope key '{key}'");
                    return false;
                }
                if (!scopeSeen.Add(key))
                {
                    Warn(path, $"duplicate scope key '{key}'");
                    return false;
                }
                ApplyScope(data, key, raw);
            }
            else
            {
                Warn(path, $"unexpected indentation at line {i + 1} (indent={indent})");
                return false;
            }
        }

        // Validate required keys.
        string[] requiredTop = ["id", "type", "scope", "source", "confidence", "created-at", "last-accessed-at", "access-count", "tier"];
        var missingTop = requiredTop.FirstOrDefault(req => !seen.Contains(req));
        if (missingTop is not null)
        {
            Warn(path, $"missing required koshi key '{missingTop}'");
            return false;
        }
        string[] requiredScope = ["user", "workspace"];
        var missingScope = requiredScope.FirstOrDefault(req => !scopeSeen.Contains(req));
        if (missingScope is not null)
        {
            Warn(path, $"missing required scope key '{missingScope}'");
            return false;
        }

        return true;
    }

    private static bool ApplyTopLevel(KoshiData data, string key, string raw, string path)
    {
        try
        {
            switch (key)
            {
                case "id":
                    data.Id = ParseScalarString(raw);
                    return true;
                case "type":
                    data.Type = ParseScalarString(raw);
                    return true;
                case "source":
                    data.Source = ParseScalarString(raw);
                    return true;
                case "confidence":
                    data.Confidence = float.Parse(raw, CultureInfo.InvariantCulture);
                    return true;
                case "created-at":
                    data.CreatedAt = ParseTimestamp(raw);
                    return true;
                case "updated-at":
                    data.UpdatedAt = ParseTimestamp(raw);
                    return true;
                case "last-accessed-at":
                    data.LastAccessedAt = ParseTimestamp(raw);
                    return true;
                case "access-count":
                    data.AccessCount = int.Parse(raw, CultureInfo.InvariantCulture);
                    return true;
                case "tier":
                    data.Tier = ParseScalarString(raw);
                    return true;
                case "superseded-by":
                    data.SupersededBy = ParseScalarOrNull(raw);
                    return true;
                case "contradiction-note":
                    data.ContradictionNote = ParseScalarOrNull(raw);
                    return true;
                default:
                    Warn(path, $"unhandled key '{key}' (internal error)");
                    return false;
            }
        }
        catch (Exception ex) when (
            ex is FormatException or ArgumentException or OverflowException
                or InvalidOperationException)
        {
            Warn(path, $"could not parse '{key}': {ex.Message}");
            return false;
        }
    }

    private static void ApplyScope(KoshiData data, string key, string raw)
    {
        switch (key)
        {
            case "user":
                data.ScopeUser = ParseScalarString(raw);
                break;
            case "workspace":
                data.ScopeWorkspace = ParseScalarString(raw);
                break;
            case "thread":
                data.ScopeThread = ParseScalarOrNull(raw);
                break;
        }
    }

    // ---------- scalar parsing ----------

    private static string ParseScalarString(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return "";
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            return UnescapeDoubleQuoted(raw[1..^1]);
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            return raw[1..^1].Replace("''", "'");
        return raw;
    }

    private static string? ParseScalarOrNull(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return null;
        if (raw == "null" || raw == "~") return null;
        return ParseScalarString(raw);
    }

    private static DateTimeOffset ParseTimestamp(string raw)
    {
        var s = ParseScalarString(raw);
        return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private static string UnescapeDoubleQuoted(string inner)
    {
        var sb = new StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                char next = inner[i + 1];
                sb.Append(next switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => next,
                });
                i++;
            }
            else sb.Append(inner[i]);
        }
        return sb.ToString();
    }

    // ---------- emit ----------

    private static void AppendKoshiBlock(StringBuilder sb, MemoryRecord r)
    {
        sb.Append("koshi:\n");
        sb.Append("  id: ").Append(YamlString(r.Id)).Append('\n');
        sb.Append("  type: ").Append(r.Type).Append('\n');
        sb.Append("  scope:\n");
        sb.Append("    user: ").Append(YamlString(r.Scope.UserId)).Append('\n');
        sb.Append("    workspace: ").Append(YamlString(r.Scope.WorkspaceId)).Append('\n');
        sb.Append("    thread: ").Append(r.Scope.ThreadId is null ? "null" : YamlString(r.Scope.ThreadId)).Append('\n');
        sb.Append("  source: ").Append(YamlString(r.Source)).Append('\n');
        sb.Append("  confidence: ").Append(r.Confidence.ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("  created-at: ").Append(r.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("  updated-at: ").Append(DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("  last-accessed-at: ").Append(r.LastAccessedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("  access-count: ").Append(r.AccessCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("  tier: ").Append(r.Tier).Append('\n');
        if (r.SupersededBy is not null)
            sb.Append("  superseded-by: ").Append(YamlString(r.SupersededBy)).Append('\n');
        if (r.ContradictionNote is not null)
            sb.Append("  contradiction-note: ").Append(YamlString(r.ContradictionNote)).Append('\n');
    }

    private static string YamlString(string? s)
    {
        if (s is null) return "null";
        if (s.Length == 0) return "\"\"";
        if (NeedsQuoting(s))
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        return s;
    }

    private static bool NeedsQuoting(string s)
    {
        if (s.Equals("null", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("no", StringComparison.OrdinalIgnoreCase)) return true;
        if (s[0] == ' ' || s[^1] == ' ') return true;
        if (s[0] is '!' or '&' or '*' or '%' or '@' or '`' or '#' or '|' or '>' or '?' or '-') return true;
        return s.Any(c => c is ':' or '"' or '\'' or '[' or ']' or '{' or '}' or ',' or '#' or '\n' or '\r' or '\t' or '\\');
    }

    // ---------- body parsing ----------

    private static string? ExtractH1(string body)
    {
        int idx = 0;
        while (idx < body.Length)
        {
            // Find start of next line.
            int lineEnd = body.IndexOf('\n', idx);
            string line = lineEnd < 0 ? body[idx..] : body[idx..lineEnd];
            if (line.EndsWith('\r')) line = line[..^1];
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                if (trimmed.StartsWith("# ") && !trimmed.StartsWith("## "))
                    return trimmed[2..].Trim();
                return null;
            }
            if (lineEnd < 0) break;
            idx = lineEnd + 1;
        }
        return null;
    }

    private static string StripFirstH1(string body)
    {
        int idx = 0;
        while (idx < body.Length)
        {
            int lineEnd = body.IndexOf('\n', idx);
            string line = lineEnd < 0 ? body[idx..] : body[idx..lineEnd];
            string lineNoCr = line.EndsWith('\r') ? line[..^1] : line;
            var trimmed = lineNoCr.Trim();
            if (trimmed.Length == 0)
            {
                if (lineEnd < 0) return body;
                idx = lineEnd + 1;
                continue;
            }
            if (trimmed.StartsWith("# ") && !trimmed.StartsWith("## "))
            {
                int removeFrom = idx;
                int removeTo = lineEnd < 0 ? body.Length : lineEnd + 1;
                // Also skip one blank line directly after the H1, if present.
                if (removeTo < body.Length)
                {
                    int nextEnd = body.IndexOf('\n', removeTo);
                    var nextLine = nextEnd < 0 ? body[removeTo..] : body[removeTo..nextEnd];
                    if (nextLine.TrimEnd('\r').Trim().Length == 0)
                        removeTo = nextEnd < 0 ? body.Length : nextEnd + 1;
                }
                return body[..removeFrom] + body[removeTo..];
            }
            return body;
        }
        return body;
    }

    private static string FilenameToSubjectFallback(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        int idx = name.LastIndexOf("--mem-", StringComparison.Ordinal);
        if (idx > 0) name = name[..idx];
        return name.Replace('-', ' ').Trim();
    }

    // ---------- line splitting (preserves raw line bytes incl. trailing \n) ----------

    private readonly record struct RawLine(string Content, string Raw);

    private static List<RawLine> SplitLines(string text)
    {
        var lines = new List<RawLine>();
        int start = 0;
        while (start < text.Length)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                var content = text[start..];
                lines.Add(new RawLine(content, content));
                break;
            }
            var raw = text[start..(nl + 1)];
            var ct = raw[..^1];
            if (ct.EndsWith('\r')) ct = ct[..^1];
            lines.Add(new RawLine(ct, raw));
            start = nl + 1;
        }
        return lines;
    }

    private static int CountLeadingSpaces(string s)
    {
        int n = 0;
        while (n < s.Length && s[n] == ' ') n++;
        return n;
    }

    private static void Warn(string path, string message)
    {
        Console.Error.WriteLine($"[koshi] Malformed memory file '{path}': {message}");
    }
}
