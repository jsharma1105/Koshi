using Koshi.Core.Memory;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

public class VaultWatcherTests : IDisposable
{
    private readonly string _vault;
    private readonly string? _origEnv;

    public VaultWatcherTests()
    {
        _vault = Path.Combine(Path.GetTempPath(), "koshi-vaultwatch-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_vault);
        _origEnv = Environment.GetEnvironmentVariable("KOSHI_VAULT_WATCH");
        Environment.SetEnvironmentVariable("KOSHI_VAULT_WATCH", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KOSHI_VAULT_WATCH", _origEnv);
        try { Directory.Delete(_vault, recursive: true); } catch { /* best-effort */ }
    }

    private static MemoryRecord Make(string id, string subject) => new()
    {
        Id = id,
        Type = MemoryType.Fact,
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

    private static bool PollUntil(Func<bool> predicate, int timeoutMs = 3_000, int intervalMs = 25)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return true;
            Thread.Sleep(intervalMs);
        }
        return predicate();
    }

    [Fact]
    public void Default_ctor_attaches_watcher_when_env_unset()
    {
        using var be = new VaultBackend(_vault);
        Assert.Equal("healthy", be.WatcherStatus);
    }

    [Fact]
    public void Watch_false_explicit_disables_watcher()
    {
        using var be = new VaultBackend(_vault, watch: false);
        Assert.StartsWith("disabled", be.WatcherStatus);
        // No watcher → fallback "always reload" semantics.
        Assert.True(be.ShouldReload());
        Assert.True(be.ShouldReload());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("disabled")]
    [InlineData("OFF")]
    public void Env_var_disables_watcher(string envValue)
    {
        Environment.SetEnvironmentVariable("KOSHI_VAULT_WATCH", envValue);
        using var be = new VaultBackend(_vault);
        Assert.StartsWith("disabled", be.WatcherStatus);
        Assert.True(be.ShouldReload());
    }

    [Theory]
    [InlineData("on")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("anything-else")]
    public void Env_var_any_truthy_value_keeps_watcher_enabled(string envValue)
    {
        Environment.SetEnvironmentVariable("KOSHI_VAULT_WATCH", envValue);
        using var be = new VaultBackend(_vault);
        Assert.Equal("healthy", be.WatcherStatus);
    }

    [Fact]
    public void Fresh_backend_with_watcher_returns_clean()
    {
        using var be = new VaultBackend(_vault);
        // No events have fired since construction → cache is clean.
        Assert.False(be.ShouldReload());
        Assert.False(be.ShouldReload());
    }

    [Fact]
    public void Upsert_via_backend_marks_cache_dirty()
    {
        using var be = new VaultBackend(_vault);
        // Drain any startup events.
        _ = be.ShouldReload();

        be.Upsert(Make("mem-000001", "watcher test alpha"), []);

        var sawDirty = PollUntil(() => be.ShouldReload());
        Assert.True(sawDirty, "Watcher did not flag dirty after Upsert wrote a file under the watched tree.");

        // Read-and-clear semantics: a fresh call sees clean.
        Assert.False(be.ShouldReload());
    }

    [Fact]
    public void External_file_create_marks_cache_dirty()
    {
        using var be = new VaultBackend(_vault);
        _ = be.ShouldReload();

        var path = Path.Combine(be.KoshiDir, "facts", $"external-{Guid.NewGuid():N}.md");
        File.WriteAllText(path,
            "---\nkoshi:\n  id: mem-999999\n  type: Fact\n  subject: external\n  scope:\n    userId: '*'\n    workspaceId: default\n  source: user\n  confidence: 0.8\n  createdAt: 2026-05-22T00:00:00Z\n  lastAccessedAt: 2026-05-22T00:00:00Z\n  accessCount: 0\n  tier: Hot\n---\nBody.\n");

        var sawDirty = PollUntil(() => be.ShouldReload());
        Assert.True(sawDirty, "Watcher did not flag dirty after an external file was created.");
    }

    [Fact]
    public void External_file_delete_marks_cache_dirty()
    {
        // Pre-seed a file before the backend attaches its watcher.
        var seededDir = Path.Combine(_vault, "koshi", "facts");
        Directory.CreateDirectory(seededDir);
        var seededPath = Path.Combine(seededDir, "seeded-for-delete.md");
        File.WriteAllText(seededPath,
            "---\nkoshi:\n  id: mem-888888\n  type: Fact\n  subject: seeded\n  scope:\n    userId: '*'\n    workspaceId: default\n  source: user\n  confidence: 0.8\n  createdAt: 2026-05-22T00:00:00Z\n  lastAccessedAt: 2026-05-22T00:00:00Z\n  accessCount: 0\n  tier: Hot\n---\nBody.\n");

        using var be = new VaultBackend(_vault);
        _ = be.ShouldReload();

        File.Delete(seededPath);

        var sawDirty = PollUntil(() => be.ShouldReload());
        Assert.True(sawDirty, "Watcher did not flag dirty after an external file was deleted.");
    }

    [Fact]
    public void Multiple_events_coalesce_into_single_dirty_signal()
    {
        using var be = new VaultBackend(_vault);
        _ = be.ShouldReload();

        // Burst-write 10 files. Should result in dirty=1 once next call observes.
        for (int i = 0; i < 10; i++)
        {
            be.Upsert(Make($"mem-{i:D6}", $"coalesce {i}"), []);
        }

        var sawDirty = PollUntil(() => be.ShouldReload());
        Assert.True(sawDirty);

        // After read-and-clear, no more dirty (no events since).
        // Give a brief settle window so any in-flight events flush, then verify
        // the second read is clean. If events are still trickling in due to OS
        // batching, accept up to one more dirty signal but then expect clean.
        Thread.Sleep(150);
        _ = be.ShouldReload();
        Assert.False(be.ShouldReload(), "Expected clean state after draining events.");
    }

    [Fact]
    public void Dispose_is_idempotent_and_safe()
    {
        var be = new VaultBackend(_vault);
        be.Dispose();
        be.Dispose(); // must not throw
    }
}
