using Koshi.Core.Memory;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

public class VaultFlavorTests : IDisposable
{
    private readonly string _vault;
    private readonly string? _origFlavorEnv;

    public VaultFlavorTests()
    {
        _vault = Path.Combine(Path.GetTempPath(), "koshi-vaultflavor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_vault);
        _origFlavorEnv = Environment.GetEnvironmentVariable(VaultLayout.EnvVar);
        Environment.SetEnvironmentVariable(VaultLayout.EnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VaultLayout.EnvVar, _origFlavorEnv);
        try { Directory.Delete(_vault, recursive: true); } catch { /* best-effort */ }
    }

    private static MemoryRecord Make(string id, MemoryType type, string subject) => new()
    {
        Id = id,
        Type = type,
        Content = $"Body of {subject}",
        Subject = subject,
        Scope = new MemoryScope("*", "default", null),
        Source = "user",
        Confidence = 0.8f,
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow,
        AccessCount = 0,
        Tier = MemoryTier.Hot,
    };

    // ─── Layout resolution ─────────────────────────────────────────────

    [Theory]
    [InlineData(null, "obsidian")]
    [InlineData("", "obsidian")]
    [InlineData("   ", "obsidian")]
    [InlineData("obsidian", "obsidian")]
    [InlineData("OBSIDIAN", "obsidian")]
    [InlineData("foam", "foam")]
    [InlineData("Foam", "foam")]
    [InlineData("logseq", "logseq")]
    [InlineData("LOGSEQ", "logseq")]
    [InlineData("dendron", "dendron")]
    public void Resolve_returns_expected_flavor(string? envValue, string expectedFlavor)
    {
        var layout = VaultLayout.Resolve(envValue);
        Assert.Equal(expectedFlavor, layout.FlavorName);
    }

    [Fact]
    public void Unknown_flavor_falls_back_to_obsidian()
    {
        var layout = VaultLayout.Resolve("notion");
        Assert.Equal("obsidian", layout.FlavorName);
    }

    [Fact]
    public void ResolveFromEnv_reads_env_var()
    {
        Environment.SetEnvironmentVariable(VaultLayout.EnvVar, "logseq");
        var layout = VaultLayout.ResolveFromEnv();
        Assert.Equal("logseq", layout.FlavorName);
    }

    // ─── Obsidian (default) ────────────────────────────────────────────

    [Fact]
    public void Obsidian_layout_writes_to_koshi_subdir()
    {
        using var be = new VaultBackend(_vault, watch: false, new ObsidianLayout());
        var rec = Make("mem-000001", MemoryType.Fact, "obsidian test");

        be.Upsert(rec, [rec]);

        var expected = Path.Combine(_vault, "koshi", "facts", "obsidian-test--mem-000001.md");
        Assert.True(File.Exists(expected), $"Expected file at: {expected}");
    }

    [Fact]
    public void Obsidian_layout_roundtrips_via_LoadAll()
    {
        using var be = new VaultBackend(_vault, watch: false, new ObsidianLayout());
        be.Upsert(Make("mem-000001", MemoryType.Decision, "chose dapper"), []);
        be.Upsert(Make("mem-000002", MemoryType.Pattern, "repository pattern"), []);

        var loaded = be.LoadAll();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, m => m.Id == "mem-000001");
        Assert.Contains(loaded, m => m.Id == "mem-000002");
    }

    // ─── Foam (alias of Obsidian) ──────────────────────────────────────

    [Fact]
    public void Foam_layout_is_obsidian_compatible()
    {
        using var be = new VaultBackend(_vault, watch: false, new FoamLayout());
        var rec = Make("mem-000001", MemoryType.Fact, "foam test");

        be.Upsert(rec, [rec]);

        // Same file path as Obsidian — Foam reads the same vault as Obsidian.
        var expected = Path.Combine(_vault, "koshi", "facts", "foam-test--mem-000001.md");
        Assert.True(File.Exists(expected));

        // Cross-flavor read: a Foam-written vault is readable by Obsidian and vice-versa.
        using var obsidianReader = new VaultBackend(_vault, watch: false, new ObsidianLayout());
        var loaded = obsidianReader.LoadAll();
        Assert.Single(loaded);
        Assert.Equal("mem-000001", loaded[0].Id);
    }

    // ─── Logseq ─────────────────────────────────────────────────────────

