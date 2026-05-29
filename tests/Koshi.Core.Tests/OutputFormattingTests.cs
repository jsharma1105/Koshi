using Koshi.Mcp.Internal;
using static Koshi.Mcp.Internal.JsonShapes;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for <see cref="OutputFormatting"/> — the helper that resolves the
/// <c>format</c> param and serialises envelopes for #66 Phase 1.
/// </summary>
public class OutputFormattingTests
{
    [Fact]
    public void Resolve_returns_text_when_param_is_null()
    {
        var format = OutputFormatting.Resolve(null, out var error);
        Assert.Equal(OutputFormat.Text, format);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Resolve_returns_text_when_param_is_whitespace(string param)
    {
        var format = OutputFormatting.Resolve(param, out var error);
        Assert.Equal(OutputFormat.Text, format);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("TEXT")]
    [InlineData("Text")]
    [InlineData(" text ")]
    public void Resolve_accepts_text_case_insensitively_with_trim(string param)
    {
        var format = OutputFormatting.Resolve(param, out var error);
        Assert.Equal(OutputFormat.Text, format);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("JSON")]
    [InlineData("Json")]
    [InlineData(" json ")]
    public void Resolve_accepts_json_case_insensitively_with_trim(string param)
    {
        var format = OutputFormatting.Resolve(param, out var error);
        Assert.Equal(OutputFormat.Json, format);
        Assert.Null(error);
    }

    [Fact]
    public void Resolve_rejects_unknown_value_and_returns_error()
    {
        var format = OutputFormatting.Resolve("xml", out var error);
        Assert.Equal(OutputFormat.Text, format);
        Assert.NotNull(error);
        Assert.Contains("xml", error);
        Assert.Contains("text", error);
        Assert.Contains("json", error);
    }

    [Fact]
    public void Resolve_returns_json_for_unknown_value_that_looks_jsonish()
    {
        // So the resulting error envelope is at least parseable by the caller
        // who clearly wanted JSON.
        var format = OutputFormatting.Resolve("application/json", out var error);
        Assert.Equal(OutputFormat.Json, format);
        Assert.NotNull(error);
    }

    [Fact]
    public void IsJson_returns_true_for_json_param()
    {
        Assert.True(OutputFormatting.IsJson("json"));
        Assert.True(OutputFormatting.IsJson("JSON"));
    }

    [Fact]
    public void IsJson_returns_false_for_default()
    {
        Assert.False(OutputFormatting.IsJson(null));
        Assert.False(OutputFormatting.IsJson(""));
        Assert.False(OutputFormatting.IsJson("text"));
    }

    [Fact]
    public void Ok_serializes_full_envelope_shape()
    {
        var data = new SearchResultData("hello", "default", 0, new List<SearchHitData>());
        var output = OutputFormatting.Ok(data, KoshiOutputJsonContext.Default.JsonEnvelopeSearchResultData);
        StructuredOutputAssertions.AssertOkEnvelope(output);
    }

    [Fact]
    public void Ok_envelope_uses_snake_case()
    {
        var data = new SearchResultData("hello", "default", 5, new List<SearchHitData>
        {
            new(1, 0.95, "src/foo.cs", "chunk-1", "hello world"),
        });
        var output = OutputFormatting.Ok(data, KoshiOutputJsonContext.Default.JsonEnvelopeSearchResultData);
        Assert.Contains("\"schema_version\":1", output);
        Assert.Contains("\"chunk_id\":\"chunk-1\"", output);
        Assert.DoesNotContain("ChunkId", output);
        Assert.DoesNotContain("SchemaVersion", output);
    }

    [Fact]
    public void Ok_envelope_always_includes_error_null_key()
    {
        var data = new SearchResultData("q", "default", 0, new List<SearchHitData>());
        var output = OutputFormatting.Ok(data, KoshiOutputJsonContext.Default.JsonEnvelopeSearchResultData);
        Assert.Contains("\"error\":null", output);
    }

    [Fact]
    public void Error_serializes_full_envelope_shape()
    {
        var output = OutputFormatting.Error<SearchResultData>(
            OutputErrorCodes.EmptyQuery,
            "Query must not be empty.",
            KoshiOutputJsonContext.Default.JsonEnvelopeSearchResultData);
        StructuredOutputAssertions.AssertErrorEnvelope(output, OutputErrorCodes.EmptyQuery);
    }

    [Fact]
    public void Error_envelope_always_includes_data_null_key()
    {
        var output = OutputFormatting.Error<SearchResultData>(
            OutputErrorCodes.NoIndex,
            "No corpus has been indexed.",
            KoshiOutputJsonContext.Default.JsonEnvelopeSearchResultData);
        Assert.Contains("\"data\":null", output);
    }

    [Fact]
    public void Error_codes_are_snake_case_no_emoji()
    {
        // Reflective spot-check of the public constants — guards against a
        // future contributor sneaking in formatting characters.
        var codes = new[]
        {
            OutputErrorCodes.EmptyQuery,
            OutputErrorCodes.NoIndex,
            OutputErrorCodes.UnknownCorpus,
            OutputErrorCodes.AutoIndexFailed,
            OutputErrorCodes.InvalidFormat,
            OutputErrorCodes.UnknownTeam,
            OutputErrorCodes.EmptyStore,
            OutputErrorCodes.NoMatchInScope,
            OutputErrorCodes.NoMatchOfType,
            OutputErrorCodes.NoMatchForQuery,
        };
        foreach (var code in codes)
        {
            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.Equal(code.ToLowerInvariant(), code);
            Assert.DoesNotContain(" ", code);
            Assert.DoesNotContain("-", code);
            Assert.Matches("^[a-z][a-z0-9_]*$", code);
        }
    }
}
