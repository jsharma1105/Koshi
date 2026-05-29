using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Writes one human-readable line per progress event to <see cref="Console.Error"/>.
/// MCP stdio reserves stdout for JSON-RPC traffic; stderr is the canonical
/// channel for logging (see Program.cs:71). Throttling lives in the
/// <see cref="IndexProgressReporter"/>, so this sink can write unconditionally.
/// </summary>
internal sealed class StderrIndexProgressSink : IIndexProgressSink
{
    private readonly TextWriter _writer;

    public StderrIndexProgressSink() : this(Console.Error) { }

    internal StderrIndexProgressSink(TextWriter writer) => _writer = writer;

    public ValueTask WriteAsync(IndexProgressEvent ev, CancellationToken cancellationToken)
    {
        string line;
        if (ev.Stage == IndexProgressStage.Enumerating)
        {
            line = $"[indexing] enumerating files under '{ev.RelativePath}' …";
        }
        else
        {
            var pad = Math.Max(1, ev.Total.ToString().Length);
            var idx = ev.Processed.ToString().PadLeft(pad, '0');
            var total = ev.Total.ToString().PadLeft(pad, '0');
            var eta = ev.EstimatedRemaining;
            var etaText = eta == TimeSpan.Zero
                ? (ev.IsFinal ? "done" : "calculating ETA")
                : $"~{(int)Math.Ceiling(eta.TotalSeconds)}s remaining";
            line = $"[indexing] {idx}/{total} {ev.RelativePath} ({ev.ChunkCount} chunks, {ev.PercentComplete}% complete, {etaText})";
        }

        _writer.WriteLine(line);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Forwards each event as an MCP <c>notifications/progress</c> message via
/// <see cref="McpServer.NotifyProgressAsync"/>. Only created when the
/// caller actually supplied a progress token in <c>_meta.progressToken</c>;
/// when the token is absent we omit this sink entirely (the MCP spec requires
/// silent no-op in that case).
/// </summary>
internal sealed class McpIndexProgressSink : IIndexProgressSink
{
    private readonly McpServer _server;
    private readonly ProgressToken _token;

    public McpIndexProgressSink(McpServer server, ProgressToken token)
    {
        _server = server;
        _token = token;
    }

    public async ValueTask WriteAsync(IndexProgressEvent ev, CancellationToken cancellationToken)
    {
        var message = ev.Stage == IndexProgressStage.Enumerating
            ? "enumerating files…"
            : $"{ev.PercentComplete}% — {ev.RelativePath}";

        await _server.NotifyProgressAsync(
            _token,
            new ProgressNotificationValue
            {
                Progress = ev.Processed,
                Total = ev.Total > 0 ? (float?)ev.Total : null,
                Message = message,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
