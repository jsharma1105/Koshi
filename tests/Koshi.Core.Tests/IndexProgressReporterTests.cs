using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// Unit tests for <see cref="IndexProgressReporter"/> (#69). The reporter
/// must:
///   • Always emit the final event (processed == total).
///   • Throttle intermediate events to one per 500 ms.
///   • Surface the first non-trivial event even before 500 ms have elapsed
///     so the user sees "something is happening" on slow-first-file repos.
///   • Be tolerant of sink exceptions (one bad sink must not abort the loop).
///   • Use Math.Floor (not Round) for non-final percent so 99.7% never reads
///     as 100% mid-flight.
/// All tests use a fake monotonic clock (Func&lt;long&gt;) so they run in
/// milliseconds without Thread.Sleep.
/// </summary>
public sealed class IndexProgressReporterTests
{
    private sealed class CapturingSink : IIndexProgressSink
    {
        public List<IndexProgressEvent> Events { get; } = new();
        public ValueTask WriteAsync(IndexProgressEvent ev, CancellationToken ct)
        {
            Events.Add(ev);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink(Exception ex) : IIndexProgressSink
    {
        public int CallCount { get; private set; }
        public ValueTask WriteAsync(IndexProgressEvent ev, CancellationToken ct)
        {
            CallCount++;
            throw ex;
        }
    }

    private sealed class FakeClock
    {
        public long NowMs;
        public long Read() => NowMs;
        public void Advance(long ms) => NowMs += ms;
    }

    [Fact]
    public async Task Final_event_is_always_emitted()
    {
        var clock = new FakeClock();
        var sink = new CapturingSink();
        var reporter = new IndexProgressReporter(total: 3, [sink], nowMs: clock.Read);

        // No time elapses → intermediate events are throttled out, but the
        // final event must still fire so 100% completion is visible.
        await reporter.ReportAsync(1, "a.txt", 2);
        await reporter.ReportAsync(2, "b.txt", 3);
        await reporter.ReportAsync(3, "c.txt", 4);

        Assert.Single(sink.Events);
        Assert.True(sink.Events[0].IsFinal);
        Assert.Equal(3, sink.Events[0].Processed);
        Assert.Equal(3, sink.Events[0].Total);
        Assert.Equal(100, sink.Events[0].PercentComplete);
    }

    [Fact]
    public async Task Time_stride_throttles_intermediate_emits_to_one_per_500ms()
    {
        var clock = new FakeClock();
        var sink = new CapturingSink();
        var reporter = new IndexProgressReporter(total: 100, [sink], nowMs: clock.Read);

        // Establish a "first emit" baseline so the firstEmitTrigger isn't the
        // one we're testing.
        clock.Advance(IndexProgressReporter.ThrottleMs);
        await reporter.ReportAsync(1, "a", 0); // first-emit trigger fires
        sink.Events.Clear();

        // 400 ms — under threshold, must be throttled.
        clock.Advance(400);
        await reporter.ReportAsync(2, "b", 0);
        Assert.Empty(sink.Events);

        // 100 ms more (500 total since last emit) — must fire.
        clock.Advance(100);
        await reporter.ReportAsync(3, "c", 0);
        Assert.Single(sink.Events);
    }

    [Fact]
    public async Task First_emit_fires_once_500ms_have_passed_since_start()
    {
        var clock = new FakeClock();
        var sink = new CapturingSink();
        var reporter = new IndexProgressReporter(total: 100, [sink], nowMs: clock.Read);

        // Immediately after construction — no emit (no elapsed time).
        await reporter.ReportAsync(1, "a", 0);
        Assert.Empty(sink.Events);

        // 499 ms in — still no emit.
        clock.Advance(499);
        await reporter.ReportAsync(2, "b", 0);
        Assert.Empty(sink.Events);

        // 500 ms in — first emit fires.
        clock.Advance(1);
        await reporter.ReportAsync(3, "c", 0);
        Assert.Single(sink.Events);
        Assert.False(sink.Events[0].IsFinal);
    }

    [Fact]
    public async Task Empty_sinks_list_is_a_noop()
    {
        var clock = new FakeClock();
        var reporter = new IndexProgressReporter(total: 5, [], nowMs: clock.Read);
        // Must not throw and must short-circuit cleanly.
        clock.Advance(10_000);
        await reporter.ReportAsync(5, "a", 0);
    }

    [Fact]
    public async Task Zero_total_does_not_divide_by_zero_and_still_emits_final()
    {
        var clock = new FakeClock();
        var sink = new CapturingSink();
        var reporter = new IndexProgressReporter(total: 0, [sink], nowMs: clock.Read);

        // processed=0, total=0 → IsFinal true via processed >= total branch.
        await reporter.ReportAsync(0, "(empty)", 0);
        Assert.Single(sink.Events);
        Assert.Equal(100, sink.Events[0].PercentComplete);
    }

    [Fact]
    public async Task Sink_exceptions_are_swallowed_so_loop_continues()
    {
        var clock = new FakeClock();
        var bad = new ThrowingSink(new InvalidOperationException("boom"));
        var good = new CapturingSink();
        var reporter = new IndexProgressReporter(total: 2, [bad, good], nowMs: clock.Read);

        await reporter.ReportAsync(1, "a", 0); // throttled
        clock.Advance(IndexProgressReporter.ThrottleMs);
        await reporter.ReportAsync(1, "a", 0); // first-emit fires
        await reporter.ReportAsync(2, "b", 0); // final fires

        Assert.True(bad.CallCount > 0);
        Assert.True(good.Events.Count > 0, "good sink must receive events even when bad sink throws");
    }

    [Fact]
    public async Task OperationCanceledException_in_sink_propagates()
    {
        var clock = new FakeClock();
        var bad = new ThrowingSink(new OperationCanceledException());
        var reporter = new IndexProgressReporter(total: 1, [bad], nowMs: clock.Read);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await reporter.ReportAsync(1, "a", 0));
    }

