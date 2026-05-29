namespace Koshi.Mcp.Cli.Setup;

/// <summary>
/// Parsed shape of a <c>.koshi-team.yml</c> file (#78 Gap A — "one-file teammate
/// handshake"). Only known keys are accepted; anything else is a parse error so
/// typos surface immediately instead of failing silently.
/// </summary>
internal sealed record TeamYaml(
    TeamYamlTeam? Team,
    TeamYamlVault? Vault);

internal sealed record TeamYamlTeam(
    string Id,
    string? Name,
    int? TokenBudget,
    double? QualityTarget);

internal sealed record TeamYamlVault(
    string Repo,
    string Path);

/// <summary>
/// Outcome of a <see cref="TeamYamlReader.TryRead"/> call.
/// </summary>
internal sealed record TeamYamlReadResult(
    TeamYaml? Parsed,
    string? Error,
    int LineNumber);

/// <summary>
/// Hand-rolled, intentionally tiny parser for the <c>.koshi-team.yml</c>
/// vertical slice. We accept exactly the shape documented in <c>docs/init-wizard.md</c>:
///
/// <code>
/// team:
///   id: platform-eng
///   name: Platform Engineering
///   token_budget: 16000
///   quality_target: 0.75
/// vault:
///   repo: gh:my-org/koshi-vault       # or full https://... URL
///   path: .koshi/vault                # repo-relative; absolute paths refused
/// </code>
///
/// <para>
/// Why hand-rolled instead of YamlDotNet:
/// </para>
/// <list type="bullet">
///   <item>The full YAML grammar is large; we only need 5 keys, one nesting level,
///         and scalar values. A ~200-line parser stays auditable.</item>
///   <item>YamlDotNet's reflection-based deserialisation trips Koshi.Mcp's
///         <c>IsAotCompatible</c> IL2026/IL3050 errors.</item>
///   <item>Strictness — unknown keys are rejected so config typos surface
///         immediately instead of being silently ignored.</item>
/// </list>
///
/// <para>
/// What we accept: comments starting with <c>#</c>, blank lines, scalar values
/// in plain / single-quoted / double-quoted form, exactly two indentation levels
/// (root + one nested block).
/// </para>
///
/// <para>
/// What we reject: anchors, aliases, multi-doc, flow style, multi-line scalars,
/// unknown keys, anything more than one nesting level. The parser fails fast
/// with a 1-based line number; the wizard surfaces it as
/// "<c>.koshi-team.yml:7 unknown key 'cosistency_target' (did you mean
/// 'quality_target'?)</c>".
/// </para>
/// </summary>
internal static class TeamYamlReader
{
    public const string FileName = ".koshi-team.yml";

    /// <summary>
    /// Read and parse <paramref name="path"/>. Missing files return
    /// <c>Parsed=null, Error=null</c> (the wizard treats that as "no team
    /// config, prompt interactively or skip non-interactively"). Empty files
    /// produce <c>TeamYaml(Team:null, Vault:null)</c>.
    /// </summary>
    public static TeamYamlReadResult TryRead(string path)
    {
        if (!File.Exists(path))
            return new TeamYamlReadResult(null, null, 0);

        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException ex)
        {
            return new TeamYamlReadResult(null, $"could not read {path}: {ex.Message}", 0);
        }

