using Koshi.Mcp.Internal;

namespace Koshi.Core.Tests;

/// <summary>
/// Tests for <c>KOSHI_OUTPUT_FORMAT</c> env-var resolution (#66 Phase 2a).
/// </summary>
/// <remarks>
/// These tests mutate process environment state and the cached env default
/// inside <see cref="OutputFormatting"/>. They share the
/// <c>OutputFormatting-env</c> xUnit collection with <see cref="OutputFormattingTests"/>
/// so they run serially — never in parallel with other env-sensitive tests.
/// Each test resets state in its ctor and in <see cref="Dispose"/>.
/// </remarks>
[Collection("OutputFormatting-env")]
public class OutputFormatEnvVarTests : IDisposable
{
    public OutputFormatEnvVarTests()
    {
        Environment.SetEnvironmentVariable(OutputFormatting.FormatEnvVar, null);
        OutputFormatting.ResetEnvDefaultForTesting();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(OutputFormatting.FormatEnvVar, null);
        OutputFormatting.ResetEnvDefaultForTesting();
    }

    [Fact]
    public void Env_unset_defaults_to_text()
    {
        var format = OutputFormatting.Resolve(null, out var error);
        Assert.Equal(OutputFormat.Text, format);
        Assert.Null(error);
        Assert.Null(OutputFormatting.LastEnvWarningForTesting);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("JSON")]
    [InlineData(" Json ")]
    public void Env_json_makes_default_json_when_no_explicit_param(string envValue)
    {
        Environment.SetEnvironmentVariable(OutputFormatting.FormatEnvVar, envValue);
        OutputFormatting.ResetEnvDefaultForTesting();

        var format = OutputFormatting.Resolve(null, out var error);
        Assert.Equal(OutputFormat.Json, format);
        Assert.Null(error);
        Assert.Null(OutputFormatting.LastEnvWarningForTesting);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("TEXT")]
    public void Env_text_keeps_default_text(string envValue)
    {
        Environment.SetEnvironmentVariable(OutputFormatting.FormatEnvVar, envValue);
        OutputFormatting.ResetEnvDefaultForTesting();

        var format = OutputFormatting.Resolve(null, out var error);
        Assert.Equal(OutputFormat.Text, format);
        Assert.Null(error);
    }

    [Fact]
    public void Explicit_text_param_wins_over_env_json()
    {
        Environment.SetEnvironmentVariable(OutputFormatting.FormatEnvVar, "json");
        OutputFormatting.ResetEnvDefaultForTesting();

        var format = OutputFormatting.Resolve("text", out var error);
        Assert.Equal(OutputFormat.Text, format);
        Assert.Null(error);
    }

    [Fact]
    public void Explicit_json_param_wins_over_env_text()
    {
        Environment.SetEnvironmentVariable(OutputFormatting.FormatEnvVar, "text");
        OutputFormatting.ResetEnvDefaultForTesting();

        var format = OutputFormatting.Resolve("json", out var error);
        Assert.Equal(OutputFormat.Json, format);
        Assert.Null(error);
    }

    [Fact]
    public void Env_invalid_value_logs_warning_and_falls_back_to_text()
    {
        Environment.SetEnvironmentVariable(OutputFormatting.FormatEnvVar, "yaml");
        OutputFormatting.ResetEnvDefaultForTesting();

        // Redirect stderr to capture the warning.
        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            var format = OutputFormatting.Resolve(null, out var error);
            Assert.Equal(OutputFormat.Text, format);
            Assert.Null(error);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var warning = OutputFormatting.LastEnvWarningForTesting;
        Assert.NotNull(warning);
        Assert.Contains("KOSHI_OUTPUT_FORMAT", warning);
        Assert.Contains("yaml", warning);
        Assert.Contains(sw.ToString(), warning + Environment.NewLine);
    }

    [Fact]
    public void Env_value_is_cached_and_only_read_once()
    {
        Environment.SetEnvironmentVariable(OutputFormatting.FormatEnvVar, "json");
        OutputFormatting.ResetEnvDefaultForTesting();

        var first = OutputFormatting.Resolve(null, out _);
        Assert.Equal(OutputFormat.Json, first);

        // Mutate the env var; without ResetEnvDefaultForTesting the cached
        // value should still be returned (this is the production behavior:
        // tools should see a stable default across the process lifetime).
        Environment.SetEnvironmentVariable(OutputFormatting.FormatEnvVar, "text");

        var second = OutputFormatting.Resolve(null, out _);
        Assert.Equal(OutputFormat.Json, second);
    }

    [Fact]
    public void Env_warning_is_only_emitted_once_per_load()
    {
        Environment.SetEnvironmentVariable(OutputFormatting.FormatEnvVar, "bogus");
        OutputFormatting.ResetEnvDefaultForTesting();

        var originalErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try
        {
            OutputFormatting.Resolve(null, out _);
            OutputFormatting.Resolve(null, out _);
            OutputFormatting.Resolve(null, out _);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var stderr = sw.ToString();
        var occurrences = stderr.Split("KOSHI_OUTPUT_FORMAT").Length - 1;
        Assert.Equal(1, occurrences);
    }
}

/// <summary>
/// Shared xUnit collection so env-mutating tests run serially. xUnit
/// guarantees that classes in the same Collection do not execute in parallel.
/// </summary>
[CollectionDefinition("OutputFormatting-env", DisableParallelization = true)]
public sealed class OutputFormattingEnvCollection { }
