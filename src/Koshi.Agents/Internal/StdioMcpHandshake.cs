using System.Diagnostics;
using System.Text.Json;

namespace Koshi.Agents.Internal;

/// <summary>
/// Production <see cref="IMcpHandshake"/> backed by <see cref="Process"/> over stdio.
/// Concurrency contract: each spawned process owns one background reader task
/// per stream (stdout JSON, stderr text); callers serialize their <c>RpcAsync</c>
/// requests through the process's stdin.
/// </summary>
internal sealed class StdioMcpHandshake : IMcpHandshake
{
    public async Task<string?> CaptureVersionAsync(
        string command,
        IReadOnlyDictionary<string, string?> env,
        string? cwd,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("--version");
        ApplyEnv(psi, env);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for {command}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);

        // Start reading stdout BEFORE WaitForExit to avoid a pipe-buffer
        // deadlock: if the child's --version output ever exceeds ~4KB
        // (e.g. future "koshi-mcp 1.0 (commit X, build Y, deps {...})"), the
        // pipe buffer fills, the child blocks on write, and WaitForExitAsync
        // blocks forever. Reading concurrently keeps the pipe drained.
        // (Codex multi-model review C3.)
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        // Also drain stderr to prevent the same deadlock on the stderr pipe.
        var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(proc);
            return null;
        }

        if (proc.ExitCode != 0) return null;
        string stdout;
        try { stdout = await stdoutTask; }
        catch (OperationCanceledException) { return null; }
        // Best-effort drain — failure here doesn't affect the version probe.
        try { await stderrTask; } catch { /* ignore */ }
        var trimmed = stdout.Trim();
        // Expected format: "koshi-mcp 0.8.1" (single line). Be tolerant of
        // future additions ("koshi-mcp 0.9.0 (commit abc)") and older builds.
        if (string.IsNullOrEmpty(trimmed)) return null;
        var firstLine = trimmed.Split('\n', 2)[0].Trim();
        var space = firstLine.IndexOf(' ');
        return space > 0 && space < firstLine.Length - 1 ? firstLine[(space + 1)..].Trim() : firstLine;
    }

    public Task<IMcpProcess> SpawnAsync(
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env,
        string? cwd,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        ApplyEnv(psi, env);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for {command}");
        return Task.FromResult<IMcpProcess>(new StdioMcpProcess(proc));
    }

    private static void ApplyEnv(ProcessStartInfo psi, IReadOnlyDictionary<string, string?> env)
    {
        foreach (var kv in env)
        {
            if (kv.Value is null)
                psi.Environment.Remove(kv.Key);
            else
                psi.Environment[kv.Key] = kv.Value;
        }
    }

    internal static void TryKillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: process may have exited between checks.
        }
    }
}

/// <summary>
/// Owns the spawned <see cref="Process"/> and the background reader tasks
/// that demultiplex JSON-RPC responses keyed by id, plus a capped stderr tail
/// for diagnostics. Cleaned up via <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class StdioMcpProcess : IMcpProcess
{
    private const int StderrTailCap = 50;

    private readonly Process _proc;
    private readonly Dictionary<int, JsonElement> _responses = new();
    private readonly SemaphoreSlim _responseSignal = new(0);
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    // Ring buffer for stderr — keeps the LAST StderrTailCap lines, not the
    // first. The old head-keep semantics meant that when something failed
    // repeatedly, the user got 50 boilerplate "starting" lines and the
    // actual error was discarded. (Codex multi-model review C4.)
    private readonly Queue<string> _stderr = new();
    private readonly object _stderrLock = new();
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _stdoutReader;
    private readonly Task _stderrReader;
    private int _nextId = 1;

    public StdioMcpProcess(Process proc)
    {
        _proc = proc;
        _stdoutReader = Task.Run(ReadStdoutLoopAsync);
        _stderrReader = Task.Run(ReadStderrLoopAsync);
    }

    public int Pid => _proc.Id;

    public IReadOnlyList<string> StderrTail
    {
        get
        {
            lock (_stderrLock) return _stderr.ToArray();
        }
    }

    // Append to the ring buffer, dropping the oldest line when full so the
    // most recent context survives. Caller MUST hold _stderrLock.
    private void AppendStderrLocked(string line)
    {
        if (_stderr.Count >= StderrTailCap) _stderr.Dequeue();
        _stderr.Enqueue(line);
    }

    public async Task<JsonElement?> RpcAsync(string method, object? @params, int timeoutMs, CancellationToken cancellationToken)
    {
        int id;
        string payload;
        await _stdinLock.WaitAsync(cancellationToken);
        try
        {
            id = _nextId++;
            payload = @params is null
                ? $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}"""
                : $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{JsonSerializer.Serialize(@params)}}}""";

            await _proc.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await _proc.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _stdinLock.Release();
        }

        var deadline = Environment.TickCount + timeoutMs;
        while (true)
        {
            lock (_responses)
            {
                if (_responses.TryGetValue(id, out var cached))
                {
                    _responses.Remove(id);
                    return cached;
                }
            }
            int remaining = deadline - Environment.TickCount;
            if (remaining <= 0) return null;

            // Wait with a bounded slice so we re-check the deadline even when
            // unrelated responses release the signal first.
            await _responseSignal.WaitAsync(Math.Min(remaining, 250), cancellationToken);
        }
    }

    public async Task NotifyAsync(string method, CancellationToken cancellationToken)
    {
        var payload = $$"""{"jsonrpc":"2.0","method":"{{method}}"}""";
        await _stdinLock.WaitAsync(cancellationToken);
        try
        {
            await _proc.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await _proc.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    public async Task<bool> ShutdownAsync(int graceMs, CancellationToken cancellationToken)
    {
        try { _proc.StandardInput.Close(); } catch { /* already closed */ }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(graceMs);
            await _proc.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            StdioMcpHandshake.TryKillTree(_proc);
            try { await _proc.WaitForExitAsync(CancellationToken.None); } catch { /* ignore */ }
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _readerCts.Cancel(); } catch { /* ignore */ }
        try { StdioMcpHandshake.TryKillTree(_proc); } catch { /* ignore */ }
        try { await _proc.WaitForExitAsync(CancellationToken.None); } catch { /* ignore */ }
        try { await Task.WhenAll(_stdoutReader, _stderrReader).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* ignore */ }

        _readerCts.Dispose();
        _responseSignal.Dispose();
        _stdinLock.Dispose();
        _proc.Dispose();
    }

    private async Task ReadStdoutLoopAsync()
    {
        var token = _readerCts.Token;
        try
        {
            string? line;
            while ((line = await _proc.StandardOutput.ReadLineAsync(token)) is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement.Clone();
                    if (root.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.Number)
                    {
                        lock (_responses)
                        {
                            _responses[idElem.GetInt32()] = root;
                        }
                        _responseSignal.Release();
                    }
                }
                catch (JsonException)
                {
                    lock (_stderrLock)
                    {
                        AppendStderrLocked($"[stdout/non-json] {Truncate(line, 200)}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            lock (_stderrLock)
            {
                AppendStderrLocked($"[stdout/error] {Truncate(ex.Message, 200)}");
            }
        }
    }

    private async Task ReadStderrLoopAsync()
    {
        var token = _readerCts.Token;
        try
        {
            string? line;
            while ((line = await _proc.StandardError.ReadLineAsync(token)) is not null)
            {
                lock (_stderrLock)
                {
                    AppendStderrLocked(Truncate(line, 200));
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch { /* ignore */ }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
