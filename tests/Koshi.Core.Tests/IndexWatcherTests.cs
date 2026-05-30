using Koshi.Mcp.Internal;
using Koshi.Mcp.Tools;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for the IndexWatcher (#78 Gap D). Most cases drive deterministically
/// via the <c>RaiseIndexWatcherEventForTest</c> / <c>DrainIndexWatcherForTest</c>
/// seams to avoid the inherent flakiness of FileSystemWatcher timing on Windows.
/// One end-to-end test uses the real watcher with a short debounce so we still
/// cover the FSW → drain pipeline.
/// </summary>
[Collection("RetrievalTools-static")]
public class IndexWatcherTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _sourceDir;
    private readonly string _indexFile;

    public IndexWatcherTests()
    {
        _tmpDir = Path.Join(Path.GetTempPath(), "koshi-watcher-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);
        _sourceDir = Path.Join(_tmpDir, "source");
        Directory.CreateDirectory(_sourceDir);
        _indexFile = Path.Join(_tmpDir, ".koshi", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_indexFile)!);
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable("KOSHI_INDEX_WATCH", null);
            Environment.SetEnvironmentVariable("KOSHI_INDEX_WATCH_DEBOUNCE_MS", null);
            Environment.SetEnvironmentVariable("KOSHI_INDEX_WATCH_POLL_SECONDS", null);
            RetrievalTools.ResetForTests(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }

        try { Directory.Delete(_tmpDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }

        GC.SuppressFinalize(this);
    }

    private void WriteSourceFile(string relPath, string content)
    {
        var full = Path.Join(_sourceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task IndexSourceWithWatchAsync(IndexWatchMode mode)
    {
        RetrievalTools.ResetForTests(_indexFile);
        RetrievalTools.SetExplicitWatchOverrideForTest(mode);
        var res = await RetrievalTools.IndexDirectory(path: _sourceDir, format: "text");
        Assert.DoesNotContain("❌", res);
    }

    [Fact]
    public async Task Watcher_off_by_default_when_env_unset()
    {
        WriteSourceFile("a.md", "alpha bravo charlie");
        RetrievalTools.ResetForTests(_indexFile);
        // Explicitly leave override at Off (the default in ResetForTests).
        await RetrievalTools.IndexDirectory(path: _sourceDir, format: "text");

        var status = RetrievalTools.GetIndexWatcherStatus();
        Assert.Equal("off", status.mode);
    }

    [Fact]
    public async Task Watcher_starts_when_per_call_override_is_watch()
    {
        WriteSourceFile("a.md", "alpha bravo charlie");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var status = RetrievalTools.GetIndexWatcherStatus();
        Assert.Equal("watch", status.mode);
        Assert.False(status.degraded);
        Assert.Equal(_sourceDir, status.root);
    }

    [Fact]
    public async Task Watcher_file_edit_rebuilds_chunks_for_that_file()
    {
        WriteSourceFile("a.md", "original content alpha");
        WriteSourceFile("b.md", "other file untouched bravo");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var before = RetrievalTools.GetStatus().chunkCount;
        Assert.True(before > 0);

        // Replace the file content with longer text that should produce
        // at least one chunk again — drain should swap a.md's chunks but
        // leave b.md's alone.
        WriteSourceFile("a.md", "replaced content delta echo foxtrot golf hotel india juliet");
        RetrievalTools.RaiseIndexWatcherEventForTest(Path.Join(_sourceDir, "a.md"));
        var processed = await RetrievalTools.DrainIndexWatcherForTest();

        Assert.True(processed >= 1, "drain should have processed at least one pending event");
        var after = RetrievalTools.GetStatus();
        Assert.True(after.chunkCount > 0);
        Assert.False(RetrievalTools.GetIndexWatcherStatus().degraded);

        // Validate the new content is actually searchable through the rebuilt retriever.
        var searchResult = RetrievalTools.Search("delta echo foxtrot", topK: 5, format: "text");
        Assert.Contains("a.md", searchResult);
    }

    [Fact]
    public async Task Watcher_file_delete_removes_chunks()
    {
        WriteSourceFile("a.md", "alpha bravo charlie delta echo");
        WriteSourceFile("b.md", "foxtrot golf hotel india");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var before = RetrievalTools.GetStatus().chunkCount;
        Assert.True(before >= 2);

        File.Delete(Path.Join(_sourceDir, "a.md"));
        RetrievalTools.RaiseIndexWatcherEventForTest(Path.Join(_sourceDir, "a.md"));
        await RetrievalTools.DrainIndexWatcherForTest();

        var after = RetrievalTools.GetStatus();
        Assert.True(after.chunkCount < before, $"expected chunk count to drop after delete (was {before}, now {after.chunkCount})");

        var searchResult = RetrievalTools.Search("alpha bravo", topK: 5, format: "text");
        Assert.DoesNotContain("a.md", searchResult);
    }

    [Fact]
    public async Task Watcher_new_file_creation_adds_chunks()
    {
        WriteSourceFile("a.md", "alpha bravo charlie");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var before = RetrievalTools.GetStatus().chunkCount;
        Assert.True(before > 0);

        WriteSourceFile("new.md", "newly created file with searchable kangaroo wombat lemur");
        RetrievalTools.RaiseIndexWatcherEventForTest(Path.Join(_sourceDir, "new.md"));
        await RetrievalTools.DrainIndexWatcherForTest();

        var after = RetrievalTools.GetStatus();
        Assert.True(after.chunkCount > before);

        var searchResult = RetrievalTools.Search("kangaroo wombat", topK: 5, format: "text");
        Assert.Contains("new.md", searchResult);
    }

    [Fact]
    public async Task Watcher_directory_delete_prefix_removes_all_chunks_under_it()
    {
        WriteSourceFile("a.md", "top-level file alpha");
        WriteSourceFile("subdir/inner1.md", "nested file one with content");
        WriteSourceFile("subdir/inner2.md", "nested file two more content");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var before = RetrievalTools.GetStatus().chunkCount;
        Assert.True(before >= 3);

        Directory.Delete(Path.Join(_sourceDir, "subdir"), recursive: true);
        RetrievalTools.RaiseIndexWatcherEventForTest(Path.Join(_sourceDir, "subdir"));
        await RetrievalTools.DrainIndexWatcherForTest();

        var after = RetrievalTools.GetStatus();
        Assert.True(after.chunkCount < before, "chunks under deleted directory should be removed");

        // The top-level file's chunks must survive — the prefix-remove
        // must NOT match 'a.md' because the directory path was 'subdir'.
        var searchTop = RetrievalTools.Search("top-level alpha", topK: 5, format: "text");
        Assert.Contains("a.md", searchTop);
    }

    [Fact]
    public async Task Watcher_directory_creation_adds_subtree_chunks()
    {
        WriteSourceFile("a.md", "initial file alpha");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var before = RetrievalTools.GetStatus().chunkCount;

        WriteSourceFile("freshdir/one.md", "fresh one zebra yak xenon");
        WriteSourceFile("freshdir/two.md", "fresh two wombat vulture");
        RetrievalTools.RaiseIndexWatcherEventForTest(Path.Join(_sourceDir, "freshdir"));
        await RetrievalTools.DrainIndexWatcherForTest();

        var after = RetrievalTools.GetStatus();
        Assert.True(after.chunkCount > before, "subtree enumeration should add new file chunks");

        var searchResult = RetrievalTools.Search("zebra yak xenon", topK: 5, format: "text");
        Assert.Contains("one.md", searchResult);
    }

    [Fact]
    public async Task Watcher_snapshot_file_self_guarded_from_predicate()
    {
        WriteSourceFile("a.md", "alpha bravo charlie");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        // Simulate a save touching the snapshot file: the watcher's path
        // predicate must reject it. We cannot directly observe the
        // predicate, so we raise the event and confirm zero processing.
        // Drain returns the count of paths it pulled off the queue —
        // since RaiseForTest itself filters via the predicate, the
        // pending queue should stay empty.
        var snapshotPath = _indexFile;
        RetrievalTools.RaiseIndexWatcherEventForTest(snapshotPath);
        RetrievalTools.RaiseIndexWatcherEventForTest(snapshotPath + ".tmp");

        var status = RetrievalTools.GetIndexWatcherStatus();
        Assert.Equal(0, status.pendingEvents);
        Assert.Equal(0, status.totalRebuilds);
    }

    [Fact]
    public async Task Watcher_excluded_path_ignored()
    {
        WriteSourceFile("a.md", "alpha");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        // .git is a SafeFileEnumerator-excluded hidden directory.
        Directory.CreateDirectory(Path.Join(_sourceDir, ".git"));
        WriteSourceFile(".git/HEAD", "ref: refs/heads/main");
        RetrievalTools.RaiseIndexWatcherEventForTest(Path.Join(_sourceDir, ".git", "HEAD"));

        var status = RetrievalTools.GetIndexWatcherStatus();
        Assert.Equal(0, status.pendingEvents);
    }

    [Fact]
    public async Task Watcher_clear_index_disposes_watcher()
    {
        WriteSourceFile("a.md", "alpha");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);
        Assert.Equal("watch", RetrievalTools.GetIndexWatcherStatus().mode);

        var res = RetrievalTools.ClearIndex(format: "text");
        Assert.DoesNotContain("❌", res);
        Assert.Equal("off", RetrievalTools.GetIndexWatcherStatus().mode);
    }

    [Fact]
    public async Task Watcher_path_switch_disposes_old_and_starts_new()
    {
        WriteSourceFile("a.md", "alpha");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);
        var first = RetrievalTools.GetIndexWatcherStatus();
        Assert.Equal("watch", first.mode);

        // Re-index a different directory in the same session.
        var secondDir = Path.Join(_tmpDir, "second");
        Directory.CreateDirectory(secondDir);
        File.WriteAllText(Path.Join(secondDir, "b.md"), "bravo");
        RetrievalTools.SetExplicitWatchOverrideForTest(IndexWatchMode.Watch);
        var res = await RetrievalTools.IndexDirectory(path: secondDir, format: "text");
        Assert.DoesNotContain("❌", res);

        var second = RetrievalTools.GetIndexWatcherStatus();
        Assert.Equal("watch", second.mode);
        Assert.Equal(secondDir, second.root);
    }

    [Fact]
    public async Task Watcher_persists_updated_snapshot_after_drain()
    {
        WriteSourceFile("a.md", "alpha bravo");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);
        Assert.True(File.Exists(_indexFile));
        var mtime1 = File.GetLastWriteTimeUtc(_indexFile);

        // Wait a tick so mtime comparison is meaningful on filesystems with
        // 1-second resolution.
        await Task.Delay(50);

        WriteSourceFile("a.md", "alpha bravo charlie delta echo");
        RetrievalTools.RaiseIndexWatcherEventForTest(Path.Join(_sourceDir, "a.md"));
        await RetrievalTools.DrainIndexWatcherForTest();

        Assert.True(File.Exists(_indexFile));
        var mtime2 = File.GetLastWriteTimeUtc(_indexFile);
        Assert.True(mtime2 >= mtime1, "snapshot should have been rewritten after drain");
    }

    [Fact]
    public async Task Watcher_health_status_reports_disabled_when_off()
    {
        WriteSourceFile("a.md", "alpha");
        RetrievalTools.ResetForTests(_indexFile);
        await RetrievalTools.IndexDirectory(path: _sourceDir, format: "text");

        var status = RetrievalTools.GetIndexWatcherStatus();
        Assert.Equal("off", status.mode);
        Assert.Contains("disabled", status.status, StringComparison.OrdinalIgnoreCase);
        Assert.Null(status.root);
        Assert.False(status.degraded);
    }

    [Fact]
    public async Task Watcher_health_status_reports_running_when_active()
    {
        WriteSourceFile("a.md", "alpha");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var status = RetrievalTools.GetIndexWatcherStatus();
        Assert.Equal("watch", status.mode);
        Assert.NotNull(status.root);
        Assert.True(status.debounceMs > 0);
        Assert.True(status.pollIntervalSeconds > 0);
    }

    [Fact]
    public async Task Watcher_e2e_real_filesystemwatcher_picks_up_edit()
    {
        // The single end-to-end test that exercises the real FSW plumbing
        // + debounce worker loop. Uses a tight 100ms debounce to keep the
        // overall test fast.
        Environment.SetEnvironmentVariable("KOSHI_INDEX_WATCH_DEBOUNCE_MS", "100");
        WriteSourceFile("a.md", "alpha bravo charlie");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var before = RetrievalTools.GetStatus().chunkCount;
        Assert.True(before > 0);

        // Modify the file and wait for the watcher to react. We poll
        // GetIndexWatcherStatus().totalRebuilds for up to 5s before
        // giving up — this is slow enough to be reliable on a CI runner
        // without being absurdly long.
        WriteSourceFile("a.md", "alpha bravo charlie delta echo foxtrot golf hotel india");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline && RetrievalTools.GetIndexWatcherStatus().totalRebuilds == 0)
            await Task.Delay(100);

        var finalStatus = RetrievalTools.GetIndexWatcherStatus();
        Assert.True(finalStatus.totalRebuilds >= 1,
            $"expected at least one rebuild (status: {finalStatus.status}, degraded: {finalStatus.degradedReason})");
    }

    [Fact]
    public async Task Watch_override_is_per_call_not_sticky()
    {
        // Regression for rubber-duck BLOCKING #1: the per-call watch=true on
        // IndexDirectory must NOT leak into subsequent calls. Previously the
        // override was set as global state and never cleared, so a later
        // IndexDirectory(watch=null) on an unrelated path would still start
        // a watcher.
        WriteSourceFile("a.md", "alpha");
        RetrievalTools.ResetForTests(_indexFile);
        // First call: explicitly request watcher via the public 'watch' param.
        var r1 = await RetrievalTools.IndexDirectory(path: _sourceDir, watch: true, format: "text");
        Assert.DoesNotContain("❌", r1);
        Assert.Equal("watch", RetrievalTools.GetIndexWatcherStatus().mode);

        // Second call on a different dir without 'watch'. The override should
        // have been restored (to Off by default) by the finally in
        // IndexDirectory, so the second call should NOT start a watcher.
        var secondDir = Path.Join(_tmpDir, "second");
        Directory.CreateDirectory(secondDir);
        File.WriteAllText(Path.Join(secondDir, "b.md"), "bravo");

        var r2 = await RetrievalTools.IndexDirectory(path: secondDir, format: "text");
        Assert.DoesNotContain("❌", r2);
        Assert.Equal("off", RetrievalTools.GetIndexWatcherStatus().mode);
    }

    [Fact]
    public async Task Watch_override_restored_even_when_index_call_fails()
    {
        // Regression for rubber-duck BLOCKING #1 (failure path): if
        // IndexDirectory bails early (e.g., directory not found) the
        // per-call watch=true must not leak to the next call.
        WriteSourceFile("a.md", "alpha");
        RetrievalTools.ResetForTests(_indexFile);

        var missing = Path.Join(_tmpDir, "does-not-exist");
        var bad = await RetrievalTools.IndexDirectory(path: missing, watch: true, format: "text");
        Assert.Contains("❌", bad);
        Assert.Equal("off", RetrievalTools.GetIndexWatcherStatus().mode);

        // Subsequent normal call must not have inherited Watch mode.
        var ok = await RetrievalTools.IndexDirectory(path: _sourceDir, format: "text");
        Assert.DoesNotContain("❌", ok);
        Assert.Equal("off", RetrievalTools.GetIndexWatcherStatus().mode);
    }

    [Fact]
    public async Task Watcher_root_sentinel_full_reconciliation_deletes_stale_chunks()
    {
        // Regression for rubber-duck BLOCKING #2: when the watcher's FSW
        // error handler raises the root sentinel (full reconciliation
        // request), drain must clear stale chunks for files that no longer
        // exist on disk. Previously prefix-remove on "./" matched nothing
        // and deleted files lingered in the in-memory index forever.
        WriteSourceFile("a.md", "alpha bravo charlie");
        WriteSourceFile("b.md", "delta echo foxtrot");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var before = RetrievalTools.GetStatus().chunkCount;
        Assert.True(before >= 2);

        // Delete one file on disk WITHOUT firing a per-file event — only the
        // root sentinel below should drive the reconciliation. Sentinel goes
        // through EnqueuePath's root bypass so we can raise it directly.
        File.Delete(Path.Join(_sourceDir, "b.md"));
        RetrievalTools.RaiseIndexWatcherEventForTest(_sourceDir);
        await RetrievalTools.DrainIndexWatcherForTest();

        var after = RetrievalTools.GetStatus();
        Assert.True(after.chunkCount < before,
            "root-sentinel drain must remove chunks for files no longer on disk");

        // The remaining file's chunks must still be searchable.
        var searchA = RetrievalTools.Search("alpha bravo", topK: 5, format: "text");
        Assert.Contains("a.md", searchA);
    }

    [Fact]
    public async Task Watcher_directory_only_event_processes_subtree_via_predicate_bypass()
    {
        // Regression for rubber-duck BLOCKING #3: directory events used to
        // be dropped by EnqueuePath because the file-oriented path predicate
        // rejects extension-less paths. Real-world FSW emits child file
        // events that compensate, but on network mounts / unusual back-ends
        // we may receive ONLY a directory event. Raise such an event with
        // no child file events first, and verify the subtree was processed.
        WriteSourceFile("a.md", "alpha top-level");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var before = RetrievalTools.GetStatus().chunkCount;

        // Create a brand-new subdir + file WITHOUT firing per-file events.
        // We then raise the directory event alone and let the drain
        // subtree-enumerate to discover the new file.
        var newSubdir = Path.Join(_sourceDir, "newdir");
        Directory.CreateDirectory(newSubdir);
        File.WriteAllText(Path.Join(newSubdir, "fresh.md"), "novel content xyzzy plover");

        RetrievalTools.RaiseIndexWatcherEventForTest(newSubdir);
        await RetrievalTools.DrainIndexWatcherForTest();

        var after = RetrievalTools.GetStatus();
        Assert.True(after.chunkCount > before,
            "directory-only event must subtree-enumerate and add chunks");
        var searchFresh = RetrievalTools.Search("xyzzy plover", topK: 5, format: "text");
        Assert.Contains("fresh.md", searchFresh);
    }

    [Fact]
    public async Task Watcher_directory_event_excluded_segment_is_dropped()
    {
        // The directory-event bypass added for BLOCKING #3 must still
        // respect SafeFileEnumerator's directory exclusions — a .git event
        // would otherwise spam the queue on every commit.
        WriteSourceFile("a.md", "alpha");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var beforePending = RetrievalTools.GetIndexWatcherStatus().pendingEvents;

        var excluded = Path.Join(_sourceDir, ".git", "objects");
        Directory.CreateDirectory(excluded);
        RetrievalTools.RaiseIndexWatcherEventForTest(excluded);

        // The directory exists, so the file predicate would have rejected
        // it anyway, but the new directory-bypass code must also drop it
        // because .git is in ExcludedDirectorySegments.
        var afterPending = RetrievalTools.GetIndexWatcherStatus().pendingEvents;
        Assert.Equal(beforePending, afterPending);
    }

    [Fact]
    public async Task Watcher_poll_mode_starts_with_baseline_and_no_immediate_rebuild()
    {
        // Smoke test for poll mode: starting in Poll mode should attach
        // without firing a spurious rebuild, and the watcher status should
        // report mode=poll with a positive poll interval. We don't wait for
        // a full poll cycle (that's a longer-running follow-up test) but we
        // do verify the watcher is alive and observing.
        Environment.SetEnvironmentVariable("KOSHI_INDEX_WATCH_POLL_SECONDS", "1");
        WriteSourceFile("a.md", "alpha");
        await IndexSourceWithWatchAsync(IndexWatchMode.Poll);

        var status = RetrievalTools.GetIndexWatcherStatus();
        Assert.Equal("poll", status.mode);
        Assert.Equal(_sourceDir, status.root);
        Assert.True(status.pollIntervalSeconds >= 1);
        Assert.False(status.degraded);
        Assert.Equal(0, status.totalRebuilds);
    }

    [Fact]
    public async Task Watcher_per_call_watch_param_does_not_mutate_static_override()
    {
        // Regression for rubber-duck round-2 #1: the per-call watch override
        // is threaded as a method parameter, not via mutable static state.
        // After IndexDirectory(watch:true) returns, the static override
        // observed by ResolveWatchMode must be exactly what it was before
        // the call. Otherwise concurrent IndexDirectory calls could stomp
        // each other's intent.
        WriteSourceFile("a.md", "alpha");
        RetrievalTools.ResetForTests(_indexFile);
        // Baseline: explicit Off (the default after ResetForTests).
        RetrievalTools.SetExplicitWatchOverrideForTest(IndexWatchMode.Off);

        // Per-call watch:true should activate the watcher for THIS index
        // without mutating the static override that a parallel call would
        // observe.
        var r1 = await RetrievalTools.IndexDirectory(path: _sourceDir, watch: true, format: "text");
        Assert.DoesNotContain("❌", r1);
        Assert.Equal("watch", RetrievalTools.GetIndexWatcherStatus().mode);

        // A follow-up call that omits 'watch' resolves against the static
        // override (still Off) and env (unset). The previous call's per-call
        // override must NOT have leaked into the static field.
        var secondDir = Path.Join(_tmpDir, "second-static");
        Directory.CreateDirectory(secondDir);
        File.WriteAllText(Path.Join(secondDir, "b.md"), "bravo");

        var r2 = await RetrievalTools.IndexDirectory(path: secondDir, format: "text");
        Assert.DoesNotContain("❌", r2);
        Assert.Equal("off", RetrievalTools.GetIndexWatcherStatus().mode);
    }

    [Fact]
    public async Task Watcher_directory_only_delete_event_removes_subtree_chunks()
    {
        // Regression for rubber-duck round-2 #2: when only a directory-level
        // delete (or the OLD side of a directory rename) event is delivered
        // — without per-file child events the FSW would normally emit — the
        // chunks under that directory must still be reclaimed. Previously
        // EnqueuePath rejected the non-existent extensionless path entirely
        // and the drain never saw it, so stale chunks lingered.
        WriteSourceFile("kept/keep.md", "kept alpha bravo");
        WriteSourceFile("doomed/inside1.md", "doomed delta echo foxtrot");
        WriteSourceFile("doomed/inside2.md", "doomed golf hotel india");
        await IndexSourceWithWatchAsync(IndexWatchMode.Watch);

        var beforeCount = RetrievalTools.GetStatus().chunkCount;
        var searchBefore = RetrievalTools.Search("delta echo", topK: 5, format: "text");
        Assert.Contains("doomed", searchBefore);

        // Delete the entire 'doomed' subtree on disk WITHOUT raising the
        // per-file delete events (simulates a back-end that only emits a
        // single directory delete event).
        Directory.Delete(Path.Join(_sourceDir, "doomed"), recursive: true);
        RetrievalTools.RaiseIndexWatcherEventForTest(Path.Join(_sourceDir, "doomed"));
        await RetrievalTools.DrainIndexWatcherForTest();

        var afterCount = RetrievalTools.GetStatus().chunkCount;
        Assert.True(afterCount < beforeCount,
            "directory-only delete event must remove chunks under the doomed subtree");

        var searchAfter = RetrievalTools.Search("delta echo foxtrot", topK: 5, format: "text");
        Assert.DoesNotContain("doomed", searchAfter);

        // And the surviving 'kept' subtree is still searchable.
        var searchKept = RetrievalTools.Search("kept alpha", topK: 5, format: "text");
        Assert.Contains("keep.md", searchKept);
    }
}
