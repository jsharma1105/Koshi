using System.Diagnostics.CodeAnalysis;

namespace Koshi.Mcp.Cli;

/// <summary>
/// Minimal, strict dotenv parser used by <c>koshi-mcp config apply</c>.
/// <para>
/// Supported grammar (one entry per line):
/// </para>
/// <list type="bullet">
///   <item>Blank lines and lines whose first non-whitespace character is <c>#</c> are ignored.</item>
///   <item><c>KEY=VALUE</c> — key matches <c>[A-Za-z_][A-Za-z0-9_]*</c>.</item>
///   <item><c>KEY="value with spaces"</c> and <c>KEY='value'</c> — surrounding quotes are stripped.</item>
///   <item><c>KEY=</c> — explicit empty value (allowed).</item>
///   <item>Trailing whitespace after unquoted values is stripped.</item>
/// </list>
/// <para>Explicitly NOT supported:</para>
/// <list type="bullet">
///   <item><c>export KEY=VALUE</c></item>
///   <item>Multiline values</item>
///   <item>Variable expansion (<c>$OTHER</c>)</item>
///   <item>Escape sequences inside quoted values</item>
/// </list>
/// Unsupported lines fail the whole parse with a line-numbered error.
/// </summary>
internal static class DotEnvParser
{
    public static bool TryParse(
        IEnumerable<string> lines,
        [NotNullWhen(true)] out Dictionary<string, string>? values,
        [NotNullWhen(false)] out string? error)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int lineNo = 0;

        foreach (var raw in lines)
        {
            lineNo++;
            var line = raw.TrimEnd('\r');
            var stripped = line.TrimStart();
            if (stripped.Length == 0 || stripped[0] == '#')
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                values = null;
                error = $"line {lineNo}: expected KEY=VALUE, got '{line}'";
                return false;
            }

            var key = line[..eq].TrimEnd();
            if (!IsValidKey(key))
            {
                values = null;
                error = $"line {lineNo}: invalid key '{key}' (must match [A-Za-z_][A-Za-z0-9_]*)";
                return false;
            }

            if (key.StartsWith("export ", StringComparison.Ordinal))
            {
                values = null;
                error = $"line {lineNo}: 'export' prefix is not supported";
                return false;
            }

            var rawValue = line[(eq + 1)..];
            if (!TryUnquoteValue(rawValue, out var value, out var valueError))
            {
                values = null;
                error = $"line {lineNo}: {valueError}";
                return false;
            }

            result[key] = value;
        }

        values = result;
        error = null;
        return true;
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length == 0) return false;
        var first = key[0];
        if (!(char.IsLetter(first) || first == '_')) return false;
        for (int i = 1; i < key.Length; i++)
        {
            var c = key[i];
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    private static bool TryUnquoteValue(string raw, out string value, [NotNullWhen(false)] out string? error)
    {
        value = "";
        error = null;
        var v = raw.TrimStart();
        if (v.Length == 0) { return true; }

        if (v[0] is '"' or '\'')
        {
            char quote = v[0];
            int close = v.IndexOf(quote, 1);
            if (close < 0)
            {
                error = $"unterminated {quote} quote";
                return false;
            }
            var trailing = v[(close + 1)..].Trim();
            if (trailing.Length > 0 && trailing[0] != '#')
            {
                error = $"unexpected trailing characters after closing quote: '{trailing}'";
                return false;
            }
            value = v[1..close];
            return true;
        }

        var commentIdx = v.IndexOf(" #", StringComparison.Ordinal);
        var body = commentIdx >= 0 ? v[..commentIdx] : v;
        value = body.TrimEnd();
        return true;
    }
}