    [Fact]
    public void Logseq_layout_writes_to_pages_dir_with_prefix()
    {
        using var be = new VaultBackend(_vault, watch: false, new LogseqLayout());
        var rec = Make("mem-000001", MemoryType.Decision, "use logseq");

        be.Upsert(rec, [rec]);

        var expected = Path.Combine(_vault, "pages", "koshi-decision-use-logseq--mem-000001.md");
        Assert.True(File.Exists(expected), $"Expected file at: {expected}");

        // No nested koshi/ directory should have been created.
        Assert.False(Directory.Exists(Path.Combine(_vault, "koshi")));
    }

    [Fact]
    public void Logseq_layout_roundtrips_via_LoadAll()
    {
        using var be = new VaultBackend(_vault, watch: false, new LogseqLayout());
        be.Upsert(Make("mem-000001", MemoryType.Fact, "logseq alpha"), []);
        be.Upsert(Make("mem-000002", MemoryType.Pattern, "logseq beta"), []);

        var loaded = be.LoadAll();
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public void Logseq_layout_ignores_user_pages_without_koshi_prefix()
    {
        using var be = new VaultBackend(_vault, watch: false, new LogseqLayout());
        Directory.CreateDirectory(Path.Combine(_vault, "pages"));

        // User's own Logseq page — should never appear in LoadAll output.
        File.WriteAllText(
            Path.Combine(_vault, "pages", "my-personal-notes.md"),
            "# My Personal Notes\n\n- not a koshi memory\n");

        be.Upsert(Make("mem-000001", MemoryType.Fact, "real koshi"), []);

        var loaded = be.LoadAll();
        Assert.Single(loaded);
        Assert.Equal(0, be.UnmanagedNoteCount);
    }

    // ─── Dendron ────────────────────────────────────────────────────────

    [Fact]
    public void Dendron_layout_writes_dot_namespaced_files_at_root()
    {
        using var be = new VaultBackend(_vault, watch: false, new DendronLayout());
        var rec = Make("mem-000001", MemoryType.Pattern, "dendron note");

        be.Upsert(rec, [rec]);

        var expected = Path.Combine(_vault, "koshi.pattern.dendron-note--mem-000001.md");
        Assert.True(File.Exists(expected), $"Expected file at: {expected}");

        // Dendron should NOT create the koshi/ subdir.
        Assert.False(Directory.Exists(Path.Combine(_vault, "koshi")));
    }

    [Fact]
    public void Dendron_layout_roundtrips_via_LoadAll()
    {
        using var be = new VaultBackend(_vault, watch: false, new DendronLayout());
        be.Upsert(Make("mem-000001", MemoryType.Fact, "dendron alpha"), []);
        be.Upsert(Make("mem-000002", MemoryType.Decision, "dendron beta"), []);

        var loaded = be.LoadAll();
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public void Dendron_layout_ignores_user_notes_without_koshi_prefix()
    {
        using var be = new VaultBackend(_vault, watch: false, new DendronLayout());

        // User's own Dendron note at the root.
        File.WriteAllText(
            Path.Combine(_vault, "my.personal.note.md"),
            "# My Note\n\nNot a koshi memory.\n");

        be.Upsert(Make("mem-000001", MemoryType.Fact, "real koshi"), []);

        var loaded = be.LoadAll();
        Assert.Single(loaded);
        Assert.Equal(0, be.UnmanagedNoteCount);
    }

    // ─── Subject rename across flavors ─────────────────────────────────

    [Theory]
    [InlineData("obsidian")]
    [InlineData("foam")]
    [InlineData("logseq")]
    [InlineData("dendron")]
    public void Subject_rename_moves_file_for_all_flavors(string flavorName)
    {
        var layout = VaultLayout.Resolve(flavorName);
        using var be = new VaultBackend(_vault, watch: false, layout);

        var rec1 = Make("mem-000001", MemoryType.Fact, "old subject");
        be.Upsert(rec1, [rec1]);

        var oldPath = layout.TargetPath(_vault, rec1);
        Assert.True(File.Exists(oldPath), $"Old path missing: {oldPath}");

        // Rename: same id, new subject.
        var rec2 = rec1 with { Subject = "new subject" };
        be.Upsert(rec2, [rec2]);

        var newPath = layout.TargetPath(_vault, rec2);
        Assert.True(File.Exists(newPath), $"New path missing: {newPath}");
        Assert.False(File.Exists(oldPath), $"Old path should have been deleted: {oldPath}");

        // LoadAll sees exactly one record with the new subject.
        var loaded = be.LoadAll();
        Assert.Single(loaded);
        Assert.Equal("new subject", loaded[0].Subject);
    }

    // ─── Delete across flavors ─────────────────────────────────────────

    [Theory]
    [InlineData("obsidian")]
    [InlineData("logseq")]
    [InlineData("dendron")]
    public void Delete_removes_file_for_all_flavors(string flavorName)
    {
        var layout = VaultLayout.Resolve(flavorName);
        using var be = new VaultBackend(_vault, watch: false, layout);

        var rec = Make("mem-000001", MemoryType.Fact, "to delete");
        be.Upsert(rec, [rec]);

        var path = layout.TargetPath(_vault, rec);
        Assert.True(File.Exists(path));

        be.Delete("mem-000001", [rec]);
        Assert.False(File.Exists(path));
        Assert.Empty(be.LoadAll());
    }

    // ─── ReplaceAll preserves user files ───────────────────────────────

    [Fact]
    public void Logseq_ReplaceAll_leaves_user_pages_intact()
    {
        using var be = new VaultBackend(_vault, watch: false, new LogseqLayout());
        Directory.CreateDirectory(Path.Combine(_vault, "pages"));

        var userPage = Path.Combine(_vault, "pages", "untouched.md");
        File.WriteAllText(userPage, "# untouched");
        var koshiOldRec = Make("mem-000001", MemoryType.Fact, "old");
        be.Upsert(koshiOldRec, [koshiOldRec]);

        var koshiNewRec = Make("mem-000002", MemoryType.Decision, "new");
        be.ReplaceAll([koshiNewRec]);

        Assert.True(File.Exists(userPage), "User page must survive ReplaceAll.");
        Assert.False(File.Exists(Path.Combine(_vault, "pages", "koshi-fact-old--mem-000001.md")));
        Assert.True(File.Exists(Path.Combine(_vault, "pages", "koshi-decision-new--mem-000002.md")));
    }

    [Fact]
    public void Dendron_ReplaceAll_leaves_user_notes_intact()
    {
        using var be = new VaultBackend(_vault, watch: false, new DendronLayout());

        var userNote = Path.Combine(_vault, "my.note.md");
        File.WriteAllText(userNote, "# my note");
        var rec = Make("mem-000001", MemoryType.Fact, "first");
        be.Upsert(rec, [rec]);

        be.ReplaceAll([Make("mem-000002", MemoryType.Decision, "second")]);

        Assert.True(File.Exists(userNote), "User note must survive ReplaceAll.");
        Assert.False(File.Exists(Path.Combine(_vault, "koshi.fact.first--mem-000001.md")));
        Assert.True(File.Exists(Path.Combine(_vault, "koshi.decision.second--mem-000002.md")));
    }

    // ─── Watcher specs are flavor-appropriate ──────────────────────────

    [Fact]
    public void Obsidian_watcher_is_recursive_under_koshi_dir()
    {
        var layout = new ObsidianLayout();
        var spec = layout.WatcherSpec(_vault);
        Assert.NotNull(spec);
        Assert.Equal(Path.Combine(_vault, "koshi"), spec!.Value.watchDir);
        Assert.True(spec.Value.recursive);
        Assert.Equal("*.md", spec.Value.filter);
    }

    [Fact]
    public void Logseq_watcher_is_flat_pages_dir_with_prefix_filter()
    {
        var layout = new LogseqLayout();
        var spec = layout.WatcherSpec(_vault);
        Assert.NotNull(spec);
        Assert.Equal(Path.Combine(_vault, "pages"), spec!.Value.watchDir);
        Assert.False(spec.Value.recursive);
        Assert.Equal("koshi-*.md", spec.Value.filter);
    }

    [Fact]
    public void Dendron_watcher_is_flat_root_dir_with_prefix_filter()
    {
        var layout = new DendronLayout();
        var spec = layout.WatcherSpec(_vault);
        Assert.NotNull(spec);
        Assert.Equal(_vault, spec!.Value.watchDir);
        Assert.False(spec.Value.recursive);
        Assert.Equal("koshi.*.md", spec.Value.filter);
    }
}
