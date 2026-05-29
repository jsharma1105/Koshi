using Koshi.Mcp.Cli.Setup;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for <see cref="SteeringTemplateCatalog"/> and
/// <see cref="SteeringTemplateInstaller"/> — the Layer-4 auto-install step
/// that drops <c>AGENTS.md</c>, <c>.github/copilot-instructions.md</c>,
/// <c>.cursorrules</c>, and <c>.windsurfrules</c> into a project so any
/// MCP-aware AI client picks up the Koshi steering automatically.
/// </summary>
public sealed class SteeringTemplateInstallerTests
{
    [Fact]
    public void Catalog_discovers_all_four_templates()
    {
        var all = SteeringTemplateCatalog.Discover();
        Assert.Equal(4, all.Count);

        var names = all.Select(t => t.Name).ToArray();
        Assert.Contains("AGENTS.md", names);
        Assert.Contains(".github/copilot-instructions.md", names);
        Assert.Contains(".cursorrules", names);
        Assert.Contains(".windsurfrules", names);
    }

    [Fact]
    public void Every_template_body_contains_the_install_marker()
    {
        // Bodies that lack the marker silently break idempotency: any
        // re-run would treat them as missing-koshi-content and re-append.
        foreach (var tpl in SteeringTemplateCatalog.Discover())
        {
            var body = tpl.Read();
            Assert.Contains(SteeringTemplateInstaller.KoshiMarker, body);
        }
    }

    [Fact]
    public void InstallAll_writes_every_template_into_fresh_project()
    {
        var dir = NewTempDir();
        try
        {
            var results = SteeringTemplateInstaller.InstallAll(dir, force: false);
            Assert.Equal(4, results.Count);
            Assert.All(results, r => Assert.Equal(TemplateInstallOutcome.Written, r.Outcome));

            Assert.True(File.Exists(Path.Combine(dir, "AGENTS.md")));
            Assert.True(File.Exists(Path.Combine(dir, ".github", "copilot-instructions.md")));
            Assert.True(File.Exists(Path.Combine(dir, ".cursorrules")));
            Assert.True(File.Exists(Path.Combine(dir, ".windsurfrules")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InstallAll_is_idempotent_after_first_run()
    {
        var dir = NewTempDir();
        try
        {
            _ = SteeringTemplateInstaller.InstallAll(dir, force: false);
            var second = SteeringTemplateInstaller.InstallAll(dir, force: false);
            Assert.All(second, r => Assert.Equal(TemplateInstallOutcome.AlreadyPresent, r.Outcome));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InstallAll_appends_when_target_exists_without_marker()
    {
        var dir = NewTempDir();
        try
        {
            // Simulate a project that already has its own AGENTS.md without
            // any Koshi steering yet.
            var existing = "# My project rules\n\nDo X. Do Y.\n";
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), existing);

            var results = SteeringTemplateInstaller.InstallAll(dir, force: false);
            var agents = results.Single(r => r.TemplateName == "AGENTS.md");
            Assert.Equal(TemplateInstallOutcome.Appended, agents.Outcome);

            var merged = File.ReadAllText(Path.Combine(dir, "AGENTS.md"));
            Assert.StartsWith("# My project rules", merged);
            Assert.Contains(SteeringTemplateInstaller.KoshiMarker, merged);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InstallAll_does_not_double_append_on_subsequent_runs()
    {
        var dir = NewTempDir();
        try
        {
            var existing = "# My project rules\n";
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), existing);

            _ = SteeringTemplateInstaller.InstallAll(dir, force: false);
            var second = SteeringTemplateInstaller.InstallAll(dir, force: false);
            var agents = second.Single(r => r.TemplateName == "AGENTS.md");
            Assert.Equal(TemplateInstallOutcome.AlreadyPresent, agents.Outcome);

            var merged = File.ReadAllText(Path.Combine(dir, "AGENTS.md"));
            // Marker appears exactly once → no duplicated append.
            var count = CountOccurrences(merged, SteeringTemplateInstaller.KoshiMarker);
            Assert.Equal(1, count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InstallAll_overwrites_when_force_true()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cursorrules"), "REPLACE_ME");

            var results = SteeringTemplateInstaller.InstallAll(dir, force: true);
            var rules = results.Single(r => r.TemplateName == ".cursorrules");
            Assert.Equal(TemplateInstallOutcome.Overwrote, rules.Outcome);

            var body = File.ReadAllText(Path.Combine(dir, ".cursorrules"));
            Assert.DoesNotContain("REPLACE_ME", body);
            Assert.Contains(SteeringTemplateInstaller.KoshiMarker, body);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void InstallAll_creates_dotgithub_directory_for_copilot_instructions()
    {
        var dir = NewTempDir();
        try
        {
            Assert.False(Directory.Exists(Path.Combine(dir, ".github")));
            _ = SteeringTemplateInstaller.InstallAll(dir, force: false);
            Assert.True(Directory.Exists(Path.Combine(dir, ".github")));
            Assert.True(File.Exists(Path.Combine(dir, ".github", "copilot-instructions.md")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewTempDir()
    {
        var p = Path.Combine(Path.GetTempPath(), "koshi-tpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
