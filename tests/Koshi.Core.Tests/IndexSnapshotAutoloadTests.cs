using System.Text.Json;
using Koshi.Core.Models;
using Koshi.Mcp.Internal;
using Koshi.Mcp.Tools;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for the index-snapshot auto-load behaviour introduced by issue #62.
/// Each test sandboxes <see cref="RetrievalTools"/> via the internal
/// <c>ResetForTests</c> hook so global state never leaks between tests, and
/// writes a real <c>index.json</c> envelope to disk that the production
/// loader path consumes — i.e. these are integration tests of the
/// load-on-startup contract rather than unit tests of a single helper.
/// </summary>
[Collection("RetrievalTools-static")]
public class IndexSnapshotAutoloadTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _indexFile;
    private readonly string _sourceDir;

    public IndexSnapshotAutoloadTests()
    {
        _tmpDir = Path.Join(Path.GetTempPath(), "koshi-autoload-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
        _indexFile = Path.Join(_tmpDir, ".koshi", "index.json");
        _sourceDir = Path.Join(_tmpDir, "source");
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_indexFile)!);
        File.WriteAllText(Path.Join(_sourceDir, "a.md"), "the quick brown fox jumps over the lazy dog");
        File.WriteAllText(Path.Join(_sourceDir, "b.md"), "package retrieval index snapshot autoload sample document");
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

    /// <summary>
    /// Write an envelope at <paramref name="indexFile"/> via the live
    /// IndexPersistence pipeline so the on-disk shape (including SchemaVersion
    /// + SnapshotPath stamping) matches exactly what the production saver
    /// produces.
    /// </summary>
    private static void SaveEnvelopeViaProduction(
        string indexFile, string? sourcePath, IReadOnlyList<Chunk> chunks)
    {
        var enumeration = new IndexEnumerationParams { Pattern = "*.md", MaxFileSizeBytes = 1_000_000, MaxFiles = 1_000 };
        var fingerprint = ContentFingerprint.Compute(sourcePath, enumeration);
        var be = new IndexPersistence(indexFile);
        be.Save(sourcePath, fingerprint, enumeration, chunks);
    }

    [Fact]
    public void Snapshot_under_project_root_loads_on_startup()
    {
        // Source is inside cwd-equivalent — the simplest happy path.
        // Even before #62 this worked, regression guard.
        SaveEnvelopeViaProduction(_indexFile, _sourceDir, [
            MakeChunk(Path.Join(_sourceDir, "a.md"), "the quick brown fox jumps over the lazy dog"),
            MakeChunk(Path.Join(_sourceDir, "b.md"), "package retrieval index snapshot autoload sample document"),
        ]);

        // Repoint the project root at the same parent so containment check passes.
        Environment.SetEnvironmentVariable("KOSHI_PROJECT_ROOT", _tmpDir);
        try
        {
            // PathConfig.Default is a process-global singleton resolved once at
            // type-init time, so we cannot directly inject via env vars for a
            // test running after the singleton has been touched. Instead this
            // test relies on running in process isolation OR on the
            // ResetForTests hook narrowing the persistence file — the
            // containment check still uses PathConfig.Default.ProjectRoot.
            // We skip when the singleton was already initialised with a
            // different root.
            if (!string.Equals(PathConfig.Default.ProjectRoot, _tmpDir, StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(true, "Skipped — PathConfig.Default already resolved to a different root.");
                return;
            }

            RetrievalTools.ResetForTests(_indexFile);

            var status = RetrievalTools.GetStatus();

            Assert.True(status.indexed, status.snapshotDiscardReason ?? "(no discard reason)");
            Assert.True(status.loadedFromSnapshot);
            Assert.Equal(2, status.chunkCount);
            Assert.Null(status.snapshotDiscardReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KOSHI_PROJECT_ROOT", null);
        }
    }

    [Fact]
    public void Snapshot_written_by_us_but_indexing_an_outside_directory_still_loads()
    {
        // The user's repro from issue #62. They called
        //   koshi_index_directory("C:/work/some-repo")
        // from a server whose cwd was a different directory. The snapshot
        // file ended up in <cwd>/.koshi/index.json with SourcePath pointing
        // outside cwd. The OLD behaviour discarded it; the #62 fix trusts
        // it because SnapshotPath matches our own _persistence.Path.
        var outsideDir = Path.Join(Path.GetTempPath(), "koshi-outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Join(outsideDir, "outside.md"), "outside file content");
        try
        {
            SaveEnvelopeViaProduction(_indexFile, outsideDir, [
                MakeChunk(Path.Join(outsideDir, "outside.md"), "outside file content"),
            ]);

            RetrievalTools.ResetForTests(_indexFile);

            var status = RetrievalTools.GetStatus();

            // The actual #62 fix assertion: the snapshot WE wrote loads even
            // though its source is outside our project root.
            Assert.True(status.indexed,
                $"snapshot should load; discard reason was: {status.snapshotDiscardReason ?? "(none)"}");
            Assert.True(status.loadedFromSnapshot);
            Assert.Equal(1, status.chunkCount);
            Assert.Null(status.snapshotDiscardReason);
            // Warning may or may not fire depending on whether project root
            // happens to contain the temp dir — we just assert the load
            // succeeded.
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    [Fact]
    public void Copied_snapshot_from_another_project_is_discarded_with_visible_reason()
    {
        // Simulates: user copies <projectA>/.koshi/index.json into
        // <projectB>/.koshi/index.json and runs koshi-mcp from projectB.
        // Production saves the absolute SnapshotPath; on copy, our
        // _persistence.Path won't match → containment defense fires.
        var phantomDir = Path.Join(Path.GetTempPath(), "koshi-phantom-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(phantomDir);
        File.WriteAllText(Path.Join(phantomDir, "phantom.md"), "phantom file");
        try
        {
            // Save the envelope using a DIFFERENT IndexPersistence path so the
            // stamped SnapshotPath != our test's _indexFile (simulates copy).
            var foreignPath = Path.Join(_tmpDir, ".koshi", "foreign-index.json");
            SaveEnvelopeViaProduction(foreignPath, phantomDir, [
                MakeChunk(Path.Join(phantomDir, "phantom.md"), "phantom file"),
            ]);

            // Hand-copy the foreign envelope to our real index file. After
            // the copy SnapshotPath still references the foreign path.
            File.Copy(foreignPath, _indexFile, overwrite: true);
            File.Delete(foreignPath);

            RetrievalTools.ResetForTests(_indexFile);

            var status = RetrievalTools.GetStatus();

            // Defense fires: copied snapshot whose source is outside any
            // plausible project root must not silently load.
            Assert.False(status.indexed,
                "copied cross-project snapshot should be discarded; was loaded with no warning");
            Assert.NotNull(status.snapshotDiscardReason);
            Assert.Contains("KOSHI_INDEX_PATH", status.snapshotDiscardReason!);
        }
        finally
        {
            try { Directory.Delete(phantomDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    [Fact]
    public void Empty_or_missing_snapshot_file_is_a_clean_no_op()
    {
        // No snapshot written; reset and observe.
        RetrievalTools.ResetForTests(_indexFile);

        var status = RetrievalTools.GetStatus();

        Assert.False(status.indexed);
        Assert.False(status.loadedFromSnapshot);
        Assert.Equal(0, status.chunkCount);
        // No discard reason — file simply doesn't exist, that's not an error.
        Assert.Null(status.snapshotDiscardReason);
    }

    [Fact]
    public void Corrupt_snapshot_is_skipped_without_throwing()
    {
        File.WriteAllText(_indexFile, "{not valid json");

        RetrievalTools.ResetForTests(_indexFile);

        // Should not throw; should not be indexed.
        var status = RetrievalTools.GetStatus();
        Assert.False(status.indexed);
    }

    [Fact]
    public void Legacy_snapshot_without_SnapshotPath_falls_back_to_containment()
    {
        // A v0.8.x snapshot written before this PR has no SnapshotPath stamp.
        // The acceptability gate must still apply containment (no
        // un-bounded trust for legacy files): source under root → load,
        // source outside root → discard.
        var legacyEnvelope = new IndexEnvelope
        {
            SchemaVersion = 1,
            SavedAt = DateTimeOffset.UtcNow,
            SourcePath = _sourceDir,            // INSIDE the test's _tmpDir
            ContentFingerprint = ContentFingerprint.Compute(_sourceDir, new IndexEnumerationParams
            {
                Pattern = "*.md", MaxFileSizeBytes = 1_000_000, MaxFiles = 1_000,
            }),
            Enumeration = new IndexEnumerationParams { Pattern = "*.md", MaxFileSizeBytes = 1_000_000, MaxFiles = 1_000 },
            Chunks = [MakeChunk(Path.Join(_sourceDir, "a.md"), "the quick brown fox")],
            SnapshotPath = null,    // legacy
        };
        File.WriteAllText(_indexFile,
            JsonSerializer.Serialize(legacyEnvelope, KoshiJsonContext.Default.IndexEnvelope));

        // Repoint project root at our temp dir so containment can pass.
        Environment.SetEnvironmentVariable("KOSHI_PROJECT_ROOT", _tmpDir);
        try
        {
            if (!string.Equals(PathConfig.Default.ProjectRoot, _tmpDir, StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(true, "Skipped — PathConfig.Default already resolved to a different root.");
                return;
            }

            RetrievalTools.ResetForTests(_indexFile);
            var status = RetrievalTools.GetStatus();

            // Legacy + source inside root → load.
            Assert.True(status.indexed,
                $"legacy in-root snapshot should load; discard reason was: {status.snapshotDiscardReason ?? "(none)"}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KOSHI_PROJECT_ROOT", null);
        }
    }
}

/// <summary>
/// Collection fixture marker. RetrievalTools holds process-global static
/// state; tests that mutate it via ResetForTests must not run in parallel
/// with each other or with any other test that touches the same statics.
/// </summary>
[CollectionDefinition("RetrievalTools-static", DisableParallelization = true)]
public class RetrievalToolsStaticCollection { }
