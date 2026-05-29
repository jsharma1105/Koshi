namespace Koshi.Mcp.Internal;

using System.Diagnostics;

/// <summary>
/// Throttled progress reporter for <c>koshi_index_directory</c> (#69).
///
/// Pure logic, no I/O — emission is delegated to <see cref="IIndexProgressSink"/>
/// implementations passed in by the caller so the reporter stays trivially
/// unit-testable. The reporter guarantees:
///
/// <list type="bullet">
///   <item>The final event (<c>processed == total</c>) is ALWAYS emitted.</item>
///   <item>Otherwise, at most one emission per <see cref="ThrottleMs"/>
///         milliseconds (default 500 ms — keeps stderr ≤ 2 lines/sec).</item>
///   <item>Sink exceptions are swallowed so a single bad sink cannot abort
///         the indexer's hot loop.</item>
/// </list>
///
/// Wall-clock readings are obtained via an injectable <see cref="Func{T}"/>
/// (monotonic milliseconds) so tests can advance time deterministically
/// without <c>Thread.Sleep</c>.
/// </summary>
internal sealed class IndexProgressReporter
{
    public const int ThrottleMs = 500;

    private readonly int _total;
    private readonly IReadOnlyList<IIndexProgressSink> _sinks;
    private readonly Func<long> _nowMs;
    private readonly long _startMs;

    private long _lastEmitMs;
    private bool _everEmitted;

    public IndexProgressReporter(
        int total,
        IReadOnlyList<IIndexProgressSink> sinks,
        Func<long>? nowMs = null)
    {
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
        _total = total;
        _sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));
        _nowMs = nowMs ?? DefaultNowMs;
        _startMs = _nowMs();
        _lastEmitMs = _startMs;
    }

    private static long DefaultNowMs() => Environment.TickCount64;

    /// <summary>
    /// Report progress after processing one file. <paramref name="processed"/>
    /// is the 1-based count of files visited so far (including any that were
    /// skipped because they were empty or unreadable). Always call this exactly
    /// once per file in a <c>finally</c> block so the throttle's invariant
    /// holds even when chunking throws.
    /// </summary>
    public async ValueTask ReportAsync(
        int processed,
        string relativePath,
        int chunkCount,
        CancellationToken cancellationToken = default)
    {
        if (_sinks.Count == 0) return;

        var nowMs = _nowMs();
        var elapsedSinceLastMs = nowMs - _lastEmitMs;
        bool finalTrigger = processed >= _total;
        bool timeTrigger = elapsedSinceLastMs >= ThrottleMs;

        // Edge: brand-new reporter, first file is slow — emit immediately so
        // the user sees *something* instead of silence.
        bool firstEmitTrigger = !_everEmitted && processed >= 1 && (nowMs - _startMs) >= ThrottleMs;

        if (!(finalTrigger || timeTrigger || firstEmitTrigger)) return;

        _lastEmitMs = nowMs;
        _everEmitted = true;

        var elapsedSinceStart = TimeSpan.FromMilliseconds(nowMs - _startMs);
        var ev = new IndexProgressEvent(
            Processed: processed,
            Total: _total,
            RelativePath: relativePath,
            ChunkCount: chunkCount,
            Elapsed: elapsedSinceStart,
            IsFinal: finalTrigger);

        foreach (var sink in _sinks)
        {
            await SafeWriteAsync(sink, ev, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Emit an initial "enumerating" event so the user gets a "we're alive"
    /// signal before the file enumeration completes (which can itself be slow
    /// on network mounts). Total is reported as 0 so MCP clients render an
    /// indeterminate spinner rather than a phantom progress bar.
    /// </summary>
    public async ValueTask ReportEnumeratingAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (_sinks.Count == 0) return;

        var ev = new IndexProgressEvent(
            Processed: 0,
            Total: 0,
            RelativePath: path,
            ChunkCount: 0,
            Elapsed: TimeSpan.FromMilliseconds(_nowMs() - _startMs),
            IsFinal: false,
            Stage: IndexProgressStage.Enumerating);

        foreach (var sink in _sinks)
        {
            await SafeWriteAsync(sink, ev, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Per spec: never let a sink failure abort indexing. The MCP sink may,
    /// for example, throw on a closed transport during graceful shutdown.
    /// Cancellation is intentionally re-thrown so the caller's cancellation
    /// token contract is preserved.
    /// </summary>
    private static async ValueTask SafeWriteAsync(
        IIndexProgressSink sink,
        IndexProgressEvent ev,
        CancellationToken cancellationToken)
    {
        try
        {
            await sink.WriteAsync(ev, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[koshi] index progress sink {sink.GetType().Name} failed: {ex.Message}");
        }
    }
}

/// <summary>
/// One progress update. Records intentionally use a flat shape so each sink
/// can render whichever fields it cares about.
/// </summary>
internal readonly record struct IndexProgressEvent(
    int Processed,
    int Total,
    string RelativePath,
    int ChunkCount,
    TimeSpan Elapsed,
    bool IsFinal,
    IndexProgressStage Stage = IndexProgressStage.Indexing)
{
    /// <summary>
    /// Percent complete. Uses <c>Math.Floor</c> for non-final events so a
    /// rounded "100%" never appears before the final event fires (rubber-duck
    /// catch — Math.Round(99.7) would otherwise show 100% mid-flight).
    /// </summary>
    public int PercentComplete
    {
        get
        {
            if (IsFinal || (Total > 0 && Processed >= Total) || (Total == 0 && Processed == 0)) return 100;
            if (Total <= 0) return 0;
            return (int)Math.Floor(100.0 * Processed / Total);
        }
    }

    /// <summary>
    /// Linear extrapolation of remaining time. Returns <see cref="TimeSpan.Zero"/>
    /// when an ETA cannot be meaningfully computed (no progress yet, too little
    /// elapsed time, or already complete).
    /// </summary>
    public TimeSpan EstimatedRemaining
    {
        get
        {
            if (IsFinal || Processed <= 0 || Total <= 0 || Elapsed.TotalMilliseconds < 200) return TimeSpan.Zero;
            var totalEstMs = Elapsed.TotalMilliseconds * Total / Processed;
            var remainingMs = totalEstMs - Elapsed.TotalMilliseconds;
            return remainingMs <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remainingMs);
        }
    }
}

internal enum IndexProgressStage
{
    Enumerating,
    Indexing,
}

/// <summary>Sink interface — caller wires up stderr and/or MCP notifications.</summary>
internal interface IIndexProgressSink
{
    ValueTask WriteAsync(IndexProgressEvent ev, CancellationToken cancellationToken);
}
