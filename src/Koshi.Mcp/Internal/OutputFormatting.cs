using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Mode for a tool's return value (#66).
/// <list type="bullet">
///   <item><see cref="Text"/> — the legacy human-readable formatted string. Default.</item>
///   <item><see cref="Json"/> — a stable structured envelope (see <see cref="OutputFormatting"/>).</item>
/// </list>
/// </summary>
public enum OutputFormat
{
    Text,
    Json,
}

/// <summary>
/// Stable identifier for a tool-side error condition (#66). Returned in the
/// JSON error envelope so programmatic callers can switch on a code rather
/// than regex-parsing a localised message. Codes are <c>snake_case</c> and
/// frozen — never rename, only add.
/// </summary>
public static class OutputErrorCodes
{
    public const string EmptyQuery = "empty_query";
    public const string NoIndex = "no_index";
    public const string UnknownCorpus = "unknown_corpus";
    public const string AutoIndexFailed = "auto_index_failed";
    public const string InvalidFormat = "invalid_format";
    public const string UnknownTeam = "unknown_team";
    public const string EmptyStore = "empty_store";
    public const string NoMatchInScope = "no_match_in_scope";
    public const string NoMatchOfType = "no_match_of_type";
    public const string NoMatchForQuery = "no_match_for_query";
}

/// <summary>
/// Helpers for resolving the <see cref="OutputFormat"/> from a tool's optional
/// <c>format</c> parameter and serialising responses through the AOT-safe
/// <see cref="KoshiOutputJsonContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Resolution precedence (per rubber-duck #66): explicit param > <c>KOSHI_OUTPUT_FORMAT</c>
/// env var > <see cref="OutputFormat.Text"/>. An explicit <c>format="text"</c>
/// always wins over the env var so a human user can drop into text output even
/// inside a JSON-default workspace.
/// </para>
/// <para>
/// <strong>NOTE for Phase 1:</strong> the <c>KOSHI_OUTPUT_FORMAT</c> env var is intentionally NOT
/// consulted yet — only the 5 high-value tools support JSON in this PR and
/// honouring a global env var would mislead orchestrators about coverage.
/// Phase 2 (issue #66 follow-up) adds <c>format</c> to the remaining 19 tools and
/// turns the env var on.
/// </para>
/// </remarks>
public static class OutputFormatting
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// Resolve the effective <see cref="OutputFormat"/> for a tool call.
    /// </summary>
    /// <param name="formatParam">
    /// The user-supplied <c>format</c> argument. <c>null</c> / whitespace
    /// means "no explicit choice — consult defaults". Recognised values
    /// (case-insensitive, trimmed): <c>text</c>, <c>json</c>.
    /// </param>
    /// <param name="error">
    /// Populated with a descriptive error when <paramref name="formatParam"/>
    /// is non-null but unrecognised. <c>null</c> on success.
    /// </param>
    /// <returns>
    /// The resolved format. Returns <see cref="OutputFormat.Text"/> on error
    /// so the caller can choose to surface the error in text or JSON shape
    /// depending on whether the input looked JSON-like.
    /// </returns>
    public static OutputFormat Resolve(string? formatParam, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(formatParam))
        {
            // Phase 2 will consult KOSHI_OUTPUT_FORMAT here.
            return OutputFormat.Text;
        }

        var trimmed = formatParam.Trim();
        if (string.Equals(trimmed, "text", StringComparison.OrdinalIgnoreCase))
            return OutputFormat.Text;
        if (string.Equals(trimmed, "json", StringComparison.OrdinalIgnoreCase))
            return OutputFormat.Json;

        error = $"Unknown format '{formatParam}'. Supported values: 'text', 'json'.";
        // If the caller asked for something JSON-shaped, return JSON so the
        // error itself comes back as a parseable envelope; otherwise default
        // to text so a human typing 'format=xml' sees a plain message.
        if (trimmed.Contains("json", StringComparison.OrdinalIgnoreCase))
            return OutputFormat.Json;
        return OutputFormat.Text;
    }

    /// <summary>
    /// Convenience: returns true when the resolved format is <see cref="OutputFormat.Json"/>.
    /// </summary>
    public static bool IsJson(string? formatParam)
        => Resolve(formatParam, out _) == OutputFormat.Json;

    /// <summary>
    /// Strip leading text-mode decorations (e.g. ❌ / ⚠ markers) from a
    /// message before embedding it in a JSON error envelope. Keeps the JSON
    /// output free of emoji per the #66 contract.
    /// </summary>
    public static string StripTextDecorations(string text)
        => text.Replace("❌ ", string.Empty)
               .Replace("❌", string.Empty)
               .Replace("⚠ ", string.Empty)
               .Replace("⚠️ ", string.Empty)
               .Replace("⚠️", string.Empty)
               .Replace("⚠", string.Empty)
               .Trim();

    /// <summary>
    /// Serialize a success envelope <c>{ schema_version, ok:true, data, error:null }</c>.
    /// </summary>
    public static string Ok<T>(T data, JsonTypeInfo<JsonEnvelope<T>> envelopeTypeInfo)
    {
        var envelope = new JsonEnvelope<T>(
            SchemaVersion: SchemaVersion,
            Ok: true,
            Data: data,
            Error: null);
        return JsonSerializer.Serialize(envelope, envelopeTypeInfo);
    }

    /// <summary>
    /// Serialize an error envelope <c>{ schema_version, ok:false, data:null, error:{code,message} }</c>.
    /// </summary>
    public static string Error<T>(
        string code,
        string message,
        JsonTypeInfo<JsonEnvelope<T>> envelopeTypeInfo)
    {
        var envelope = new JsonEnvelope<T>(
            SchemaVersion: SchemaVersion,
            Ok: false,
            Data: default,
            Error: new JsonErrorDetail(code, message));
        return JsonSerializer.Serialize(envelope, envelopeTypeInfo);
    }
}

/// <summary>
/// Stable envelope wrapping every JSON-mode tool response (#66).
/// </summary>
/// <param name="SchemaVersion">Bumped when the envelope shape itself changes — not for per-tool data shape evolution. Always 1 today.</param>
/// <param name="Ok">True for normal returns (including "no results" / "empty store"). False for validation failures, unknown resources, and internal errors.</param>
/// <param name="Data">Per-tool typed payload. Null when <see cref="Ok"/> is false.</param>
/// <param name="Error">Structured error info; null when <see cref="Ok"/> is true. Programmatic callers should switch on <see cref="JsonErrorDetail.Code"/>.</param>
public sealed record JsonEnvelope<T>(
    int SchemaVersion,
    bool Ok,
    T? Data,
    JsonErrorDetail? Error);

/// <summary>
/// Machine-readable error info embedded inside a <see cref="JsonEnvelope{T}"/>.
/// </summary>
/// <param name="Code">Stable identifier from <see cref="OutputErrorCodes"/>. Frozen string contract.</param>
/// <param name="Message">Human-readable plain-text message — no emoji, no box-drawing.</param>
public sealed record JsonErrorDetail(
    string Code,
    string Message);
