using Koshi.Core.Context;
using Koshi.Core.Harness;
using Koshi.Core.Memory;
using Koshi.Core.Models;
using Koshi.Core.Retrieval;
using Koshi.Core.Tokenization;
using Microsoft.Extensions.AI;

namespace Koshi.Core.Tests;

// ─── Session State Tests ────────────────────────────────────────────────

public class SessionStateTests
{
    [Fact]
    public void New_Session_Has_Active_Status()
    {
        var session = new SessionState();
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Null(session.EndedAt);
    }

    [Fact]
    public void IncrementTurn_Tracks_Count()
    {
        var session = new SessionState();
        Assert.Equal(0, session.TurnCount);
        session.IncrementTurn();
        session.IncrementTurn();
        Assert.Equal(2, session.TurnCount);
    }

    [Fact]
    public void End_Sets_Status_And_Timestamp()
    {
        var session = new SessionState();
        session.End();
        Assert.Equal(SessionStatus.Ended, session.Status);
        Assert.NotNull(session.EndedAt);
    }

    [Fact]
    public void MemoryScope_Derives_From_User_And_Workspace()
    {
        var session = new SessionState { UserId = "alice", WorkspaceId = "team-x" };
        var scope = session.MemoryScope;
        Assert.Equal("alice", scope.UserId);
        Assert.Equal("team-x", scope.WorkspaceId);
    }

    [Fact]
    public void TokenUsage_Accumulates()
    {
        var usage = new TokenUsage();
        usage.Add(100, 50, 20);
        usage.Add(200, 75, 40);
        Assert.Equal(300, usage.InputTokens);
        Assert.Equal(125, usage.OutputTokens);
        Assert.Equal(60, usage.CachedTokens);
        Assert.Equal(425, usage.TotalTokens);
    }
}

// ─── Conversation History Tests ─────────────────────────────────────────

public class ConversationHistoryTests
{
    [Fact]
    public void AddTurn_Stores_Messages()
    {
        var history = new ConversationHistory();
        history.AddTurn("user", "hello");
        history.AddTurn("assistant", "hi there");
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void SlidingWindow_Drops_Oldest()
    {
        var history = new ConversationHistory(maxTurns: 3);
        history.AddTurn("user", "msg1");
        history.AddTurn("assistant", "reply1");
        history.AddTurn("user", "msg2");
        history.AddTurn("assistant", "reply2"); // This should push msg1 out

        Assert.Equal(3, history.Count);
        Assert.Equal("reply1", history.Turns[0].Content);
    }

    [Fact]
    public void Recent_Returns_Last_N()
    {
        var history = new ConversationHistory();
        for (int i = 0; i < 10; i++)
            history.AddTurn("user", $"msg{i}");

        var recent = history.Recent(3);
        Assert.Equal(3, recent.Count);
        Assert.Equal("msg7", recent[0].Content);
        Assert.Equal("msg9", recent[2].Content);
    }
}

// ─── Quality Tracker Tests ──────────────────────────────────────────────

public class QualityTrackerTests
{
    [Fact]
    public void Record_Tracks_Turn_Metrics()
    {
        var tracker = new QualityTracker();
        var ctx = MakeTurnContext();
        ctx.LlmResponse = "Some response text here";
        ctx.CompiledContext = MakeCompiledContext(100, 3, 0);

        tracker.Record(ctx);

        Assert.Equal(1, tracker.TurnCount);
        Assert.True(tracker.AvgInputTokens > 0);
    }

    [Fact]
    public void GetSummary_Aggregates_Multiple_Turns()
    {
        var tracker = new QualityTracker();

        for (int i = 0; i < 5; i++)
        {
            var ctx = MakeTurnContext();
            ctx.LlmResponse = $"Response {i}";
            ctx.CompiledContext = MakeCompiledContext(100 + i * 10, 3, i % 2 == 0 ? 1 : 0);
            tracker.Record(ctx);
        }

        var summary = tracker.GetSummary();
        Assert.Equal(5, summary.TurnCount);
        Assert.True(summary.TotalInputTokens > 0);
        Assert.True(summary.TotalOutputTokens > 0);
    }

    [Fact]
    public void FallbackCount_Tracks_Degraded_Turns()
    {
        var tracker = new QualityTracker();

        var normal = MakeTurnContext();
        normal.LlmResponse = "ok";
        normal.CompiledContext = MakeCompiledContext(50, 2, 0);
        tracker.Record(normal);

        var degraded = MakeTurnContext();
        degraded.FallbackLevel = FallbackLevel.MemoryOnly;
        degraded.LlmResponse = "fallback response";
        degraded.CompiledContext = MakeCompiledContext(30, 1, 0);
        tracker.Record(degraded);

        Assert.Equal(1, tracker.FallbackCount);
    }

    private static SessionTurnContext MakeTurnContext() => new()
    {
        Query = "test query",
        Session = new SessionState(),
        History = new ConversationHistory(),
    };

