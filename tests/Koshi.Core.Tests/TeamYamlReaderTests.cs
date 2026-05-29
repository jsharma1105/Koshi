using Koshi.Mcp.Cli.Setup;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for <see cref="TeamYamlReader"/>. The schema is tiny (5 keys); we
/// cover every accepted shape plus the rejection cases (#78 Gap A safety).
/// </summary>
public sealed class TeamYamlReaderTests
{
    [Fact]
    public void Parse_happy_path_extracts_both_blocks()
    {
        const string yaml = """
            team:
              id: platform-eng
              name: Platform Engineering
              token_budget: 16000
              quality_target: 0.75
            vault:
              repo: gh:my-org/koshi-vault
              path: .koshi/vault
            """;

        var r = TeamYamlReader.Parse(yaml);
        Assert.Null(r.Error);
        Assert.NotNull(r.Parsed);
        Assert.Equal("platform-eng", r.Parsed!.Team!.Id);
        Assert.Equal("Platform Engineering", r.Parsed.Team.Name);
        Assert.Equal(16000, r.Parsed.Team.TokenBudget);
        Assert.Equal(0.75, r.Parsed.Team.QualityTarget);
        Assert.Equal("gh:my-org/koshi-vault", r.Parsed.Vault!.Repo);
        Assert.Equal(".koshi/vault", r.Parsed.Vault.Path);
    }

    [Fact]
    public void Parse_accepts_quoted_values()
    {
        const string yaml = """
            team:
              id: "platform-eng"
              name: 'Platform Engineering'
            """;
        var r = TeamYamlReader.Parse(yaml);
        Assert.Null(r.Error);
        Assert.Equal("platform-eng", r.Parsed!.Team!.Id);
        Assert.Equal("Platform Engineering", r.Parsed.Team.Name);
    }

    [Fact]
    public void Parse_strips_inline_comments_outside_quotes()
    {
        const string yaml = """
            team:
              id: platform-eng     # the team id we use everywhere
              name: "Platform # Eng"  # the literal '#' must survive
            """;
        var r = TeamYamlReader.Parse(yaml);
        Assert.Null(r.Error);
        Assert.Equal("platform-eng", r.Parsed!.Team!.Id);
        Assert.Equal("Platform # Eng", r.Parsed.Team.Name); // hash inside double quotes is preserved
    }

    [Fact]
    public void Parse_rejects_unknown_root_key()
    {
        const string yaml = "teams:\n  id: x\n";
        var r = TeamYamlReader.Parse(yaml);
        Assert.NotNull(r.Error);
        Assert.Contains("unknown root key", r.Error);
        Assert.Equal(1, r.LineNumber);
    }

    [Fact]
    public void Parse_rejects_unknown_nested_key()
    {
        const string yaml = "team:\n  id: x\n  cosistency_target: 0.9\n";
        var r = TeamYamlReader.Parse(yaml);
        Assert.NotNull(r.Error);
        Assert.Contains("unknown key 'team.cosistency_target'", r.Error);
        Assert.Equal(3, r.LineNumber);
    }

    [Fact]
    public void Parse_rejects_nested_before_root()
    {
        const string yaml = "  id: orphan\n";
        var r = TeamYamlReader.Parse(yaml);
        Assert.NotNull(r.Error);
        Assert.Contains("nested value with no enclosing block", r.Error);
    }

    [Fact]
    public void Parse_rejects_non_integer_token_budget()
    {
        const string yaml = "team:\n  id: x\n  token_budget: lots\n";
        var r = TeamYamlReader.Parse(yaml);
        Assert.NotNull(r.Error);
        Assert.Contains("integer", r.Error);
    }

    [Fact]
    public void Parse_rejects_partial_vault_block()
    {
        const string yaml = "vault:\n  repo: gh:owner/repo\n";  // missing path
        var r = TeamYamlReader.Parse(yaml);
        Assert.NotNull(r.Error);
        Assert.Contains("both 'repo' and 'path'", r.Error);
    }

    [Fact]
    public void Parse_rejects_tabs_in_indentation()
    {
        const string yaml = "team:\n\tid: x\n";
        var r = TeamYamlReader.Parse(yaml);
        Assert.NotNull(r.Error);
        Assert.Contains("tabs", r.Error);
    }

    [Fact]
    public void Parse_empty_file_produces_empty_record()
    {
        var r = TeamYamlReader.Parse("");
        Assert.Null(r.Error);
        Assert.NotNull(r.Parsed);
        Assert.Null(r.Parsed!.Team);
        Assert.Null(r.Parsed.Vault);
    }

    [Fact]
    public void TryRead_returns_null_parsed_for_missing_file()
    {
        var r = TeamYamlReader.TryRead(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-missing.yml"));
        Assert.Null(r.Error);
        Assert.Null(r.Parsed);
    }

    [Fact]
    public void TryRead_reads_a_real_file()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "team:\n  id: alpha\n");
            var r = TeamYamlReader.TryRead(tmp);
            Assert.Null(r.Error);
            Assert.Equal("alpha", r.Parsed!.Team!.Id);
        }
        finally { File.Delete(tmp); }
    }
}
