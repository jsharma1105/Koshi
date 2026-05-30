using System.Collections.Concurrent;
using System.Security;

namespace Koshi.Mcp.Internal;

internal enum IndexWatchMode { Off, Watch, Poll }

/// <summary>
/// Outcome of a single drain cycle. <see cref="IndexWatcher"/> consumers
/// return one of these from their drain callback so the watcher can update
/// telemetry (TotalRebuilds, LastRebuildAt) and degraded state without
/// caring about the actual rebuild mechanics.
/// </summary>
internal readonly record struct IndexWatcherDrainResult(
    bool Rebuilt,
    bool Degraded,
    string? DegradedReason);

/// <summary>
/// Background watcher that keeps the BM25 retrieval index in sync with on-disk
/// source files. Issue #78 Gap D.
///
/// Two backends:
/// <list type="bullet">
/// <item><b>Watch</b> — <see cref="FileSystemWatcher"/> rooted at the indexed
/// directory; events are coalesced into a per-path set and drained after a
/// debounce window.</item>
/// <item><b>Poll</b> — periodic enumeration that diffs the previous file-state
/// map (relpath → (size, mtime)) against the current one, then feeds the
/// resulting changed-path set through the same drain callback. Used on
/// network mounts / containers where inotify is unreliable.</item>
/// </list>
///
/// <para>
/// This class is intentionally <i>only</i> about event aggregation + debounce
/// timing. It owns no chunks, retrievers, or snapshot persistence — those
/// belong to the consumer's drain callback. That keeps the watcher
/// independently testable and avoids re-introducing the static-state coupling
/// that bit us in the original sketch.
/// </para>
///
/// <para>
/// Final-state reconciliation: the pending queue is a path set, NOT a
/// (path → event-kind) map. Event kinds are unreliable because common editor
/// save patterns (temp file → rename → delete) interleave deletes with
/// creates within the debounce window. The drain callback decides what to do
/// per path by re-stat'ing the filesystem at drain time.
/// </para>
/// </summary>
internal sealed class IndexWatcher : IDisposable
{
    /// <summary>Root of the indexed directory (absolute, normalised).</summary>
    public string RootPath { get; }
    public IndexWatchMode Mode { get; }
    public TimeSpan Debounce { get; }
    public TimeSpan PollInterval { get; }

    public bool IsAttached { get; private set; }
    public string Status
    {
        get
        {
            if (Mode == IndexWatchMode.Off) return "disabled (mode=off)";
            if (!IsAttached) return $"unavailable ({_attachFailure ?? "not attached"})";
            if (_isDegraded) return $"degraded ({_degradedReason ?? "unknown"})";
            return "healthy";
        }
    }
    public bool IsDegraded => _isDegraded;
    public string? DegradedReason => _degradedReason;
    public int PendingEvents
    {
        get { lock (_pendingLock) return _pending.Count; }
    }
    public int TotalRebuilds => _totalRebuilds;
    public DateTimeOffset? LastEventAt => _lastEventAt;
    public DateTimeOffset? LastRebuildAt => _lastRebuildAt;

    private readonly Func<string, bool> _pathPredicate;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<IndexWatcherDrainResult>> _onDrain;
    private readonly Func<IEnumerable<string>> _pollEnumerator;
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingLock = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();
    private readonly FileSystemWatcher? _fsw;
    private readonly Task? _workerTask;
    private readonly Task? _pollTask;
    private readonly string? _attachFailure;
    private Dictionary<string, (long Size, long Mtime)>? _pollBaseline;
    private int _totalRebuilds;
    private bool _disposed;
    private bool _isDegraded;
    private string? _degradedReason;
    private DateTimeOffset? _lastEventAt;
    private DateTimeOffset? _lastRebuildAt;

