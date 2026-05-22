using Koshi.Core.Memory;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

// Serialize against other vault test classes — PR #41 made the 2-arg
// VaultBackend ctor consult KOSHI_VAULT_FLAVOR, and VaultFlavorTests
// mutates that env var mid-test. Without [Collection], xUnit can run
// the constructors in parallel and our nulled-out env can be observed
// as "logseq" by an unsuspecting test in this class.
[Collection("VaultEnvVar")]
public class VaultBackendTests : IDisposable
{
    private readonly string _vault;
    private readonly string? _origFlavorEnv;

    public VaultBackendTests()
    {
        _vault = Path.Join(Path.GetTempPath(), "koshi-vaultbe-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_vault);
        // The 2-arg VaultBackend ctor resolves layout from this env var
        // (since PR #41). Null it out so these tests deterministically
        // exercise the Obsidian (default) layout they were written for,
        // regardless of the developer's shell environment.
        _origFlavorEnv = Environment.GetEnvironmentVariable(VaultLayout.EnvVar);
        Environment.SetEnvironmentVariable(VaultLayout.EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VaultLayout.EnvVar, _origFlavorEnv);
        try { Directory.Delete(_vault, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { _ = ex; }
    }

    private static MemoryRecord Make(string id, MemoryType type, string subject, string? content = null) => new()
    {
        Id = id,
        Type = type,
        Content = content ?? $"Body of {subject}",
        Subject = subject,
        Scope = new MemoryScope("*", "default", null),
        Source = "user",
        Confidence = 0.8f,
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow,
        AccessCount = 0,
        Tier = MemoryTier.Hot,
    };

    [Fact]
    public void Upsert_writes_file_under_type_subdir()
    {
        var be = new VaultBackend(_vault, watch: false);
        var rec = Make("mem-000001", MemoryType.Decision, "Chose Dapper");

        be.Upsert(rec, [rec]);

        var expected = Path.Join(_vault, "koshi", "decisions", "chose-dapper--mem-000001.md");
        Assert.True(File.Exists(expected), $"Expected file at: {expected}");
    }

    [Fact]
    public void Upsert_creates_all_type_subdirectories_on_init()
    {
        _ = new VaultBackend(_vault, watch: false);

        Assert.True(Directory.Exists(Path.Join(_vault, "koshi", "facts")));
        Assert.True(Directory.Exists(Path.Join(_vault, "koshi", "decisions")));
        Assert.True(Directory.Exists(Path.Join(_vault, "koshi", "patterns")));
        Assert.True(Directory.Exists(Path.Join(_vault, "koshi", "preferences")));
    }

    [Fact]
    public void Upsert_same_id_twice_results_in_single_file()
    {
        var be = new VaultBackend(_vault, watch: false);
        var v1 = Make("mem-000001", MemoryType.Fact, "Title", "original content");
        be.Upsert(v1, [v1]);

        var v2 = v1 with { Content = "updated content" };
        be.Upsert(v2, [v2]);

        var files = Directory.GetFiles(Path.Join(_vault, "koshi"), "*.md", SearchOption.AllDirectories);
        Assert.Single(files);
        Assert.Contains("updated content", File.ReadAllText(files[0]));
    }

    [Fact]
    public void Upsert_with_changed_subject_moves_file()
    {
        var be = new VaultBackend(_vault, watch: false);
        var v1 = Make("mem-000001", MemoryType.Pattern, "Old Subject");
        be.Upsert(v1, [v1]);
        var oldPath = Path.Join(_vault, "koshi", "patterns", "old-subject--mem-000001.md");
        Assert.True(File.Exists(oldPath));

        var v2 = v1 with { Subject = "New Subject" };
        be.Upsert(v2, [v2]);

        var newPath = Path.Join(_vault, "koshi", "patterns", "new-subject--mem-000001.md");
        Assert.True(File.Exists(newPath), "new path must exist");
        Assert.False(File.Exists(oldPath), "old path must be deleted");

        var files = Directory.GetFiles(Path.Join(_vault, "koshi"), "*.md", SearchOption.AllDirectories);
        Assert.Single(files);
    }

    [Fact]
    public void Delete_removes_file()
    {
        var be = new VaultBackend(_vault, watch: false);
        var rec = Make("mem-000001", MemoryType.Fact, "Doomed");
        be.Upsert(rec, [rec]);
        be.LoadAll(); // populate _idToPath

        be.Delete("mem-000001", []);

        var files = Directory.GetFiles(Path.Join(_vault, "koshi"), "*.md", SearchOption.AllDirectories);
        Assert.Empty(files);
    }

    [Fact]
    public void LoadAll_picks_up_externally_added_file()
    {
        var be = new VaultBackend(_vault, watch: false);
        Assert.Empty(be.LoadAll());

        // External agent (e.g., git pull) drops a properly-formatted .md file.
        var ext = Path.Join(_vault, "koshi", "facts", "external--mem-000077.md");
        File.WriteAllText(ext,
            "---\n" +
            "koshi:\n" +
            "  id: mem-000077\n" +
            "  type: Fact\n" +
            "  scope:\n" +
            "    user: \"*\"\n" +
            "    workspace: default\n" +
            "    thread: null\n" +
            "  source: teammate\n" +
            "  confidence: 0.75\n" +
            "  created-at: 2026-05-21T21:05:00Z\n" +
            "  last-accessed-at: 2026-05-21T21:05:00Z\n" +
            "  access-count: 0\n" +
            "  tier: Hot\n" +
            "---\n" +
            "# External fact\n" +
            "\n" +
            "This came from a teammate's git pull.\n");

        var all = be.LoadAll();
        Assert.Single(all);
        Assert.Equal("mem-000077", all[0].Id);
        Assert.Equal("External fact", all[0].Subject);
    }

    [Fact]
    public void LoadAll_reports_files_without_koshi_id_as_unmanaged()
    {
        var be = new VaultBackend(_vault, watch: false);
        var plain = Path.Join(_vault, "koshi", "my-personal-note.md");
        File.WriteAllText(plain, "# Just my note\n\nNo frontmatter here.\n");

        var all = be.LoadAll();

        Assert.Empty(all);
        Assert.Equal(1, be.UnmanagedNoteCount);
        Assert.Contains(plain, be.UnmanagedNotePaths);
    }

    [Fact]
    public void User_added_frontmatter_keys_survive_Upsert()
    {
        var be = new VaultBackend(_vault, watch: false);
        var rec = Make("mem-000010", MemoryType.Fact, "Tagged fact");
        be.Upsert(rec, [rec]);
        be.LoadAll();

        // User edits the file in Obsidian and adds tags.
        var path = Path.Join(_vault, "koshi", "facts", "tagged-fact--mem-000010.md");
        var orig = File.ReadAllText(path);
        var fmEnd = orig.IndexOf("\n---\n", StringComparison.Ordinal);
        Assert.True(fmEnd > 0);
        var enhanced = orig[..fmEnd] + "\ntags: [user-added, important]\ncssclass: callout\n" + orig[fmEnd..];
        File.WriteAllText(path, enhanced);

        // Koshi-side update.
        var updated = rec with { Content = "Updated body" };
        be.Upsert(updated, [updated]);

        var after = File.ReadAllText(path);
        Assert.Contains("tags: [user-added, important]", after);
        Assert.Contains("cssclass: callout", after);
        Assert.Contains("Updated body", after);
    }

    [Fact]
    public void ReplaceAll_deletes_managed_files_but_keeps_unmanaged()
    {
        var be = new VaultBackend(_vault, watch: false);
        var rec = Make("mem-000001", MemoryType.Fact, "Will be replaced");
        be.Upsert(rec, [rec]);

        var unmanaged = Path.Join(_vault, "koshi", "my-readme.md");
        File.WriteAllText(unmanaged, "# User README\n");

        var newRec = Make("mem-000099", MemoryType.Decision, "Brand new");
        be.ReplaceAll([newRec]);

        Assert.True(File.Exists(unmanaged), "unmanaged file must be preserved");
        var managed = Directory.EnumerateFiles(Path.Join(_vault, "koshi"), "*.md", SearchOption.AllDirectories)
            .Where(p => p != unmanaged)
            .ToList();
        Assert.Single(managed);
        Assert.Contains("mem-000099", managed[0]);
    }

    [Fact]
    public void Duplicate_id_across_files_picks_newest_mtime()
    {
        var be = new VaultBackend(_vault, watch: false);

        // Same id in two different paths (e.g., post-merge artifact).
        var earlier = Path.Join(_vault, "koshi", "facts", "earlier--mem-000005.md");
        var later = Path.Join(_vault, "koshi", "facts", "later--mem-000005.md");
        var frontmatter =
            "---\n" +
            "koshi:\n" +
            "  id: mem-000005\n" +
            "  type: Fact\n" +
            "  scope:\n" +
            "    user: \"*\"\n" +
            "    workspace: default\n" +
            "    thread: null\n" +
            "  source: user\n" +
            "  confidence: 0.8\n" +
            "  created-at: 2026-05-21T21:05:00Z\n" +
            "  last-accessed-at: 2026-05-21T21:05:00Z\n" +
            "  access-count: 0\n" +
            "  tier: Hot\n" +
            "---\n";

        File.WriteAllText(earlier, frontmatter + "# Earlier\n\nold body\n");
        File.SetLastWriteTimeUtc(earlier, DateTime.UtcNow.AddMinutes(-10));

        File.WriteAllText(later, frontmatter + "# Later\n\nnew body\n");
        File.SetLastWriteTimeUtc(later, DateTime.UtcNow);

        var all = be.LoadAll();
        Assert.Single(all);
        Assert.Equal("Later", all[0].Subject);
        Assert.True(be.DuplicateIdWarningCount >= 1);
    }

    [Fact]
    public void External_deletion_is_reflected_on_next_LoadAll()
    {
        var be = new VaultBackend(_vault, watch: false);
        var a = Make("mem-000001", MemoryType.Fact, "Will be deleted");
        var b = Make("mem-000002", MemoryType.Fact, "Will remain");
        be.Upsert(a, [a, b]);
        be.Upsert(b, [a, b]);

        var aPath = Path.Join(_vault, "koshi", "facts", "will-be-deleted--mem-000001.md");
        Assert.True(File.Exists(aPath));

        // Teammate deletes the file (or it gets removed by a git pull).
        File.Delete(aPath);

        var all = be.LoadAll();
        Assert.Single(all);
        Assert.Equal("mem-000002", all[0].Id);
    }

    [Fact]
    public void Tmp_files_are_ignored_by_LoadAll()
    {
        var be = new VaultBackend(_vault, watch: false);
        var rec = Make("mem-000001", MemoryType.Fact, "Real");
        be.Upsert(rec, [rec]);

        // Simulate a crashed half-written file from a previous Upsert.
        var stuckTmp = Path.Join(_vault, "koshi", "facts", "stuck--mem-000002.md.tmp");
        File.WriteAllText(stuckTmp, "junk");

        var all = be.LoadAll();
        Assert.Single(all);
        Assert.Equal("mem-000001", all[0].Id);
    }
}
