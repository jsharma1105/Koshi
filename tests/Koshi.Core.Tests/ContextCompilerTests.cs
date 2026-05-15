using Koshi.Core.Context;
using Koshi.Core.Models;
using Koshi.Core.Tokenization;

namespace Koshi.Core.Tests;

public class BudgetManagerTests
{
    private readonly TokenCounter _tc;
    private readonly ContextBudget _budget;

    public BudgetManagerTests()
    {
        _tc = TokenCounter.CreateAsync().GetAwaiter().GetResult();
        _budget = ContextBudget.Default(8192);
    }

    [Fact]
    public void Default_Budget_Has_Correct_Available()
    {
        // ResponseReserve = 25% of 8192 = 2048
        Assert.Equal(6144, _budget.AvailableBudget);
    }

    [Fact]
    public void TryReserve_Succeeds_Within_Role_Limit()
    {
        var manager = new BudgetManager(_budget, _tc);
        // SystemPrompt allocation: 12% of 6144 = 737 tokens, clamped to [200, 1000]
        Assert.True(manager.TryReserve(ContextRole.SystemPrompt, 500));
    }

    [Fact]
    public void TryReserve_Fails_When_Exceeds_Total_Budget()
    {
        var manager = new BudgetManager(_budget, _tc);
        // Total available is 6144. Requesting more than total should fail.
        Assert.False(manager.TryReserve(ContextRole.SystemPrompt, 7000));
    }

    [Fact]
    public void ForceReserve_Always_Succeeds_And_Reduces_Budget()
    {
        var manager = new BudgetManager(_budget, _tc);
        manager.ForceReserve(ContextRole.SystemPrompt, 5000);
        // After force-reserving most budget, a large TryReserve should fail
        Assert.False(manager.TryReserve(ContextRole.RetrievedContext, 3000));
    }

    [Fact]
    public void Rebalance_Redistributes_Unused_Budget()
    {
        var manager = new BudgetManager(_budget, _tc);
        // Reserve only 200 tokens for system (max is 1000) — leaves room
        manager.ForceReserve(ContextRole.SystemPrompt, 200);
        manager.Rebalance();

        // After rebalance, other roles should be able to use more
        // RetrievedContext max is 4000, try something large
        Assert.True(manager.TryReserve(ContextRole.RetrievedContext, 3500));
    }

    [Fact]
    public void LargeContext_Budget_Has_Correct_Structure()
    {
        var large = ContextBudget.LargeContext(128000);
        Assert.Equal(128000 - 4096, large.AvailableBudget);
        Assert.Equal(6, large.Allocations.Count);
    }

    [Fact]
    public void BudgetAllocation_ComputeTokens_Respects_Bounds()
    {
        var alloc = new BudgetAllocation(0.50f, MinTokens: 100, MaxTokens: 500);

        // 50% of 200 = 100, which equals min
        Assert.Equal(100, alloc.ComputeTokens(200));

        // 50% of 2000 = 1000, clamped to max 500
        Assert.Equal(500, alloc.ComputeTokens(2000));

        // 50% of 600 = 300, within bounds
        Assert.Equal(300, alloc.ComputeTokens(600));
    }
}

public class ContextCompilerTests
{
    private readonly TokenCounter _tc;
    private readonly ContextCompiler _compiler;

    public ContextCompilerTests()
    {
        _tc = TokenCounter.CreateAsync().GetAwaiter().GetResult();
        _compiler = new ContextCompiler(_tc);
    }

    [Fact]
    public void Compile_Simple_Request_Includes_All_Sections()
    {
        var result = _compiler.Compile(new ContextCompileRequest
        {
            UserQuery = "What is dependency injection?",
            SystemPrompt = "You are a helpful assistant.",
            RetrievedChunks = [MakeResult("c1", "DI is a design pattern.", 0.9f)],
        });

        Assert.Equal(3, result.Sections.Count); // system + retrieval + query
        Assert.Equal(ContextRole.SystemPrompt, result.Sections[0].Role);
        Assert.Equal(ContextRole.UserQuery, result.Sections[^1].Role);
    }