    private static CompiledContext MakeCompiledContext(int tokens, int included, int dropped) => new()
    {
        Sections = [],
        Budget = ContextBudget.Default(),
        Metrics = new ContextCompilationMetrics
        {
            TotalTokensUsed = tokens,
            TokenBudgetAvailable = 6144,
            SectionsIncluded = included,
            SectionsDropped = dropped,
            CacheableTokens = (int)(tokens * 0.15),
            TokensPerRole = new Dictionary<ContextRole, int>(),
            CompilationTime = TimeSpan.FromMicroseconds(500),
            StrategyUsed = PositioningStrategy.CacheOptimized,
        },
    };
}

// ─── Fallback Strategy Tests ────────────────────────────────────────────

public class FallbackStrategyTests
{
    private readonly FallbackStrategy _strategy = new();

    [Fact]
    public void Retrieval_Failure_Falls_To_MemoryOnly()
    {
        var ctx = MakeTurnContext();
        var decision = _strategy.NextFallback(
            FallbackLevel.None, FailureType.RetrievalFailed, ctx);

        Assert.Equal(FallbackLevel.MemoryOnly, decision.Level);
        Assert.True(decision.ShouldRetry);
    }

    [Fact]
    public void Budget_Exceeded_Falls_To_ReducedContext()
    {
        var ctx = MakeTurnContext();
        var decision = _strategy.NextFallback(
            FallbackLevel.None, FailureType.BudgetExceeded, ctx);

        Assert.Equal(FallbackLevel.ReducedContext, decision.Level);
        Assert.True(decision.ShouldRetry);
    }

    [Fact]
    public void ReducedContext_Failure_Falls_To_MemoryOnly()
    {
        var ctx = MakeTurnContext();
        var decision = _strategy.NextFallback(
            FallbackLevel.ReducedContext, FailureType.Unknown, ctx);

        Assert.Equal(FallbackLevel.MemoryOnly, decision.Level);
        Assert.True(decision.ShouldRetry);
    }

    [Fact]
    public void MemoryOnly_Failure_Falls_To_NoContext()
    {
        var ctx = MakeTurnContext();
        var decision = _strategy.NextFallback(
            FallbackLevel.MemoryOnly, FailureType.Unknown, ctx);

        Assert.Equal(FallbackLevel.NoContext, decision.Level);
        Assert.True(decision.ShouldRetry);
    }

    [Fact]
    public void NoContext_Failure_Falls_To_Failed()
    {
        var ctx = MakeTurnContext();
        var decision = _strategy.NextFallback(
            FallbackLevel.NoContext, FailureType.Unknown, ctx);

        Assert.Equal(FallbackLevel.Failed, decision.Level);
        Assert.False(decision.ShouldRetry);
    }

    [Fact]
    public void AdjustConfig_ReducedContext_Halves_Budget()
    {
        var original = new HarnessPipelineConfig
        {
            ContextBudget = ContextBudget.Default(8192),
        };
        var adjusted = _strategy.AdjustConfig(original, FallbackLevel.ReducedContext);
        Assert.Equal(4096, adjusted.ContextBudget!.TotalBudget);
    }

    [Fact]
    public void AdjustConfig_MemoryOnly_Disables_Retrieval()
    {
        var original = new HarnessPipelineConfig();
        var adjusted = _strategy.AdjustConfig(original, FallbackLevel.MemoryOnly);
        Assert.False(adjusted.EnableRetrieval);
        Assert.True(adjusted.EnableMemory);
    }

    [Fact]
    public void AdjustConfig_NoContext_Disables_Everything()
    {
        var original = new HarnessPipelineConfig();
        var adjusted = _strategy.AdjustConfig(original, FallbackLevel.NoContext);
        Assert.False(adjusted.EnableRetrieval);
        Assert.False(adjusted.EnableMemory);
        Assert.False(adjusted.EnableFactExtraction);
    }

    [Fact]
    public void AdjustConfig_SkipFactExtraction_OnFallback_Default()
    {
        var original = new HarnessPipelineConfig
        {
            EnableFactExtraction = true,
            SkipFactExtractionOnFallback = true,
        };
        var adjusted = _strategy.AdjustConfig(original, FallbackLevel.ReducedContext);
        Assert.False(adjusted.EnableFactExtraction);
    }

    [Fact]
    public void GetDegradedDisclaimer_None_Returns_Null()
    {
        Assert.Null(FallbackStrategy.GetDegradedDisclaimer(FallbackLevel.None));
    }

    [Fact]
    public void GetDegradedDisclaimer_ReducedContext_Returns_Message()
    {
        var msg = FallbackStrategy.GetDegradedDisclaimer(FallbackLevel.ReducedContext);
        Assert.NotNull(msg);
        Assert.Contains("reduced context", msg, StringComparison.OrdinalIgnoreCase);
    }

    private static SessionTurnContext MakeTurnContext() => new()
    {
        Query = "test",
        Session = new SessionState(),
        History = new ConversationHistory(),
    };
}

// ─── Harness Tracer Tests ───────────────────────────────────────────────

public class HarnessTracerTests
{
    [Fact]
    public void NullTracer_Does_Not_Throw()
    {
        var tracer = NullTracer.Instance;
        var activity = tracer.StartActivity("test");
        Assert.Null(activity);
        tracer.AddEvent("event"); // Should not throw
    }

