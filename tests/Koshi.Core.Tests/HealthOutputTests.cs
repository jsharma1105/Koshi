using System.Text;
using Koshi.Core.Models;
using Koshi.Mcp.Internal;
using Koshi.Mcp.Tools;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for the per-section <c>Persistence</c> sub-block introduced by #70.
/// Splits into two layers:
///   • Helper tests (<see cref="DiagnosticTools.DescribeFileOnDisk"/>,
///     <see cref="DiagnosticTools.AppendPersistenceBlock"/>,
///     <see cref="DiagnosticTools.FormatBytes"/>) — pure, deterministic.
///   • Integration tests (<see cref="DiagnosticTools.Health"/> end-to-end
///     output) using the same RetrievalTools-static fixture as
///     <see cref="IndexSnapshotAutoloadTests"/> so the snapshot-load path
///     exercises live state.
/// </summary>
public class HealthOutputHelperTests
{
    [Fact]
    public void DescribeFileOnDisk_returns_placeholder_when_path_null()
    {
        Assert.Equal("(in-memory only)", DiagnosticTools.DescribeFileOnDisk(null));
    }

    [Fact]
    public void DescribeFileOnDisk_returns_no_file_yet_when_file_missing()
    {
        var missing = Path.Join(Path.GetTempPath(), "koshi-test-missing-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        Assert.False(File.Exists(missing));
        Assert.Equal("(no file yet)", DiagnosticTools.DescribeFileOnDisk(missing));
    }

    [Fact]
    public void DescribeFileOnDisk_returns_vault_marker_for_existing_directory()
    {
        var dir = Path.Join(Path.GetTempPath(), "koshi-test-dir-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var result = DiagnosticTools.DescribeFileOnDisk(dir);
            Assert.Contains("vault directory", result, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    [Fact]
    public void DescribeFileOnDisk_returns_size_and_mtime_for_existing_file()
    {
        var path = Path.Join(Path.GetTempPath(), "koshi-test-file-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, new string('x', 2048));
        try
        {
            var result = DiagnosticTools.DescribeFileOnDisk(path);
            Assert.Contains("KB", result, StringComparison.Ordinal);
            Assert.Contains("last modified", result, StringComparison.Ordinal);
            // ISO-ish ("u") timestamp ends with " Z".
            Assert.EndsWith("Z", result.TrimEnd(), StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1024L * 1024, "1.0 MB")]
    [InlineData(1024L * 1024 * 1024 + 512L * 1024 * 1024, "1.5 GB")]
    public void FormatBytes_emits_unit_appropriate_to_magnitude(long bytes, string expected)
    {
        Assert.Equal(expected, DiagnosticTools.FormatBytes(bytes));
    }

    [Fact]
    public void AppendPersistenceBlock_disabled_persistence_shows_in_memory_only()
    {
        var sb = new StringBuilder();
        DiagnosticTools.AppendPersistenceBlock(
            sb,
            persistenceEnabled: false,
            persistencePath: null,
            loadAttempted: false,
            loadSucceeded: false,
            loadDiscardReason: null,
            loadedCount: 0,
            loadedAt: null,
            loadedNoun: "chunks",
            diskNoun: "Snapshot on disk");

        var out_ = sb.ToString();
        Assert.Contains("Save on shutdown:  disabled (in-memory only)", out_, StringComparison.Ordinal);
        Assert.Contains("Load on startup:   disabled (no persistence path configured)", out_, StringComparison.Ordinal);
        Assert.Contains("Snapshot on disk: (in-memory only)", out_, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendPersistenceBlock_load_succeeded_shows_count_and_timestamp()
    {
        var path = Path.Join(Path.GetTempPath(), "koshi-pb-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, "{}");
        try
        {
            var loadedAt = new DateTimeOffset(2026, 5, 28, 18, 46, 1, TimeSpan.Zero);
            var sb = new StringBuilder();
            DiagnosticTools.AppendPersistenceBlock(
                sb,
                persistenceEnabled: true,
                persistencePath: path,
                loadAttempted: true,
                loadSucceeded: true,
                loadDiscardReason: null,
                loadedCount: 42,
                loadedAt: loadedAt,
                loadedNoun: "records",
                diskNoun: "File on disk");

            var out_ = sb.ToString();
            Assert.Contains($"Save on shutdown:  yes (→ {path})", out_, StringComparison.Ordinal);
            Assert.Contains("Load on startup:   yes — loaded 42 records on startup (2026-05-28 18:46:01Z)", out_, StringComparison.Ordinal);
            Assert.Contains("File on disk:", out_, StringComparison.Ordinal);
            Assert.Contains("last modified", out_, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    [Fact]
    public void AppendPersistenceBlock_load_discarded_shows_reason()
    {
        var path = Path.Join(Path.GetTempPath(), "koshi-pb-discard-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(path, "{}");
        try
        {
            var sb = new StringBuilder();
            DiagnosticTools.AppendPersistenceBlock(
                sb,
                persistenceEnabled: true,
                persistencePath: path,
                loadAttempted: true,
                loadSucceeded: false,
                loadDiscardReason: "snapshot SnapshotPath disagrees with KOSHI_INDEX_PATH",
                loadedCount: 0,
                loadedAt: null,
                loadedNoun: "chunks",
                diskNoun: "Snapshot on disk");

            var out_ = sb.ToString();
            Assert.Contains("Load on startup:   no — discarded: snapshot SnapshotPath disagrees with KOSHI_INDEX_PATH", out_, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    [Fact]
    public void AppendPersistenceBlock_load_attempted_but_no_file_shows_no_snapshot_found()
    {
        var sb = new StringBuilder();
        var missing = Path.Join(Path.GetTempPath(), "koshi-pb-missing-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        DiagnosticTools.AppendPersistenceBlock(
            sb,
            persistenceEnabled: true,
            persistencePath: missing,
            loadAttempted: true,
            loadSucceeded: false,
            loadDiscardReason: null,
            loadedCount: 0,
            loadedAt: null,
            loadedNoun: "chunks",
            diskNoun: "Snapshot on disk");

        var out_ = sb.ToString();
        Assert.Contains("Load on startup:   no — no snapshot found on disk", out_, StringComparison.Ordinal);
        Assert.Contains("Snapshot on disk: (no file yet)", out_, StringComparison.Ordinal);
    }
}

/// <summary>
/// End-to-end tests of <see cref="DiagnosticTools.Health"/> output focused on
/// the retrieval section, where <see cref="RetrievalTools.ResetForTests"/>
/// gives us a clean static-state sandbox. Memory/Teams sections share global
/// state with other tests in the run and are therefore covered by the helper
/// tests above plus the smoke test.
/// </summary>
[Collection("RetrievalTools-static")]
public class HealthOutputIntegrationTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _indexFile;
    private readonly string _sourceDir;

    public HealthOutputIntegrationTests()
    {
        _tmpDir = Path.Join(Path.GetTempPath(), "koshi-health-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
        _indexFile = Path.Join(_tmpDir, ".koshi", "index.json");
        _sourceDir = Path.Join(_tmpDir, "source");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_indexFile)!);
        File.WriteAllText(Path.Join(_sourceDir, "a.md"), "the quick brown fox");
    }

    public void Dispose()
    {
        RetrievalTools.ResetForTests(null);
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { _ = ex; }
        GC.SuppressFinalize(this);
    }

    private static Chunk MakeChunk(string source, string content) => new(
        Id: Guid.NewGuid().ToString("N"),
        Content: content,
        Metadata: new ChunkMetadata(
            Source: source,
            DocumentType: "documentation",
            StartOffset: 0,
            EndOffset: content.Length,
            IngestedAt: DateTimeOffset.UtcNow))
    {
        TokenCount = content.Length / 4,
    };

    [Fact]
    public void Retrieval_section_shows_no_snapshot_found_when_nothing_loaded()
    {
        RetrievalTools.ResetForTests(_indexFile);
        var health = DiagnosticTools.Health();

        Assert.Contains("Retrieval:", health, StringComparison.Ordinal);
        // New three-line block, not the old "Persistence: enabled" line.
        Assert.Contains("Persistence:\r\n      Save on shutdown:", health.ReplaceLineEndings("\r\n"), StringComparison.Ordinal);
        Assert.Contains("Save on shutdown:  yes (→", health, StringComparison.Ordinal);
        Assert.Contains("Load on startup:   no — no snapshot found on disk", health, StringComparison.Ordinal);
        Assert.Contains("Snapshot on disk: (no file yet)", health, StringComparison.Ordinal);
    }

    [Fact]
    public void Retrieval_section_shows_load_succeeded_with_count_and_timestamp()
    {
        var enumeration = new IndexEnumerationParams { Pattern = "*.md", MaxFileSizeBytes = 1_000_000, MaxFiles = 1_000 };
        var fingerprint = ContentFingerprint.Compute(_sourceDir, enumeration);
        var persistence = new IndexPersistence(_indexFile);
        persistence.Save(_sourceDir, fingerprint, enumeration, [
            MakeChunk(Path.Join(_sourceDir, "a.md"), "the quick brown fox jumps over the lazy dog"),
            MakeChunk(Path.Join(_sourceDir, "a.md"), "package retrieval index snapshot sample"),
        ]);

        Environment.SetEnvironmentVariable("KOSHI_PROJECT_ROOT", _tmpDir);
        try
        {
            // Skip if the singleton's project root resolved before we set the env var.
            if (!string.Equals(PathConfig.Default.ProjectRoot, _tmpDir, StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(true, "Skipped — PathConfig.Default already resolved to a different root.");
                return;
            }

            RetrievalTools.ResetForTests(_indexFile);
            var health = DiagnosticTools.Health();

            // The "loaded N chunks on startup (UTC ts)" line is the heart of #70.
            Assert.Matches(@"Load on startup:   yes — loaded 2 chunks on startup \(\d{4}-\d{2}-\d{2}", health);
            // Snapshot file size+mtime line replaces the old plain "File: <path>".
            Assert.Matches(@"Snapshot on disk: \d+\s*(B|KB|MB), last modified \d{4}-\d{2}-\d{2}", health);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KOSHI_PROJECT_ROOT", null);
        }
    }
}
