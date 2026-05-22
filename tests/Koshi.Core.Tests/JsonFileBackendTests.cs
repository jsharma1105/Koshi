using Koshi.Core.Memory;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

public class JsonFileBackendTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _file;

    public JsonFileBackendTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "koshi-jsonbe-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
        _file = Path.Combine(_tmpDir, "memory.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
    }

    private static MemoryRecord Make(string id, string subject) => new()
    {
        Id = id,
        Type = MemoryType.Fact,
        Content = $"Body of {subject}",
        Subject = subject,
        Scope = new MemoryScope("*", "default", null),
        Source = "user",
        Confidence = 0.7f,
        CreatedAt = DateTimeOffset.UtcNow,
        LastAccessedAt = DateTimeOffset.UtcNow,
        AccessCount = 0,
        Tier = MemoryTier.Hot,
    };

    [Fact]
    public void Backend_metadata_reports_json_kind()
    {
        var be = new JsonFileBackend(_file);
        Assert.True(be.IsEnabled);
        Assert.Equal("json", be.BackendKind);
        Assert.False(be.ShouldReload());
        Assert.Equal(_file, be.Location);
    }

    [Fact]
    public void Null_path_disables_persistence()
    {
        var be = new JsonFileBackend(null);
        Assert.False(be.IsEnabled);
        Assert.Empty(be.LoadAll());
        be.Upsert(Make("mem-000001", "x"), [Make("mem-000001", "x")]);
        Assert.Empty(be.LoadAll()); // never touched disk
    }

    [Fact]
    public void Upsert_then_LoadAll_returns_snapshot()
    {
        var be = new JsonFileBackend(_file);
        var a = Make("mem-000001", "First");
        var b = Make("mem-000002", "Second");

        be.Upsert(a, [a]);
        be.Upsert(b, [a, b]);

        // Use a fresh backend instance to verify on-disk state.
        var fresh = new JsonFileBackend(_file);
        var all = fresh.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.Id == "mem-000001");
        Assert.Contains(all, m => m.Id == "mem-000002");
    }

    [Fact]
    public void Delete_writes_snapshot_without_target_id()
    {
        var be = new JsonFileBackend(_file);
        var a = Make("mem-000001", "Keep");
        var b = Make("mem-000002", "Drop");
        be.Upsert(a, [a, b]);
        be.Upsert(b, [a, b]);

        // Caller has already removed b from snapshot before calling Delete.
        be.Delete("mem-000002", [a]);

        var fresh = new JsonFileBackend(_file);
        var all = fresh.LoadAll();
        Assert.Single(all);
        Assert.Equal("mem-000001", all[0].Id);
    }

    [Fact]
    public void ReplaceAll_overwrites_envelope()
    {
        var be = new JsonFileBackend(_file);
        be.Upsert(Make("mem-000001", "old"), [Make("mem-000001", "old")]);

        var fresh = Make("mem-000099", "fresh");
        be.ReplaceAll([fresh]);

        var verify = new JsonFileBackend(_file).LoadAll();
        Assert.Single(verify);
        Assert.Equal("mem-000099", verify[0].Id);
    }

    [Fact]
    public void Corrupt_file_returns_empty_list_without_throwing()
    {
        File.WriteAllText(_file, "{not valid json at all");
        var be = new JsonFileBackend(_file);
        Assert.Empty(be.LoadAll()); // tolerates corrupt file
    }

    [Fact]
    public void LoadAll_empty_when_file_missing()
    {
        var be = new JsonFileBackend(_file);
        Assert.False(File.Exists(_file));
        Assert.Empty(be.LoadAll());
    }

    [Fact]
    public void Atomic_write_creates_parent_directory()
    {
        var nested = Path.Combine(_tmpDir, "a", "b", "c", "memory.json");
        var be = new JsonFileBackend(nested);
        be.Upsert(Make("mem-000001", "x"), [Make("mem-000001", "x")]);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Unmanaged_and_duplicate_counts_are_always_zero_for_json()
    {
        var be = new JsonFileBackend(_file);
        Assert.Equal(0, be.UnmanagedNoteCount);
        Assert.Empty(be.UnmanagedNotePaths);
        Assert.Equal(0, be.DuplicateIdWarningCount);
    }
}