    [Fact]
    public async Task Cancellation_token_is_observed_by_sinks()
    {
        var clock = new FakeClock();
        var received = new CapturingSink();
        var reporter = new IndexProgressReporter(total: 1, [received], nowMs: clock.Read);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Sink's WriteAsync receives the cancellation token; whether it honours
        // it is up to the sink. Reporter doesn't pre-check (that's the caller's
        // job via ThrowIfCancellationRequested in the indexer hot loop).
        await reporter.ReportAsync(1, "a", 0, cts.Token);
        Assert.Single(received.Events);
    }

    [Fact]
    public async Task ReportEnumeratingAsync_emits_a_stage_event_before_total_is_known()
    {
        var clock = new FakeClock();
        var sink = new CapturingSink();
        var reporter = new IndexProgressReporter(total: 0, [sink], nowMs: clock.Read);

        await reporter.ReportEnumeratingAsync("C:\\some\\path");

        Assert.Single(sink.Events);
        Assert.Equal(IndexProgressStage.Enumerating, sink.Events[0].Stage);
        Assert.Equal(0, sink.Events[0].Total);
        Assert.False(sink.Events[0].IsFinal);
    }

    [Fact]
    public void IndexProgressEvent_percent_uses_floor_for_non_final_events()
    {
        // 99.7% should render as 99% mid-flight, NEVER as 100% before the
        // final event fires (rubber-duck #7 catch).
        var ev = new IndexProgressEvent(
            Processed: 997, Total: 1000, RelativePath: "x", ChunkCount: 0,
            Elapsed: TimeSpan.FromSeconds(10), IsFinal: false);
        Assert.Equal(99, ev.PercentComplete);
    }

    [Fact]
    public void IndexProgressEvent_percent_forces_100_only_on_final()
    {
        var final = new IndexProgressEvent(
            Processed: 1000, Total: 1000, RelativePath: "x", ChunkCount: 0,
            Elapsed: TimeSpan.FromSeconds(10), IsFinal: true);
        Assert.Equal(100, final.PercentComplete);

        // Same processed counts but missing IsFinal — still 100 because
        // processed >= total is the implicit "we're done" signal even when
        // the caller forgot to flag it.
        var alsoFinal = new IndexProgressEvent(
            Processed: 1000, Total: 1000, RelativePath: "x", ChunkCount: 0,
            Elapsed: TimeSpan.FromSeconds(10), IsFinal: false);
        Assert.Equal(100, alsoFinal.PercentComplete);
    }