    [Fact]
    public void Compile_Respects_Budget_Drops_Low_Priority()
    {
        var chunks = Enumerable.Range(1, 20).Select(i =>
            MakeResult($"c{i}", new string('x', 800), 1.0f - i * 0.04f)).ToList();

        var result = _compiler.Compile(new ContextCompileRequest
        {
            UserQuery = "test",
            SystemPrompt = "system",
            RetrievedChunks = chunks,
            Budget = ContextBudget.Default(1024), // Very tight
        });

        Assert.True(result.Metrics.SectionsDropped > 0, "Should drop some sections");
        Assert.True(result.Metrics.SectionsIncluded < 22); // Less than all 22 (20 chunks + system + query)
    }

    [Fact]
    public void Compile_Always_Includes_System_And_Query()
    {
        var result = _compiler.Compile(new ContextCompileRequest
        {
            UserQuery = "hello",
            SystemPrompt = "You are an AI.",
            Budget = ContextBudget.Default(256), // Extremely tight
        });

        Assert.Contains(result.Sections, s => s.Role == ContextRole.SystemPrompt);
        Assert.Contains(result.Sections, s => s.Role == ContextRole.UserQuery);
    }

    [Fact]
    public void Compile_CacheOptimized_Places_Cacheable_First()
    {
        var result = _compiler.Compile(new ContextCompileRequest
        {
            UserQuery = "question",
            SystemPrompt = "system prompt here",
            TeamContext = "team conventions here",
            RetrievedChunks = [MakeResult("r1", "retrieved content", 0.8f)],
        });

        // First two should be cacheable (system + team)
        Assert.True(result.Sections[0].IsCacheable);
        Assert.True(result.Sections[1].IsCacheable);
        // Last should be query
        Assert.Equal(ContextRole.UserQuery, result.Sections[^1].Role);
    }

    [Fact]
    public void Compile_PrimacyRecency_Places_Best_At_Edges()
    {
        var compiler = new ContextCompiler(_tc, PositioningStrategy.PrimacyRecency);
        var chunks = Enumerable.Range(1, 5).Select(i =>
            MakeResult($"c{i}", $"content {i}", 1.0f - i * 0.1f)).ToList();

        var result = compiler.Compile(new ContextCompileRequest
        {
            UserQuery = "test",
            SystemPrompt = "sys",
            RetrievedChunks = chunks,
        });

        // After system prompt, first dynamic section should be highest priority
        var dynamic = result.Sections.Where(s => s.Role == ContextRole.RetrievedContext).ToList();
        Assert.True(dynamic.Count >= 3);
        // First and last dynamic should have higher priority than middle
        Assert.True(dynamic[0].Priority >= dynamic[dynamic.Count / 2].Priority);
    }

    [Fact]
    public void Compile_Includes_Memories_And_History()
    {
        var result = _compiler.Compile(new ContextCompileRequest
        {
            UserQuery = "what did we decide?",
            SystemPrompt = "sys",
            Memories = [MakeResult("m1", "We decided to use CosmosDB", 0.8f)],
            ConversationHistory =
            [
                new ConversationTurn("user", "Which database?"),
                new ConversationTurn("assistant", "Let me check."),
            ],
        });

        Assert.Contains(result.Sections, s => s.Role == ContextRole.Memory);
        Assert.Contains(result.Sections, s => s.Role == ContextRole.ConversationHistory);
    }

    [Fact]
    public void Compile_Metrics_Are_Consistent()
    {
        var result = _compiler.Compile(new ContextCompileRequest
        {
            UserQuery = "test query",
            SystemPrompt = "system",
            RetrievedChunks = [MakeResult("c1", "chunk content here", 0.9f)],
        });

        var m = result.Metrics;
        Assert.Equal(result.Sections.Count, m.SectionsIncluded);
        Assert.Equal(result.Sections.Sum(s => s.TokenCount), m.TotalTokensUsed);
        Assert.True(m.BudgetUtilization > 0 && m.BudgetUtilization <= 1.0f);
        Assert.True(m.CompilationTime.TotalMicroseconds > 0);
    }

    [Fact]
    public void CompiledContext_AssembleText_Joins_All_Content()
    {
        var result = _compiler.Compile(new ContextCompileRequest
        {
            UserQuery = "hello",
            SystemPrompt = "You are helpful.",
        });

        var text = result.AssembleText();
        Assert.Contains("You are helpful.", text);
        Assert.Contains("hello", text);
    }

