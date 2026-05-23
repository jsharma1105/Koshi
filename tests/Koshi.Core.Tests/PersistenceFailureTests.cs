using System.Runtime.InteropServices;
using Koshi.Core.Memory;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for the durable-write contract introduced in v0.8.x (issue #48).
/// Verifies that <see cref="MemoryPersistenceException"/> is thrown on real persistence
/// failures and that the <see cref="MemoryStore"/> rolls the in-memory cache forward to
/// the actual durable state (not back to a pre-mutation snapshot) when one is raised.
/// </summary>
public sealed class PersistenceFailureTests : IDisposable
{
    private readonly string _tmpDir;

    public PersistenceFailureTests()
    {
        _tmpDir = Path.Join(Path.GetTempPath(), "koshi-pf-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try
        {
            // Best-effort: clear any read-only attributes left by the OS-gated test.
            foreach (var f in Directory.EnumerateFiles(_tmpDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                        or PathTooLongException or DirectoryNotFoundException or ArgumentException)
                { _ = ex; }
            }
            Directory.Delete(_tmpDir, recursive: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or DirectoryNotFoundException)
        { _ = ex; }
    }

    private static MemoryRecord Make(string id, string subject = "x") => new()
    {
        Id = id,
        Type = MemoryType.Fact,
        Content = "body of " + subject,
        Subject = subject,
        Scope = new MemoryScope("*", "default", null),
        Source = "test",
        Confidence = 0.8f,
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow,
        AccessCount = 0,
        Tier = MemoryTier.Hot,
    };

    // ─── Exception type ────────────────────────────────────────────────────

    [Fact]
    public void Exception_carries_backend_kind_location_and_inner()
    {
        var inner = new IOException("disk full");
        var ex = new MemoryPersistenceException("json", "/tmp/koshi.json", "write failed", inner);

        Assert.Equal("json", ex.BackendKind);
        Assert.Equal("/tmp/koshi.json", ex.Location);
        Assert.Equal("write failed", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    // ─── ThrowingBackend round-trip rollback ───────────────────────────────
    // These are the cross-platform correctness tests for the WithFreshState
    // catch-reload-rethrow contract. They do not depend on filesystem quirks.

    [Fact]
    public void Upsert_failure_reloads_cache_from_disk_and_rethrows()
    {
        var backend = new ThrowingBackend(initial: [Make("mem-000001", "alpha")]);
        var store = new MemoryStore(backend);

        Assert.Equal(1, store.WithFreshState(m => m.Count));

        backend.ThrowOnUpsert = true;

        var thrown = Assert.Throws<MemoryPersistenceException>(() =>
            store.WithFreshState(memories =>
            {
                var r = Make("mem-000002", "beta");
                memories.Add(r);
                store.Upsert(r);
                return 0;
            }));

        Assert.Equal("test", thrown.BackendKind);
        Assert.NotNull(thrown.InnerException);

        // Cache must mirror disk state (still 1 record, the original alpha).
        backend.ThrowOnUpsert = false;
        Assert.Equal(1, store.WithFreshState(m => m.Count));
        Assert.Equal("alpha", store.WithFreshState(m => m[0].Subject));
    }

    [Fact]
    public void Delete_failure_mid_loop_reloads_cache_and_rethrows()
    {
        // Disk has 3 records; a Forget call removes the first two from cache, then
        // delete-from-disk fails on the third. After the throw, cache must reflect
        // what's actually on disk, NOT the pre-mutation snapshot.
        var backend = new ThrowingBackend(initial:
        [
            Make("mem-000001", "a"),
            Make("mem-000002", "a"),
            Make("mem-000003", "a"),
        ]);
        var store = new MemoryStore(backend);

        backend.ThrowOnDeleteIds.Add("mem-000003");

        Assert.Throws<MemoryPersistenceException>(() =>
            store.WithFreshState(memories =>
            {
                var toRemove = memories.ToList();
                foreach (var r in toRemove) memories.Remove(r);
                foreach (var r in toRemove) store.Delete(r.Id);
                return 0;
            }));

        // Backend's "disk" state: 1+2 were deleted, 3 remained (because delete threw before
        // mutating the disk list). Cache reload must reflect that.
        backend.ThrowOnDeleteIds.Clear();
        var after = store.WithFreshState(m => m.Select(r => r.Id).OrderBy(s => s).ToList());
        Assert.Single(after);
        Assert.Equal("mem-000003", after[0]);
    }

    [Fact]
    public void ReplaceAll_failure_reloads_cache_from_disk_and_rethrows()
    {
        var backend = new ThrowingBackend(initial: [Make("mem-000001", "kept")]);
        var store = new MemoryStore(backend);
        backend.ThrowOnReplaceAll = true;

        var thrown = Assert.Throws<MemoryPersistenceException>(() =>
            store.ReplaceAll([Make("mem-000099", "new")]));

        Assert.Equal("test", thrown.BackendKind);

        // Cache must still see the original record from disk.
        backend.ThrowOnReplaceAll = false;
        var after = store.WithFreshState(m => m.Select(r => r.Subject).ToList());
        Assert.Single(after);
        Assert.Equal("kept", after[0]);
    }

    [Fact]
    public void Cache_reload_survives_secondary_LoadAll_failure()
    {
        var backend = new ThrowingBackend(initial: [Make("mem-000001", "alpha")]);
        var store = new MemoryStore(backend);

        // First write succeeds, second throws. LoadAll during recovery also throws.
        backend.ThrowOnUpsert = true;
        backend.ThrowOnLoadAllAfterFailure = true;

        // The primary throw is rethrown; the secondary LoadAll failure is logged but
        // does not mask the primary exception.
        Assert.Throws<MemoryPersistenceException>(() =>
            store.WithFreshState(memories =>
            {
                memories.Add(Make("mem-000002", "beta"));
                store.Upsert(memories[^1]);
                return 0;
            }));
    }

    // ─── JsonFileBackend integration test (Windows-gated for predictability) ─

    [Fact]
    public void JsonFileBackend_throws_when_target_file_is_read_only()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On POSIX, File.SetAttributes(ReadOnly) maps to chmod -w which the .NET runtime
            // honors inconsistently across distros + CI runners. Skip cross-platform to avoid
            // flaky tests — the ThrowingBackend tests above cover the contract cross-platform.
            return;
        }

        var path = Path.Join(_tmpDir, "memory.json");
        var be = new JsonFileBackend(path);

        // Seed the file with a valid initial save.
        be.Upsert(Make("mem-000001", "seed"), [Make("mem-000001", "seed")]);
        Assert.True(File.Exists(path));

        // Now make it read-only — but the backend writes to `path + ".tmp"` first then moves
        // over it. So we need to lock the FINAL path (File.Move overwrite fails on RO target).
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var thrown = Assert.Throws<MemoryPersistenceException>(() =>
                be.Upsert(Make("mem-000002", "blocked"), [Make("mem-000001"), Make("mem-000002")]));

            Assert.Equal("json", thrown.BackendKind);
            Assert.Equal(path, thrown.Location);
            Assert.NotNull(thrown.InnerException);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // ─── VaultBackend integration test ─────────────────────────────────────

    [Fact]
    public void VaultBackend_throws_when_owned_file_cannot_be_deleted()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var vault = Path.Join(_tmpDir, "vault");
        Directory.CreateDirectory(vault);
        var be = new VaultBackend(vault, watch: false);

        // Seed one memory.
        be.Upsert(Make("mem-000001", "seed"), [Make("mem-000001", "seed")]);
        var loaded = be.LoadAll();
        Assert.Single(loaded);
        var ownedPath = Path.Join(vault, "koshi", "facts", "seed--mem-000001.md");
        Assert.True(File.Exists(ownedPath));

        // Make the file read-only so Delete throws AccessDeniedException.
        File.SetAttributes(ownedPath, FileAttributes.ReadOnly);
        try
        {
            var thrown = Assert.Throws<MemoryPersistenceException>(() =>
                be.Delete("mem-000001", []));
            Assert.Equal("vault", thrown.BackendKind);
            Assert.Equal(Path.GetFullPath(vault), thrown.Location);
            Assert.NotNull(thrown.InnerException);
        }
        finally
        {
            File.SetAttributes(ownedPath, FileAttributes.Normal);
        }
    }

    // ─── Disabled JSON backend (no path) is a no-op, not a throw ───────────

    [Fact]
    public void Disabled_JsonFileBackend_does_not_throw_on_save()
    {
        var be = new JsonFileBackend(null);
        Assert.False(be.IsEnabled);

        // No path => Save is a no-op even with a non-empty snapshot. Must not throw.
        be.Upsert(Make("mem-000001"), [Make("mem-000001")]);
        be.Delete("mem-000001", []);
        be.ReplaceAll([Make("mem-000002")]);
    }
}

/// <summary>
/// Test double that simulates a backend whose durable writes can be made to throw
/// <see cref="MemoryPersistenceException"/> on demand. Maintains an in-memory
/// "disk" list so reload-from-disk behavior can be observed.
/// </summary>
internal sealed class ThrowingBackend : IMemoryBackend
{
    private readonly List<MemoryRecord> _disk;

    public ThrowingBackend(IEnumerable<MemoryRecord>? initial = null)
    {
        _disk = initial?.ToList() ?? [];
    }

    public bool ThrowOnUpsert { get; set; }
    public bool ThrowOnReplaceAll { get; set; }
    public HashSet<string> ThrowOnDeleteIds { get; } = new(StringComparer.Ordinal);
    public bool ThrowOnLoadAllAfterFailure { get; set; }
    private bool _failureSeen;

    public bool IsEnabled => true;
    public string? Location => "test://memory";
    public string BackendKind => "test";
    public bool ShouldReload() => false;

    public int UnmanagedNoteCount => 0;
    public IReadOnlyList<string> UnmanagedNotePaths => [];
    public int DuplicateIdWarningCount => 0;

    public List<MemoryRecord> LoadAll()
    {
        if (ThrowOnLoadAllAfterFailure && _failureSeen)
            throw new IOException("simulated load failure after primary write failure");
        return [.. _disk];
    }

    public void Upsert(MemoryRecord record, IReadOnlyList<MemoryRecord> snapshot)
    {
        if (ThrowOnUpsert)
        {
            _failureSeen = true;
            throw new MemoryPersistenceException(
                BackendKind, Location, "simulated upsert failure",
                new IOException("disk full"));
        }
        var idx = _disk.FindIndex(m => m.Id == record.Id);
        if (idx >= 0) _disk[idx] = record;
        else _disk.Add(record);
    }

    public void Delete(string id, IReadOnlyList<MemoryRecord> snapshot)
    {
        if (ThrowOnDeleteIds.Contains(id))
        {
            _failureSeen = true;
            throw new MemoryPersistenceException(
                BackendKind, Location, $"simulated delete failure for {id}",
                new IOException("file locked"));
        }
        _disk.RemoveAll(m => m.Id == id);
    }

    public void ReplaceAll(IReadOnlyList<MemoryRecord> records)
    {
        if (ThrowOnReplaceAll)
        {
            _failureSeen = true;
            throw new MemoryPersistenceException(
                BackendKind, Location, "simulated replace-all failure",
                new IOException("disk full"));
        }
        _disk.Clear();
        _disk.AddRange(records);
    }
}