    [Fact]
    public void IndexProgressEvent_eta_returns_zero_when_no_progress_yet()
    {
        var ev = new IndexProgressEvent(
            Processed: 0, Total: 100, RelativePath: "x", ChunkCount: 0,
            Elapsed: TimeSpan.FromSeconds(5), IsFinal: false);
        Assert.Equal(TimeSpan.Zero, ev.EstimatedRemaining);
    }

    [Fact]
    public void IndexProgressEvent_eta_returns_zero_until_min_elapsed_window()
    {
        // < 200ms elapsed → ETA unreliable; reporter renders "calculating ETA".
        var ev = new IndexProgressEvent(
            Processed: 5, Total: 100, RelativePath: "x", ChunkCount: 0,
            Elapsed: TimeSpan.FromMilliseconds(100), IsFinal: false);
        Assert.Equal(TimeSpan.Zero, ev.EstimatedRemaining);
    }

    [Fact]
    public void IndexProgressEvent_eta_extrapolates_linearly()
    {
        // 10 of 100 files in 1 second → ~9 seconds remaining (linear).
        var ev = new IndexProgressEvent(
            Processed: 10, Total: 100, RelativePath: "x", ChunkCount: 0,
            Elapsed: TimeSpan.FromSeconds(1), IsFinal: false);
        var eta = ev.EstimatedRemaining;
        Assert.InRange(eta.TotalSeconds, 8.5, 9.5);
    }

    [Fact]
    public void IndexProgressEvent_eta_is_zero_for_final_event()
    {
        var ev = new IndexProgressEvent(
            Processed: 100, Total: 100, RelativePath: "x", ChunkCount: 0,
            Elapsed: TimeSpan.FromSeconds(10), IsFinal: true);
        Assert.Equal(TimeSpan.Zero, ev.EstimatedRemaining);
    }

    [Fact]
    public async Task StderrSink_emits_indexing_prefix_with_padded_counts()
    {
        var writer = new StringWriter();
        var sink = new StderrIndexProgressSink(writer);
        await sink.WriteAsync(new IndexProgressEvent(
            Processed: 12, Total: 918, RelativePath: "src/App.cs", ChunkCount: 3,
            Elapsed: TimeSpan.FromSeconds(2), IsFinal: false), CancellationToken.None);

        var line = writer.ToString().TrimEnd();
        Assert.StartsWith("[indexing] ", line);
        Assert.Contains("012/918", line);
        Assert.Contains("src/App.cs", line);
        Assert.Contains("3 chunks", line);
        Assert.Contains("1% complete", line);
    }

    [Fact]
    public async Task StderrSink_emits_enumerating_line_for_pre_enum_event()
    {
        var writer = new StringWriter();
        var sink = new StderrIndexProgressSink(writer);
        await sink.WriteAsync(new IndexProgressEvent(
            Processed: 0, Total: 0, RelativePath: "C:/repo", ChunkCount: 0,
            Elapsed: TimeSpan.FromMilliseconds(50), IsFinal: false,
            Stage: IndexProgressStage.Enumerating), CancellationToken.None);

        var line = writer.ToString().TrimEnd();
        Assert.StartsWith("[indexing] enumerating files", line);
        Assert.Contains("C:/repo", line);
    }

    [Fact]
    public async Task StderrSink_renders_done_label_on_final_event()
    {
        var writer = new StringWriter();
        var sink = new StderrIndexProgressSink(writer);
        await sink.WriteAsync(new IndexProgressEvent(
            Processed: 5, Total: 5, RelativePath: "last.md", ChunkCount: 1,
            Elapsed: TimeSpan.FromSeconds(2), IsFinal: true), CancellationToken.None);

        var line = writer.ToString().TrimEnd();
        Assert.Contains("100% complete", line);
        Assert.Contains("done", line);
    }
}