    public IndexWatcher(
        string rootPath,
        IndexWatchMode mode,
        TimeSpan debounce,
        TimeSpan pollInterval,
        Func<string, bool> pathPredicate,
        Func<IEnumerable<string>> pollEnumerator,
        Func<IReadOnlyList<string>, CancellationToken, Task<IndexWatcherDrainResult>> onDrain)
    {
        RootPath = Path.GetFullPath(rootPath);
        Mode = mode;
        Debounce = debounce;
        PollInterval = pollInterval;
        _pathPredicate = pathPredicate ?? throw new ArgumentNullException(nameof(pathPredicate));
        _pollEnumerator = pollEnumerator ?? throw new ArgumentNullException(nameof(pollEnumerator));
        _onDrain = onDrain ?? throw new ArgumentNullException(nameof(onDrain));

        if (mode == IndexWatchMode.Off) return;

        _workerTask = Task.Run(WorkerLoopAsync);

        if (mode == IndexWatchMode.Watch)
        {
            try
            {
                _fsw = new FileSystemWatcher(RootPath, "*")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Size
                                 | NotifyFilters.CreationTime,
                    InternalBufferSize = 64 * 1024,
                };
                _fsw.Created += OnEvent;
                _fsw.Changed += OnEvent;
                _fsw.Deleted += OnEvent;
                _fsw.Renamed += OnRenamed;
                _fsw.Error += OnFswError;
                _fsw.EnableRaisingEvents = true;
                IsAttached = true;
            }
            catch (Exception ex) when (
                ex is ArgumentException or IOException or PathTooLongException
                    or FileNotFoundException or DirectoryNotFoundException
                    or UnauthorizedAccessException or SecurityException
                    or PlatformNotSupportedException)
            {
                _attachFailure = $"{ex.GetType().Name}: {ex.Message}";
                Console.Error.WriteLine(
                    $"[koshi] Index watcher could not attach to '{RootPath}' " +
                    $"({_attachFailure}); index will not auto-refresh.");
                try { _fsw?.Dispose(); }
                catch (Exception disposeEx) when (disposeEx is ObjectDisposedException or InvalidOperationException) { _ = disposeEx; }
                _fsw = null;
                IsAttached = false;
            }
        }
        else // Poll
        {
            try
            {
                _pollBaseline = SnapshotPollState();
                IsAttached = true;
                _pollTask = Task.Run(PollLoopAsync);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or SecurityException
                    or NotSupportedException or PathTooLongException or ArgumentException)
            {
                _attachFailure = $"{ex.GetType().Name}: {ex.Message}";
                Console.Error.WriteLine(
                    $"[koshi] Index watcher (poll) could not snapshot '{RootPath}' " +
                    $"({_attachFailure}); polling disabled.");
                IsAttached = false;
            }
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FullPath)) return;
        EnqueuePath(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Renamed is split into old+new — both paths need reconciliation:
        // the old path may need chunks removed; the new path may need chunks
        // added. Final-state stat at drain time decides.
        if (!string.IsNullOrEmpty(e.OldFullPath)) EnqueuePath(e.OldFullPath);
        if (!string.IsNullOrEmpty(e.FullPath)) EnqueuePath(e.FullPath);
    }

    private void OnFswError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow or backend failure — we have likely missed events.
        // Mark degraded and request a full reconciliation by enqueuing the
        // root sentinel; the drain callback knows to interpret root as
        // "re-enumerate the whole tree".
        var ex = e.GetException();
        SetDegraded($"FileSystemWatcher error: {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine(
            $"[koshi] Index watcher error: {ex.Message}. Scheduling full reconciliation.");
        EnqueuePath(RootPath);
    }

    /// <summary>
    /// Adds a path to the pending set after relativising and applying the
    /// path predicate. Test seam: tests call <see cref="RaiseForTest"/> which
    /// funnels here so the worker loop drives the drain identically.
    /// </summary>
    private void EnqueuePath(string fullPath)
    {
        if (_disposed) return;

        string rel;
        try
        {
            rel = Path.GetRelativePath(RootPath, fullPath);
        }
        catch (ArgumentException) { return; }

        // Normalize separators; treat any walk that escapes the root as out-of-scope.
        rel = rel.Replace('\\', '/');
        if (rel.StartsWith("../", StringComparison.Ordinal) || rel == "..") return;

        // Root-sentinel is a request for full reconciliation; bypass the predicate.
        var isRootSentinel = string.Equals(rel, ".", StringComparison.Ordinal)
                          || string.Equals(Path.GetFullPath(fullPath), RootPath, StringComparison.OrdinalIgnoreCase);

        // Directory events also bypass the file-oriented path predicate: an
        // existing directory has no extension and would otherwise be rejected
        // by SafeFileEnumerator.IsPathLikelyIndexed, but drain time still
        // needs the event so it can prefix-remove or subtree-enumerate.
        // Directories in excluded segments (.git, node_modules, etc.) are
        // still dropped here so noisy infra-dir events don't flood the queue.
        // For NON-EXISTING paths (file deletes AND directory deletes/renames)
        // we also bypass the file predicate so the drain can prefix-remove
        // chunks under the old directory name (rubber-duck #78 round-2 #2).
        // Drain idempotently handles unmatched paths so admitting extra
        // non-existing events is harmless.
        bool isDirectory;
        try { isDirectory = Directory.Exists(fullPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { isDirectory = false; }

        bool isFile;
        try { isFile = !isDirectory && File.Exists(fullPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { isFile = false; }

        bool exists = isDirectory || isFile;

        if (isDirectory && SafeFileEnumerator.IsExcludedDirectoryPath(fullPath)) return;
        if (!exists && SafeFileEnumerator.IsExcludedDirectoryPath(fullPath)) return;
        if (!isRootSentinel && exists && !isDirectory && !_pathPredicate(fullPath)) return;

        bool added;
        lock (_pendingLock)
        {
            added = _pending.Add(rel);
        }
        if (added)
        {
            _lastEventAt = DateTimeOffset.UtcNow;
            try { _signal.Release(); }
            catch (ObjectDisposedException) { /* watcher disposed mid-event */ }
            catch (SemaphoreFullException) { /* signal already at int.MaxValue, fine */ }
        }
    }

    /// <summary>Test seam — synthesises a watcher event without touching the filesystem.</summary>
    internal void RaiseForTest(string fullPath) => EnqueuePath(fullPath);

    /// <summary>
    /// Test seam — runs ONE drain cycle synchronously (with no debounce wait)
    /// and returns when the drain callback completes. Lets tests assert
    /// "after I raise events X, Y, Z, the next drain sees exactly those paths"
    /// without sleeping for the debounce timer.
    /// </summary>
    internal async Task<int> DrainNowForTest(CancellationToken ct = default)
    {
        // Wait for any in-flight worker iteration to finish, then run one drain.
        return await DrainOnceAsync(ct).ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Wait for the first event.
                await _signal.WaitAsync(ct).ConfigureAwait(false);

                // Debounce: drain the signal while waiting Debounce ms without
                // new signals. Any signal arriving during the wait extends it.
                while (true)
                {
                    try
                    {
                        // Drain accumulated signals so we don't loop forever
                        // emptying them after quiescence.
                        while (_signal.Wait(0)) { /* drain */ }

                        // Wait up to Debounce; any new event releases the
                        // semaphore and we loop again, extending the window.
                        var more = await _signal.WaitAsync(Debounce, ct).ConfigureAwait(false);
                        if (!more) break; // quiescence reached
                    }
                    catch (OperationCanceledException) { return; }
                }

                await DrainOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[koshi] Index watcher worker crashed: {ex.GetType().Name}: {ex.Message}");
            SetDegraded($"worker crash: {ex.GetType().Name}");
        }
    }

    private async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        List<string> batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0) return 0;
            batch = _pending.ToList();
            _pending.Clear();
        }

        try
        {
            var result = await _onDrain(batch, ct).ConfigureAwait(false);
            if (result.Rebuilt)
            {
                Interlocked.Increment(ref _totalRebuilds);
                _lastRebuildAt = DateTimeOffset.UtcNow;
            }
            if (result.Degraded)
                SetDegraded(result.DegradedReason ?? "unspecified");
            else
                ClearDegraded();
            return batch.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[koshi] Index watcher drain failed: {ex.GetType().Name}: {ex.Message}");
            SetDegraded($"drain crash: {ex.GetType().Name}");
            return batch.Count;
        }
    }

    private async Task PollLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                Dictionary<string, (long Size, long Mtime)> current;
                try { current = SnapshotPollState(); }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or SecurityException
                        or NotSupportedException or PathTooLongException or ArgumentException)
                {
                    SetDegraded($"poll snapshot failed: {ex.GetType().Name}");
                    continue;
                }

                var baseline = _pollBaseline ?? new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (path, state) in current)
                {
                    if (!baseline.TryGetValue(path, out var prev) || prev.Size != state.Size || prev.Mtime != state.Mtime)
                        EnqueuePath(path);
                }
                foreach (var path in baseline.Keys.Where(p => !current.ContainsKey(p)))
                {
                    EnqueuePath(path);
                }
                _pollBaseline = current;
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private Dictionary<string, (long Size, long Mtime)> SnapshotPollState()
    {
        var map = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _pollEnumerator())
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                map[path] = (info.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or SecurityException
                    or NotSupportedException or PathTooLongException or ArgumentException)
            {
                _ = ex; // skip unstatable file
            }
        }
        return map;
    }

    private void SetDegraded(string reason)
    {
        _isDegraded = true;
        _degradedReason = reason;
    }

    private void ClearDegraded()
    {
        _isDegraded = false;
        _degradedReason = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { /* best effort */ }

        if (_fsw is not null)
        {
            try
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Created -= OnEvent;
                _fsw.Changed -= OnEvent;
                _fsw.Deleted -= OnEvent;
                _fsw.Renamed -= OnRenamed;
                _fsw.Error -= OnFswError;
                _fsw.Dispose();
            }
            catch { /* best effort */ }
        }

        try { _signal.Dispose(); } catch { /* best effort */ }
        try { _cts.Dispose(); } catch { /* best effort */ }
    }

    /// <summary>
    /// Parses the <c>KOSHI_INDEX_WATCH</c> env var into a mode.
    /// <c>on</c>/<c>true</c>/<c>1</c>/<c>watch</c> → Watch.
    /// <c>poll</c> → Poll. Empty / <c>off</c> / <c>false</c> / <c>0</c> / unknown → Off.
    /// </summary>
    public static IndexWatchMode ParseModeFromEnv(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return IndexWatchMode.Off;
        return raw.Trim().ToLowerInvariant() switch
        {
            "on" or "true" or "1" or "watch" or "yes" or "enabled" => IndexWatchMode.Watch,
            "poll" or "polling" => IndexWatchMode.Poll,
            _ => IndexWatchMode.Off,
        };
    }
}
