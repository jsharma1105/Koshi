using System.Text.Json;
using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// Shared assertions for the structured-output envelope contract (#66 Phase 1).
/// Use these from every per-tool JSON-mode test so the global shape rules stay
/// uniform: <c>{ schema_version, ok, data, error }</c>, no text-only artefacts
/// leaking in, error envelopes always carry a stable <c>code</c>.
/// </summary>
internal static class StructuredOutputAssertions
{
    /// <summary>
    /// Parse a tool's JSON-mode string output and assert it represents a
    /// successful envelope. Returns the parsed <c>data</c> element for the
    /// caller to drill into. When <paramref name="expectedTool"/> is provided,
    /// asserts the envelope's <c>tool</c> field equals it.
    /// </summary>
    public static JsonElement AssertOkEnvelope(string output, string? expectedTool = null)
    {
        AssertNoTextPollution(output);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement.Clone();

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(OutputFormatting.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean(), $"Expected ok=true; envelope was: {output}");
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
        AssertToolField(root, expectedTool);

        var data = root.GetProperty("data");
        Assert.NotEqual(JsonValueKind.Null, data.ValueKind);
        return data.Clone();
    }

    /// <summary>
    /// Parse a tool's JSON-mode string output and assert it represents an
    /// error envelope with the expected stable error code. When
    /// <paramref name="expectedTool"/> is provided, asserts the envelope's
    /// <c>tool</c> field equals it.
    /// </summary>
    public static void AssertErrorEnvelope(string output, string expectedCode, string? expectedTool = null)
    {
        AssertNoTextPollution(output);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(OutputFormatting.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.False(root.GetProperty("ok").GetBoolean(), $"Expected ok=false; envelope was: {output}");
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        AssertToolField(root, expectedTool);

        var error = root.GetProperty("error");
        Assert.Equal(JsonValueKind.Object, error.ValueKind);
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        var message = error.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message), "error.message must be a non-empty plain-text string");
        AssertNoEmojiOrBoxDrawing(message!);
    }

    private static void AssertToolField(JsonElement root, string? expectedTool)
    {
        Assert.True(root.TryGetProperty("tool", out var toolEl), "Envelope must include 'tool' field.");
        Assert.Equal(JsonValueKind.String, toolEl.ValueKind);
        var tool = toolEl.GetString();
        Assert.False(string.IsNullOrWhiteSpace(tool), "tool must be non-empty.");
        Assert.StartsWith("koshi_", tool);
        if (expectedTool is not null)
            Assert.Equal(expectedTool, tool);
    }

    /// <summary>
    /// Assert a JSON-mode response contains none of the human-formatting
    /// artefacts (emojis, box-drawing) used by the text path. This is the
    /// canonical "the JSON is actually clean" guard.
    /// </summary>
    public static void AssertNoTextPollution(string output)
    {
        Assert.False(string.IsNullOrWhiteSpace(output), "Output must not be empty.");
        // The envelope itself is a JSON object — assert that first so a stray
        // text response triggers a clearer failure than the substring scan.
        Assert.StartsWith("{", output.TrimStart());
        AssertNoEmojiOrBoxDrawing(output);
    }

    private static void AssertNoEmojiOrBoxDrawing(string value)
    {
        // Text-mode decorations used across the Koshi tools. Any of these
        // leaking into a JSON envelope means a code-path forgot to strip
        // text formatting or branch on OutputFormat.
        var forbidden = new[]
        {
            // Box-drawing characters used by every text formatter in the codebase.
            "═", "─", "│", "┌", "┐", "└", "┘", "├", "┤",
            // Severity emoji.
            "❌", "✅", "⚠️", "⚠",
            // Dashboard/health emoji.
            "📊", "🟢", "🟡", "🔴", "🟠",
            // Phase 2b memory/capture tool emoji (#66 N9 — must be kept in
            // sync with MemoryTools.cs / RetrievalTools.cs text output).
            "🔢", "ℹ", "📋", "⏭",
        };
        foreach (var token in forbidden)
            Assert.DoesNotContain(token, value);
    }
}
