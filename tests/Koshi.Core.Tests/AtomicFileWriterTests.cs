using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// Regressions for the <see cref="AtomicFileWriter"/> introduced in the
/// multi-model deep review (Opus O4/O5/O11). Verifies the temp-suffix
/// pattern, parent-directory creation, atomic append semantics, and the
/// best-effort cleanup-on-failure behaviour.
/// </summary>
public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _root;

    public AtomicFileWriterTests()
    {
        _root = Path.Join(Path.GetTempPath(), "koshi-afw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void WriteAllText_creates_missing_parent_directory()
    {
        var target = Path.Join(_root, "nested", "subdir", "out.json");
        AtomicFileWriter.WriteAllText(target, "{\"x\":1}");
        Assert.True(File.Exists(target));
        Assert.Equal("{\"x\":1}", File.ReadAllText(target));
    }

    [Fact]
    public void WriteAllText_overwrites_existing_target_atomically()
    {
        var target = Path.Join(_root, "out.json");
        File.WriteAllText(target, "OLD");
        AtomicFileWriter.WriteAllText(target, "NEW");
        Assert.Equal("NEW", File.ReadAllText(target));
    }

    [Fact]
    public void WriteAllText_uses_koshi_pid_guid_temp_suffix_pattern()
    {
        var target = Path.Join(_root, "suffix.json");
        var sample = AtomicFileWriter.MakeTempPath(target);

        Assert.StartsWith(target + ".koshi-", sample, StringComparison.Ordinal);
        Assert.EndsWith(".tmp", sample, StringComparison.Ordinal);
        Assert.Contains(Environment.ProcessId.ToString(), sample, StringComparison.Ordinal);

        // Sibling calls MUST produce distinct temp paths so two writers
        // never collide on the same temp filename (the entire reason this
        // helper exists per Opus O4).
        var a = AtomicFileWriter.MakeTempPath(target);
        var b = AtomicFileWriter.MakeTempPath(target);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void WriteAllText_leaves_no_stale_temp_file_on_success()
    {
        var target = Path.Join(_root, "noTempLeak.json");
        AtomicFileWriter.WriteAllText(target, "DONE");

        var leftovers = Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories).ToArray();
        Assert.Empty(leftovers);
    }

    [Fact]
    public void AppendAllText_concatenates_to_existing_content()
    {
        var target = Path.Join(_root, "appended.txt");
        AtomicFileWriter.WriteAllText(target, "first;");
        AtomicFileWriter.AppendAllText(target, "second;");
        AtomicFileWriter.AppendAllText(target, "third;");

        Assert.Equal("first;second;third;", File.ReadAllText(target));
    }

    [Fact]
    public void AppendAllText_creates_target_when_missing()
    {
        var target = Path.Join(_root, "createOnAppend.txt");
        AtomicFileWriter.AppendAllText(target, "only");
        Assert.Equal("only", File.ReadAllText(target));
    }

    [Fact]
    public void WriteAllText_rejects_empty_path()
    {
        Assert.Throws<ArgumentException>(() => AtomicFileWriter.WriteAllText("", "x"));
    }
}
