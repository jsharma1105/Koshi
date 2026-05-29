using Koshi.Agents.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for <see cref="NextStepsBanner"/> — the post-install banner shown
/// by <c>koshi-agents install</c>. The renderer is a pure function, so we
/// can drive every outcome (Created / Added / AlreadyPresent / DryRun /
/// Error / skippedMcp) and pin the output contract.
/// </summary>
public sealed class NextStepsBannerTests
{
    private const string FakeConfigPath = @"C:\fake\config.json";
    private const string FakeDir = @"C:\fake\agents";

    private static string RenderJoined(
        PersonaClient client,
        McpRegisterResult? mcpResult,
        bool skippedMcp)
    {
        var lines = NextStepsBanner.Render(client, FakeDir, mcpResult, skippedMcp);
        return string.Join("\n", lines);
    }

    [Fact]
    public void Render_Copilot_SkippedMcp_ShowsManualRegistrationCommand()
    {
        var output = RenderJoined(PersonaClient.Copilot, mcpResult: null, skippedMcp: true);

        Assert.Contains("Copilot CLI", output);
        Assert.Contains("Register the", output);
        Assert.Contains("copilot mcp add koshi -- koshi-mcp", output);
        Assert.Contains("\"koshi\":", output);
        Assert.Contains("\"command\": \"koshi-mcp\"", output);
    }

    [Fact]
    public void Render_Claude_SkippedMcp_ShowsManualJsonSnippet()
    {
        var output = RenderJoined(PersonaClient.Claude, mcpResult: null, skippedMcp: true);

        Assert.Contains("Claude Desktop", output);
        Assert.Contains("Register the", output);
        Assert.Contains("\"koshi\":", output);
        Assert.Contains("\"command\": \"koshi-mcp\"", output);
        // Claude's snippet has no "type": "local" line.
        Assert.DoesNotContain("\"type\": \"local\"", output);
    }

    [Fact]
    public void Render_Created_OutcomeReportsAutoRegisteredAndConfigPath()
    {
        var result = new McpRegisterResult(McpRegisterOutcome.Created, FakeConfigPath);
        var output = RenderJoined(PersonaClient.Copilot, result, skippedMcp: false);

        Assert.Contains("koshi MCP server registered", output);
        Assert.Contains(FakeConfigPath, output);
        Assert.DoesNotContain("copilot mcp add koshi", output);
    }

    [Fact]
    public void Render_Added_OutcomeReportsAutoRegistered()
    {
        var result = new McpRegisterResult(
            McpRegisterOutcome.Added,
            FakeConfigPath,
            BackupPath: @"C:\fake\config.json.bak");
        var output = RenderJoined(PersonaClient.Claude, result, skippedMcp: false);

        Assert.Contains("koshi MCP server registered", output);
        Assert.Contains(FakeConfigPath, output);
    }

    [Fact]
    public void Render_AlreadyPresent_OutcomeReportsIdempotentNoOp()
    {
        var result = new McpRegisterResult(McpRegisterOutcome.AlreadyPresent, FakeConfigPath);
        var output = RenderJoined(PersonaClient.Claude, result, skippedMcp: false);

        Assert.Contains("already present", output);
        Assert.Contains("left untouched", output);
        Assert.Contains(FakeConfigPath, output);
    }

    [Fact]
    public void Render_Error_OutcomeFallsBackToManualInstructions()
    {
        var result = new McpRegisterResult(
            McpRegisterOutcome.Error,
            FakeConfigPath,
            ErrorMessage: "permission denied");
        var output = RenderJoined(PersonaClient.Copilot, result, skippedMcp: false);

        Assert.Contains("Could not auto-register", output);
        Assert.Contains("permission denied", output);
        Assert.Contains(FakeConfigPath, output);
        // Fall-back: manual instructions are emitted too.
        Assert.Contains("copilot mcp add koshi -- koshi-mcp", output);
        Assert.Contains("\"koshi\":", output);
    }

    [Fact]
    public void Render_DryRun_OutcomeTellsUserToReRun()
    {
        var result = new McpRegisterResult(McpRegisterOutcome.DryRun, FakeConfigPath);
        var output = RenderJoined(PersonaClient.Copilot, result, skippedMcp: false);

        Assert.Contains("Dry-run only", output);
        Assert.Contains("--dry-run", output);
    }

    [Fact]
    public void Render_AlwaysIncludesRestartAndDoctorAndPersonaSmokeCommand()
    {
        var result = new McpRegisterResult(McpRegisterOutcome.Created, FakeConfigPath);
        var output = RenderJoined(PersonaClient.Copilot, result, skippedMcp: false);

        Assert.Contains("Restart", output);
        Assert.Contains("koshi-agents doctor", output);
        Assert.Contains("koshi-orchestrator", output);
    }

    [Fact]
    public void Render_Copilot_SmokeCommandUsesCopilotAgentFlag()
    {
        var result = new McpRegisterResult(McpRegisterOutcome.Created, FakeConfigPath);
        var output = RenderJoined(PersonaClient.Copilot, result, skippedMcp: false);

        Assert.Contains("copilot --agent koshi-orchestrator", output);
    }

    [Fact]
    public void Render_Claude_SmokeCommandUsesAtMention()
    {
        var result = new McpRegisterResult(McpRegisterOutcome.Created, FakeConfigPath);
        var output = RenderJoined(PersonaClient.Claude, result, skippedMcp: false);

        Assert.Contains("@koshi-orchestrator", output);
    }

    [Fact]
    public void Render_AlwaysIncludesInertWarning()
    {
        var result = new McpRegisterResult(McpRegisterOutcome.Created, FakeConfigPath);
        var output = RenderJoined(PersonaClient.Copilot, result, skippedMcp: false);

        Assert.Contains("inert", output);
    }

    [Fact]
    public void Render_NullMcpResult_NotSkipped_StillEmitsRestartAndVerifySteps()
    {
        // Defensive: caller forgot to register but didn't pass --no-mcp.
        // Banner shouldn't crash and should still cover restart + verify.
        var output = RenderJoined(PersonaClient.Copilot, mcpResult: null, skippedMcp: false);

        Assert.Contains("Restart", output);
        Assert.Contains("koshi-agents doctor", output);
        Assert.Contains("koshi-orchestrator", output);
    }

    [Fact]
    public void Render_LinesAreNonEmpty_AndEscapeMarkupSafely()
    {
        // Path with brackets would break Spectre markup if not escaped.
        var trickyPath = @"C:\fake\[brackets]\config.json";
        var result = new McpRegisterResult(McpRegisterOutcome.AlreadyPresent, trickyPath);
        var lines = NextStepsBanner.Render(PersonaClient.Copilot, FakeDir, result, skippedMcp: false);

        Assert.NotEmpty(lines);
        // Escaped form replaces [ with [[ and ] with ]].
        var joined = string.Join("\n", lines);
        Assert.Contains("[[brackets]]", joined);
        Assert.DoesNotContain(@"\[brackets\]", joined);
    }
}
