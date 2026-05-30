namespace Koshi.Core.Harness;

using System.Diagnostics;
using Koshi.Core.Context;
using Koshi.Core.Memory;
using Koshi.Core.Models;
using Koshi.Core.Retrieval;
using Koshi.Core.Tokenization;
using Microsoft.Extensions.AI;

/// <summary>
/// The central orchestrator that ties retrieval, context compilation,
/// and LLM execution into a single pipeline. This is the "Harness" —
/// the scaffolding that makes an AI system work reliably.
///
/// Pipeline:
///   Query → Retrieve → Compile context → LLM call → Track metrics
///
/// Design principles:
/// - Coordinator only — does not implement logic, delegates to components
/// - Does not own conversation history or session storage
/// - Configurable pipeline steps (skip retrieval, etc.)
/// - Fallback cascade on failures
/// - Full observability via tracer and quality tracker
/// </summary>
public sealed class SessionOrchestrator
{
    private readonly IRetriever _retriever;
    private readonly ContextCompiler _contextCompiler;
    private readonly CachePrefixOptimizer _cacheOptimizer;
    private readonly IChatClient _chatClient;
    private readonly TokenCounter _tokenCounter;
    private readonly QualityTracker _qualityTracker;
    private readonly FallbackStrategy _fallbackStrategy;
    private readonly IHarnessTracer _tracer;

    public SessionOrchestrator(
        IRetriever retriever,
        ContextCompiler contextCompiler,
        CachePrefixOptimizer cacheOptimizer,
        IChatClient chatClient,
        TokenCounter tokenCounter,
        QualityTracker qualityTracker,
        IHarnessTracer? tracer = null)
    {
        _retriever = retriever;
        _contextCompiler = contextCompiler;
        _cacheOptimizer = cacheOptimizer;
        _chatClient = chatClient;
        _tokenCounter = tokenCounter;
        _qualityTracker = qualityTracker;
        _tracer = tracer ?? NullTracer.Instance;
        _fallbackStrategy = new FallbackStrategy(_tracer);
    }

    /// <summary>Access quality tracker for metrics queries.</summary>
    public QualityTracker Quality => _qualityTracker;

    /// <summary>Access cache optimizer for hit rate queries.</summary>
    public CachePrefixOptimizer CacheOptimizer => _cacheOptimizer;

    /// <summary>
    /// Process a single turn: retrieve → compile → call LLM → extract facts.
    /// </summary>
    public async Task<TurnResult> ProcessTurnAsync(
        SessionTurnContext ctx,
        HarnessPipelineConfig config,
        CancellationToken ct = default)
    {
        using var activity = _tracer.StartActivity("ProcessTurn");
        activity?.SetTag("session.id", ctx.Session.SessionId);
        activity?.SetTag("query", ctx.Query);

        var activeConfig = config;

        try
        {
            return await ExecutePipelineAsync(ctx, activeConfig, ct);
        }
        catch (Exception ex) when (config.EnableFallback)
        {
            // Enter fallback cascade
            return await HandleFallbackAsync(ctx, activeConfig, ex, ct);
        }
    }

    private async Task<TurnResult> ExecutePipelineAsync(
        SessionTurnContext ctx,
        HarnessPipelineConfig config,
        CancellationToken ct)
    {
        // ── Step 1: Retrieve relevant documents ──
        if (config.EnableRetrieval)
        {
            await RetrieveAsync(ctx, config, ct);
        }

        // ── Step 2: Compile context window ──
        CompileContext(ctx, config);

        // ── Step 3: Call LLM ──
        await CallLlmAsync(ctx, config, ct);

        // ── Step 4: Update session state and track metrics ──
        FinalizeAndTrack(ctx, config);

        return BuildResult(ctx);
    }

    // ── Pipeline Steps ──────────────────────────────────────────────────

    private async Task RetrieveAsync(
        SessionTurnContext ctx, HarnessPipelineConfig config, CancellationToken ct)
    {
        using var activity = _tracer.StartActivity("Retrieve");
        var sw = Stopwatch.StartNew();

        ctx.RetrievedChunks = await _retriever.SearchAsync(
            ctx.Query, config.RetrievalOptions, ct);

        sw.Stop();
        ctx.RetrievalLatency = sw.Elapsed;
        activity?.SetTag("chunks.count", ctx.RetrievedChunks.Count);
    }

    private void CompileContext(SessionTurnContext ctx, HarnessPipelineConfig config)
    {
        using var activity = _tracer.StartActivity("CompileContext");
        var sw = Stopwatch.StartNew();

        var request = new ContextCompileRequest
        {
            UserQuery = ctx.Query,
            SystemPrompt = config.SystemPrompt,
            TeamContext = config.TeamContext,
            RetrievedChunks = ctx.RetrievedChunks,
            Memories = ctx.RecalledMemories,
            ConversationHistory = ctx.History.Recent(config.MaxHistoryTurns),
            Budget = config.ContextBudget,
            Strategy = config.PositioningStrategy,
        };

        ctx.CompiledContext = _contextCompiler.Compile(request);
        _cacheOptimizer.OptimizeAndTrack(ctx.CompiledContext);

        sw.Stop();
        ctx.CompilationLatency = sw.Elapsed;
        activity?.SetTag("sections.included", ctx.CompiledContext.Metrics.SectionsIncluded);
        activity?.SetTag("sections.dropped", ctx.CompiledContext.Metrics.SectionsDropped);
        activity?.SetTag("tokens.used", ctx.CompiledContext.Metrics.TotalTokensUsed);
    }