    [Fact]
    public void CompiledContext_CacheablePrefix_Only_Includes_Cacheable()
    {
        var result = _compiler.Compile(new ContextCompileRequest
        {
            UserQuery = "dynamic query",
            SystemPrompt = "stable system",
            TeamContext = "stable team",
            RetrievedChunks = [MakeResult("r1", "dynamic retrieval", 0.8f)],
        });

        var prefix = result.CacheablePrefix();
        Assert.Contains("stable system", prefix);
        Assert.Contains("stable team", prefix);
        Assert.DoesNotContain("dynamic query", prefix);
        Assert.DoesNotContain("dynamic retrieval", prefix);
    }

    private static SearchResult MakeResult(string id, string content, float score) => new(
        new Chunk(id, content, new ChunkMetadata("test.md", "markdown", 0, content.Length, DateTimeOffset.Now)),
        score, "test");
}

public class CachePrefixOptimizerTests
{
    private readonly TokenCounter _tc;

    public CachePrefixOptimizerTests()
    {
        _tc = TokenCounter.CreateAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void CacheHitRate_Starts_At_Zero()
    {
        var optimizer = new CachePrefixOptimizer(_tc);
        Assert.Equal(0f, optimizer.CacheHitRate);
    }

    [Fact]
    public void CacheHitRate_Tracks_Hits()
    {
        var optimizer = new CachePrefixOptimizer(_tc);
        var compiler = new ContextCompiler(_tc);

        // Same system prompt = same cacheable prefix
        var request = new ContextCompileRequest
        {
            UserQuery = "q1",
            SystemPrompt = "stable prompt",
        };

        var ctx1 = compiler.Compile(request);
        optimizer.OptimizeAndTrack(ctx1);
        Assert.Equal(0f, optimizer.CacheHitRate); // First call = miss

        var ctx2 = compiler.Compile(request with { UserQuery = "q2" });
        optimizer.OptimizeAndTrack(ctx2);
        Assert.Equal(0.5f, optimizer.CacheHitRate); // 1 hit / 2 calls

        var ctx3 = compiler.Compile(request with { UserQuery = "q3" });
        optimizer.OptimizeAndTrack(ctx3);
        Assert.True(optimizer.CacheHitRate > 0.6f); // 2 hits / 3 calls
    }

    [Fact]
    public void BuildStablePrefix_Combines_System_And_Team()
    {
        var optimizer = new CachePrefixOptimizer(_tc);
        var prefix = optimizer.BuildStablePrefix("system", "team");
        Assert.Contains("system", prefix);
        Assert.Contains("team", prefix);
    }

    [Fact]
    public void BuildStablePrefix_Without_Team_Returns_System_Only()
    {
        var optimizer = new CachePrefixOptimizer(_tc);
        var prefix = optimizer.BuildStablePrefix("system");
        Assert.Equal("system", prefix);
    }

    [Fact]
    public void EstimateSavings_Calculates_Correctly()
    {
        var optimizer = new CachePrefixOptimizer(_tc);
        var compiler = new ContextCompiler(_tc);

        // Build up some cache hits
        var request = new ContextCompileRequest { UserQuery = "q", SystemPrompt = "s" };
        optimizer.OptimizeAndTrack(compiler.Compile(request));
        optimizer.OptimizeAndTrack(compiler.Compile(request with { UserQuery = "q2" }));

        var savings = optimizer.EstimateSavings(100, queriesPerDay: 1000);
        Assert.True(savings.DailySavings > 0);
        Assert.Equal(savings.DailySavings * 365, savings.AnnualSavings);
    }

    [Fact]
    public void FindStablePrefixLength_Identifies_Common_Prefix()
    {
        var optimizer = new CachePrefixOptimizer(_tc);
        var compiler = new ContextCompiler(_tc);

        var contexts = new List<CompiledContext>
        {
            compiler.Compile(new ContextCompileRequest { UserQuery = "q1", SystemPrompt = "same prompt" }),
            compiler.Compile(new ContextCompileRequest { UserQuery = "q2", SystemPrompt = "same prompt" }),
            compiler.Compile(new ContextCompileRequest { UserQuery = "q3", SystemPrompt = "same prompt" }),
        };

        int stableTokens = optimizer.FindStablePrefixLength(contexts);
        Assert.True(stableTokens > 0, "Should find stable prefix tokens");
    }
}
