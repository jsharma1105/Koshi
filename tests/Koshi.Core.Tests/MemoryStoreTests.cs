using Koshi.Core.Memory;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

public class MemoryStoreTests : IDisposable
{
    private readonly string _vault;

    public MemoryStoreTests()
    {
        _vault = Path.Combine(Path.GetTempPath(), "koshi-store-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_vault);
    }

    public void Dispose()
    {
        try { Directory.Delete(_vault, recursive: true); } catch { /* best-effort */ }
    }

    private static MemoryRecord Make(string id, string subject = "x") => new()
    {
        Id = id,
        Type = MemoryType.Fact,
        Content = "body",
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
    public void AllocateId_starts_at_mem_000001_when_empty()
    {
        var store = new MemoryStore(new JsonFileBackend(null));
        store.WithFreshState(mems =>
        {
            Assert.Equal("mem-000001", store.AllocateId());
            Assert.Equal("mem-000002", store.AllocateId());
            return 0;
        });
    }

    [Fact]
    public void AllocateId_continues_past_max_existing_id()
    {
        // Seed backend with mem-000050.
        var path = Path.Combine(_vault, "memory.json");
        var json = new JsonFileBackend(path);
        json.Upsert(Make("mem-000050"), [Make("mem-000050")]);

        var store = new MemoryStore(new JsonFileBackend(path));
        store.WithFreshState(_ =>
        {
            Assert.Equal("mem-000051", store.AllocateId());
            return 0;
        });
    }

    [Fact]
    public void WithFreshState_reloads_in_vault_mode_after_external_add()
    {
        var be = new VaultBackend(_vault, watch: false);
        var store = new MemoryStore(be);

        Assert.Equal(0, store.WithFreshState(m => m.Count));

        // Externally drop a valid memory file.
        Directory.CreateDirectory(Path.Combine(_vault, "koshi", "facts"));
        File.WriteAllText(
            Path.Combine(_vault, "koshi", "facts", "ext--mem-000123.md"),
            "---\n" +
            "koshi:\n" +
            "  id: mem-000123\n" +
            "  type: Fact\n" +
            "  scope:\n" +
            "    user: \"*\"\n" +
            "    workspace: default\n" +
            "    thread: null\n" +
            "  source: external\n" +
            "  confidence: 0.9\n" +
            "  created-at: 2026-05-21T21:05:00Z\n" +
            "  last-accessed-at: 2026-05-21T21:05:00Z\n" +
            "  access-count: 0\n" +
            "  tier: Hot\n" +
            "---\n" +
            "# External\n\nbody\n");

        Assert.Equal(1, store.WithFreshState(m => m.Count));
    }

    [Fact]
    public void Vault_AllocateId_avoids_collision_with_externally_added_id()
    {
        var be = new VaultBackend(_vault, watch: false);
        var store = new MemoryStore(be);

        // External agent drops a memory at mem-000777.
        Directory.CreateDirectory(Path.Combine(_vault, "koshi", "facts"));
        File.WriteAllText(
            Path.Combine(_vault, "koshi", "facts", "ext--mem-000777.md"),
            "---\n" +
            "koshi:\n" +
            "  id: mem-000777\n" +
            "  type: Fact\n" +
            "  scope:\n" +
            "    user: \"*\"\n" +
            "    workspace: default\n" +
            "    thread: null\n" +
            "  source: external\n" +
            "  confidence: 0.9\n" +
            "  created-at: 2026-05-21T21:05:00Z\n" +
            "  last-accessed-at: 2026-05-21T21:05:00Z\n" +
            "  access-count: 0\n" +
            "  tier: Hot\n" +
            "---\n" +
            "# External\n");

        var nextId = store.WithFreshState(_ => store.AllocateId());
        Assert.Equal("mem-000778", nextId);
    }

    [Fact]
    public void Vault_external_deletion_does_not_resurrect_memory()
    {
        // Regression guard: in v0.5.x, the static _memories cache would re-save deleted files.
        var be = new VaultBackend(_vault, watch: false);
        var store = new MemoryStore(be);

        store.WithFreshState(memories =>
        {
            var a = Make("mem-000001", "A");
            memories.Add(a);
            store.Upsert(a);
            var b = Make("mem-000002", "B");
            memories.Add(b);
            store.Upsert(b);
            return 0;
        });

        // External deletion of mem-000001's file.
        var deletedPath = Path.Combine(_vault, "koshi", "facts", "a--mem-000001.md");
        Assert.True(File.Exists(deletedPath));
        File.Delete(deletedPath);

        // Next tool entry on store should reload — mem-000001 must be gone, mem-000002 must remain.
        var afterIds = store.WithFreshState(memories => memories.Select(m => m.Id).OrderBy(x => x).ToList());

        Assert.DoesNotContain("mem-000001", afterIds);
        Assert.Contains("mem-000002", afterIds);

        // And another Upsert of a DIFFERENT memory must not silently rewrite mem-000001 to disk.
        store.WithFreshState(memories =>
        {
            var c = Make("mem-000003", "C");
            memories.Add(c);
            store.Upsert(c);
            return 0;
        });

        Assert.False(File.Exists(deletedPath), "mem-000001 file must NOT have been resurrected");
    }

    [Fact]
    public void Json_mode_does_not_reload_per_call()
    {
        var path = Path.Combine(_vault, "memory.json");
        var be = new JsonFileBackend(path);
        var store = new MemoryStore(be);

        store.WithFreshState(memories =>
        {
            var a = Make("mem-000001");
            memories.Add(a);
            store.Upsert(a);
            return 0;
        });

        // Mutate the on-disk file behind the store's back.
        File.WriteAllText(path, "{}"); // empty envelope

        // JSON backend should NOT reload — cache survives.
        var count = store.WithFreshState(m => m.Count);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ReplaceAll_resets_state_and_disk()
    {
        var path = Path.Combine(_vault, "memory.json");
        var store = new MemoryStore(new JsonFileBackend(path));

        store.WithFreshState(memories =>
        {
            for (int i = 1; i <= 3; i++)
            {
                var r = Make($"mem-{i:D6}");
                memories.Add(r);
                store.Upsert(r);
            }
            return 0;
        });

        store.ReplaceAll([Make("mem-000099")]);

        var seen = store.WithFreshState(m => m.Select(x => x.Id).ToList());
        Assert.Single(seen);
        Assert.Equal("mem-000099", seen[0]);
    }
}