    private async Task CallLlmAsync(
        SessionTurnContext ctx, HarnessPipelineConfig config, CancellationToken ct)
    {
        using var activity = _tracer.StartActivity("CallLlm");
        var sw = Stopwatch.StartNew();

        // Build chat messages from compiled context
        var messages = new List<ChatMessage>();

        // System message = cacheable prefix + dynamic context
        var compiledText = ctx.CompiledContext!.AssembleText();

        // Add degraded-mode disclaimer if in fallback
        var disclaimer = FallbackStrategy.GetDegradedDisclaimer(ctx.FallbackLevel);
        if (disclaimer is not null)
            compiledText = $"{disclaimer}\n\n{compiledText}";

        messages.Add(new ChatMessage(ChatRole.System, compiledText));

        // Add conversation history as separate messages
        foreach (var turn in ctx.History.Recent(config.MaxHistoryTurns))
        {
            var role = turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.User : ChatRole.Assistant;
            messages.Add(new ChatMessage(role, turn.Content));
        }

        // Current query
        messages.Add(new ChatMessage(ChatRole.User, ctx.Query));

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: ct);
        ctx.LlmResponse = response.Text;

        sw.Stop();
        ctx.LlmLatency = sw.Elapsed;
        activity?.SetTag("response.length", ctx.LlmResponse?.Length ?? 0);
    }

    private void FinalizeAndTrack(SessionTurnContext ctx, HarnessPipelineConfig config)
    {
        // Update session state
        ctx.Session.IncrementTurn();
        var metrics = ctx.CompiledContext?.Metrics;
        ctx.Session.TotalTokens.Add(
            metrics?.TotalTokensUsed ?? 0,
            _tokenCounter.CountTokens(ctx.LlmResponse ?? ""),
            metrics?.CacheableTokens ?? 0);

        // Add to conversation history
        ctx.History.AddTurn("user", ctx.Query);
        if (ctx.LlmResponse is not null)
            ctx.History.AddTurn("assistant", ctx.LlmResponse);

        // Track quality metrics
        _qualityTracker.Record(ctx);
    }

    // ── Fallback Handling ───────────────────────────────────────────────

    // Max number of fallback retries before we surface failure. Without a
    // bound, a fallback strategy that always returns ShouldRetry=true would
    // recurse until the stack overflows. (Codex multi-model review H1.)
    private const int MaxFallbackRetries = 5;

    private async Task<TurnResult> HandleFallbackAsync(
        SessionTurnContext ctx,
        HarnessPipelineConfig config,
        Exception originalException,
        CancellationToken ct,
        int depth = 0)
    {
        var failure = ClassifyFailure(originalException);
        var decision = _fallbackStrategy.NextFallback(ctx.FallbackLevel, failure, ctx);

        if (!decision.ShouldRetry || depth >= MaxFallbackRetries)
        {
            ctx.FallbackLevel = FallbackLevel.Failed;
            ctx.FallbackReasons.Add(depth >= MaxFallbackRetries
                ? $"Max fallback depth ({MaxFallbackRetries}) reached: {decision.Reason}"
                : decision.Reason);
            ctx.LlmResponse = $"I'm unable to process this request. Error: {originalException.Message}";
            FinalizeAndTrack(ctx, config);
            return BuildResult(ctx);
        }

        ctx.FallbackLevel = decision.Level;
        ctx.FallbackReasons.Add(decision.Reason);
        var adjustedConfig = _fallbackStrategy.AdjustConfig(config, decision.Level);

        try
        {
            return await ExecutePipelineAsync(ctx, adjustedConfig, ct);
        }
        catch (Exception retryEx)
        {
            // Recurse with incremented depth — will hit MaxFallbackRetries
            // even if the strategy never returns ShouldRetry=false.
            return await HandleFallbackAsync(ctx, adjustedConfig, retryEx, ct, depth + 1);
        }
    }

    private static FailureType ClassifyFailure(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => FailureType.LlmTimeout,
        TimeoutException => FailureType.LlmTimeout,
        HttpRequestException => FailureType.LlmError,
        InvalidOperationException e when e.Message.Contains("retriev", StringComparison.OrdinalIgnoreCase)
            => FailureType.RetrievalFailed,
        _ => FailureType.Unknown,
    };

    private static TurnResult BuildResult(SessionTurnContext ctx)
    {
        var metrics = ctx.CompiledContext?.Metrics;
        return new TurnResult
        {
            Response = ctx.LlmResponse ?? "(no response)",
            FallbackUsed = ctx.FallbackLevel,
            FactsExtracted = ctx.FactExtraction?.Accepted ?? 0,
            Metrics = new TurnMetrics
            {
                InputTokens = metrics?.TotalTokensUsed ?? 0,
                OutputTokens = ctx.LlmResponse is not null
                    ? (int)(ctx.LlmResponse.Length / 3.5) : 0,
                CachedTokens = metrics?.CacheableTokens ?? 0,
                ContextSectionsIncluded = metrics?.SectionsIncluded ?? 0,
                ContextSectionsDropped = metrics?.SectionsDropped ?? 0,
                BudgetUtilization = metrics?.BudgetUtilization ?? 0,
                CacheRatio = metrics?.CacheRatio ?? 0,
                RetrievedChunkCount = ctx.RetrievedChunks.Count,
                RecalledMemoryCount = ctx.RecalledMemories.Count,
                RetrievalLatency = ctx.RetrievalLatency ?? TimeSpan.Zero,
                MemoryLatency = ctx.MemoryLatency ?? TimeSpan.Zero,
                CompilationLatency = ctx.CompilationLatency ?? TimeSpan.Zero,
                LlmLatency = ctx.LlmLatency ?? TimeSpan.Zero,
                TotalLatency = ctx.TotalLatency,
                PositioningStrategy = metrics?.StrategyUsed ?? PositioningStrategy.CacheOptimized,
            },
        };
    }
}