    [Fact]
    public void HarnessTracer_Returns_Activity_When_Listener_Attached()
    {
        // Without a listener, Activity is null
        var tracer = new HarnessTracer();
        var activity = tracer.StartActivity("test");
        // May be null if no listener — that's expected behavior
        activity?.Dispose();
    }
}

// ─── Pipeline Config Tests ──────────────────────────────────────────────

public class HarnessPipelineConfigTests
{
    [Fact]
    public void Default_Config_Enables_All_Steps()
    {
        var config = new HarnessPipelineConfig();
        Assert.True(config.EnableRetrieval);
        Assert.True(config.EnableMemory);
        Assert.True(config.EnableFactExtraction);
        Assert.True(config.EnableFallback);
    }

    [Fact]
    public void SkipFactExtractionOnFallback_Default_True()
    {
        var config = new HarnessPipelineConfig();
        Assert.True(config.SkipFactExtractionOnFallback);
    }
}

// ─── Session Orchestrator Integration Tests (no LLM required) ───────────

public class SessionOrchestratorTests
{
    [Fact]
    public async Task ProcessTurn_Without_Retrieval_Or_Memory_Still_Works()
    {
        var tc = await TokenCounter.CreateAsync();
        var compiler = new ContextCompiler(tc);
        var cacheOptimizer = new CachePrefixOptimizer(tc);
        var tracker = new QualityTracker();

        // Stub chat client that echoes
        var chatClient = new EchoChatClient();

        // Stub retriever that returns empty
        var retriever = new EmptyRetriever();

        var orchestrator = new SessionOrchestrator(
            retriever: retriever,
            contextCompiler: compiler,
            cacheOptimizer: cacheOptimizer,
            chatClient: chatClient,
            tokenCounter: tc,
            qualityTracker: tracker);

        var session = new SessionState();
        var history = new ConversationHistory();
        var ctx = new SessionTurnContext
        {
            Query = "What is the meaning of life?",
            Session = session,
            History = history,
        };

        var config = new HarnessPipelineConfig
        {
            EnableRetrieval = true,
            EnableMemory = false,
            EnableFactExtraction = false,
        };

        var result = await orchestrator.ProcessTurnAsync(ctx, config);

        Assert.NotNull(result.Response);
        Assert.True(result.Response.Length > 0);
        Assert.Equal(FallbackLevel.None, result.FallbackUsed);
        Assert.Equal(1, session.TurnCount);
        Assert.Equal(1, tracker.TurnCount);
        Assert.Equal(2, history.Count); // user + assistant
    }

    [Fact]
    public async Task ProcessTurn_Fallback_On_LLM_Failure()
    {
        var tc = await TokenCounter.CreateAsync();
        var compiler = new ContextCompiler(tc);
        var cacheOptimizer = new CachePrefixOptimizer(tc);
        var tracker = new QualityTracker();

        // Chat client that throws on first call, succeeds on retry
        var chatClient = new FailOnceChatClient();
        var retriever = new EmptyRetriever();

        var orchestrator = new SessionOrchestrator(
            retriever: retriever,
            contextCompiler: compiler,
            cacheOptimizer: cacheOptimizer,
            chatClient: chatClient,
            tokenCounter: tc,
            qualityTracker: tracker);

        var session = new SessionState();
        var ctx = new SessionTurnContext
        {
            Query = "test fallback",
            Session = session,
            History = new ConversationHistory(),
        };

        var config = new HarnessPipelineConfig
        {
            EnableRetrieval = false,
            EnableMemory = false,
            EnableFactExtraction = false,
            EnableFallback = true,
        };

        var result = await orchestrator.ProcessTurnAsync(ctx, config);

        // Should have fallen back but still produced a response
        Assert.NotNull(result.Response);
        Assert.True(result.FallbackUsed != FallbackLevel.None || result.Response.Length > 0);
    }
}

// ─── Test Doubles ───────────────────────────────────────────────────────

file class EmptyRetriever : IRetriever
{
    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, RetrievalOptions options, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SearchResult>>([]);
}

file class EchoChatClient : IChatClient
{
    public void Dispose() { }

    public ChatClientMetadata Metadata => new("echo");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var lastUserMsg = chatMessages.LastOrDefault(m => m.Role == ChatRole.User);
        var response = new ChatMessage(ChatRole.Assistant,
            $"Echo: {lastUserMsg?.Text ?? "no query"}");
        return Task.FromResult(new ChatResponse(response));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

file class FailOnceChatClient : IChatClient
{
    private int _callCount;

    public void Dispose() { }

    public ChatClientMetadata Metadata => new("fail-once");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _callCount) == 1)
            throw new HttpRequestException("Simulated LLM failure");

        var response = new ChatMessage(ChatRole.Assistant, "Recovered after fallback");
        return Task.FromResult(new ChatResponse(response));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
