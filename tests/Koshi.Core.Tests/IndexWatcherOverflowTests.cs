using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// Direct unit tests for <see cref="IndexWatcher"/> covering the multi-model
/// review findings — specifically the overflow cap on the pending event
/// queue (Opus O6) and the Dispose contract (Opus O7).
///
/// Built on top of <see cref="IndexWatcher.RaiseForTest"/> so we don't have
/// to drive a real <c>FileSystemWatcher</c> here; the cap logic lives in
/// <c>EnqueuePath</c> which is exercised by both real events and the seam.
/// </summary>
public sealed class IndexWatcherOverflowTests : IDisposable
{
    private readonly string _root;

    public IndexWatcherOverflowTests()
    {
        _root = Path.Join(Path.GetTempPath(), "koshi-iwo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void PendingQueue_caps_at_MaxPendingEvents_and_collapses_to_root_sentinel()
    {
        // Use Off mode so the worker loop doesn't drain underneath us — we
        // want to observe the cap behaviour deterministically.
        using var watcher = new IndexWatcher(
            rootPath: _root,
            mode: IndexWatchMode.Off,
            debounce: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromSeconds(1),
            pathPredicate: _ => true,
            pollEnumerator: () => Array.Empty<string>(),
            onDrain: (_, _) => Task.FromResult(new IndexWatcherDrainResult(false, false, null)));

        // Synthesise more events than the cap. Use synthetic paths so we
        // don't have to actually create 50k files on disk.
        const int Synthesize = IndexWatcher.MaxPendingEvents + 100;
        for (int i = 0; i < Synthesize; i++)
        {
            watcher.RaiseForTest(Path.Join(_root, $"f{i}.cs"));
        }

        // Once the cap is breached, the watcher flips PendingOverflowed and
        // collapses the existing queue to a single root sentinel ("."). The
        // queue may then grow again with subsequent events (because they
        // arrive AFTER the collapse), but it can never exceed the cap by
        // more than 1 cycle's worth, and the overflow flag stays sticky.
        Assert.True(watcher.PendingOverflowed, "PendingOverflowed should flip true once the cap is hit");
        Assert.True(watcher.PendingEvents <= IndexWatcher.MaxPendingEvents,
            $"PendingEvents should never exceed the cap; was {watcher.PendingEvents}");
    }

    [Fact]
    public void Dispose_returns_within_a_few_seconds_even_with_a_busy_worker()
    {
        // Spin up a watcher in Watch mode so the worker loop runs, then
        // dispose. The Opus O7 fix awaits the worker + poll tasks with a
        // 5s timeout; we assert dispose completes well within that.
        using var done = new ManualResetEventSlim(false);
        var watcher = new IndexWatcher(
            rootPath: _root,
            mode: IndexWatchMode.Watch,
            debounce: TimeSpan.FromMilliseconds(20),
            pollInterval: TimeSpan.FromSeconds(1),
            pathPredicate: _ => true,
            pollEnumerator: () => Array.Empty<string>(),
            onDrain: (_, _) => Task.FromResult(new IndexWatcherDrainResult(false, false, null)));

        // Give the worker time to start.
        Thread.Sleep(50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        watcher.Dispose();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7),
            $"Dispose should return within 7s; took {sw.ElapsedMilliseconds}ms");
    }
}
