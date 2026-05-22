using System.Text;

namespace Koshi.Mcp.Internal;

/// <summary>
/// ASCII-only kebab-case slug generator for memory filenames. Designed for both Windows
/// and POSIX filesystems: drops every char outside [a-z0-9], collapses runs of '-',
/// caps length, and rewrites reserved Windows names.
/// </summary>
internal static class Slug
{
    private const int MaxLength = 64;
    private const string Fallback = "untitled";

    // Lowercased because we lowercase before checking.
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public static string Make(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return Fallback;

        var sb = new StringBuilder(subject.Length);
        foreach (var ch in subject.Select(c => (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c))
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                sb.Append(ch);
            else
                sb.Append('-');
        }

        var collapsed = CollapseDashes(sb);
        if (collapsed.Length == 0) return Fallback;

        if (collapsed.Length > MaxLength)
        {
            collapsed = collapsed[..MaxLength].TrimEnd('-');
            if (collapsed.Length == 0) return Fallback;
        }

        if (ReservedWindowsNames.Contains(collapsed)) collapsed += "-note";

        return collapsed;
    }

    private static string CollapseDashes(StringBuilder input)
    {
        var sb = new StringBuilder(input.Length);
        bool prevDash = false;
        for (int i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '-')
            {
                if (!prevDash && sb.Length > 0) sb.Append('-');
                prevDash = true;
            }
            else
            {
                sb.Append(ch);
                prevDash = false;
            }
        }
        // Trim trailing '-'
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.ToString();
    }
}