        return Parse(text);
    }

    /// <summary>
    /// Pure-function entry point for tests. Same contract as <see cref="TryRead"/>
    /// but takes the raw YAML text directly.
    /// </summary>
    public static TeamYamlReadResult Parse(string text)
    {
        TeamYamlTeam? team = null;
        TeamYamlVault? vault = null;

        string? currentBlock = null;
        string? teamId = null, teamName = null;
        int? teamBudget = null;
        double? teamQuality = null;
        string? vaultRepo = null, vaultPath = null;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var ln = i + 1;

            // Strip trailing comment but preserve `#` inside quoted strings.
            var (logical, _) = StripComment(raw);
            if (string.IsNullOrWhiteSpace(logical)) continue;

            int indent = CountLeadingSpaces(logical);
            var trimmed = logical.TrimStart();
            // Tabs in indentation: real YAML forbids them; we do too.
            if (logical.AsSpan(0, indent).IndexOf('\t') >= 0)
                return Err(ln, "tabs are not allowed in indentation; use spaces");

            // ── Root-level key (indent == 0) ──
            if (indent == 0)
            {
                if (!trimmed.EndsWith(':'))
                    return Err(ln, $"root key must end with ':' — got '{trimmed}'");

                var key = trimmed[..^1].Trim();
                currentBlock = key switch
                {
                    "team" => "team",
                    "vault" => "vault",
                    _ => null,
                };
                if (currentBlock is null)
                    return Err(ln, $"unknown root key '{key}' (expected 'team' or 'vault')");
                continue;
            }

            // ── Nested key (indent > 0) ──
            if (currentBlock is null)
                return Err(ln, "nested value with no enclosing block (expected 'team:' or 'vault:' first)");

            // Require exactly 2 spaces of indentation.
            if (indent != 2)
                return Err(ln, $"expected 2 spaces of indentation, got {indent}");

            var (k, v, scanErr) = SplitKeyValue(trimmed);
            if (scanErr is not null) return Err(ln, scanErr);

            try
            {
                switch (currentBlock)
                {
                    case "team":
                        switch (k)
                        {
                            case "id": teamId = v; break;
                            case "name": teamName = v; break;
                            case "token_budget": teamBudget = ParseInt(v, k); break;
                            case "quality_target": teamQuality = ParseDouble(v, k); break;
                            default:
                                return Err(ln,
                                    $"unknown key 'team.{k}' (expected: id, name, token_budget, quality_target)");
                        }
                        break;
                    case "vault":
                        switch (k)
                        {
                            case "repo": vaultRepo = v; break;
                            case "path": vaultPath = v; break;
                            default:
                                return Err(ln, $"unknown key 'vault.{k}' (expected: repo, path)");
                        }
                        break;
                }
            }
            catch (FormatException ex)
            {
                return Err(ln, ex.Message);
            }
        }

        if (teamId is not null)
            team = new TeamYamlTeam(teamId, teamName, teamBudget, teamQuality);
        if (vaultRepo is not null && vaultPath is not null)
            vault = new TeamYamlVault(vaultRepo, vaultPath);
        else if (vaultRepo is not null || vaultPath is not null)
            return Err(0, "vault block requires both 'repo' and 'path'");

        return new TeamYamlReadResult(new TeamYaml(team, vault), null, 0);
    }

    private static TeamYamlReadResult Err(int line, string msg) => new(null, msg, line);

    private static (string Logical, bool HadComment) StripComment(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        char? quote = null;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote is null && ch == '#')
                return (sb.ToString(), HadComment: true);
            if (ch == '"' || ch == '\'')
            {
                if (quote is null) quote = ch;
                else if (quote == ch) quote = null;
            }
            sb.Append(ch);
        }
        return (sb.ToString(), HadComment: false);
    }

    private static int CountLeadingSpaces(string s)
    {
        int n = 0;
        while (n < s.Length && (s[n] == ' ' || s[n] == '\t')) n++;
        return n;
    }

    private static (string Key, string Value, string? Error) SplitKeyValue(string line)
    {
        int colon = line.IndexOf(':');
        if (colon < 0) return (string.Empty, string.Empty, "missing ':' in key/value");
        var key = line[..colon].Trim();
        var value = line[(colon + 1)..].Trim();
        if (key.Length == 0) return (string.Empty, string.Empty, "empty key");
        return (key, Unquote(value), null);
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 &&
            ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
        {
            return v[1..^1];
        }
        return v;
    }

    private static int ParseInt(string v, string fieldName)
    {
        if (!int.TryParse(v, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new FormatException($"value of '{fieldName}' must be an integer, got '{v}'");
        return parsed;
    }

    private static double ParseDouble(string v, string fieldName)
    {
        if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new FormatException($"value of '{fieldName}' must be a number, got '{v}'");
        return parsed;
    }
}
