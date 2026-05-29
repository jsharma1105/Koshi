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
    public const string InvalidSplit = "invalid_split";
    public const string UnknownTeam = "unknown_team";
    public const string InsufficientData = "insufficient_data";
    public const string EmptyStore = "empty_store";
    public const string NoMatchInScope = "no_match_in_scope";
    public const string NoMatchOfType = "no_match_of_type";
    public const string NoMatchForQuery = "no_match_for_query";

    // ── Phase 2b — mutation/persistence tool error codes ────────────────
    public const string EmptyContent = "empty_content";
    public const string EmptySubject = "empty_subject";
    public const string EmptyPath = "empty_path";
    public const string EmptySummary = "empty_summary";
    public const string MemoryLimitReached = "memory_limit_reached";
    public const string PersistenceFailed = "persistence_failed";
    public const string ConfirmationRequired = "confirmation_required";
    public const string VaultOpenFailed = "vault_open_failed";
    public const string VaultNotEmpty = "vault_not_empty";
    public const string InvalidMode = "invalid_mode";
    public const string InvalidJson = "invalid_json";
    public const string NoDocuments = "no_documents";
    public const string TooManyChunks = "too_many_chunks";
    public const string DirectoryNotFound = "directory_not_found";
    public const string AccessDenied = "access_denied";
    public const string IoError = "io_error";
    public const string NoFiles = "no_files";
    public const string AllEmpty = "all_empty";
    public const string InvalidTeam = "invalid_team";
}

/// <summary>
/// Stable reason-code constants for non-error informational outcomes carried
/// in the success envelope's <c>reason</c> field (#66 Phase 2b). Like
/// <see cref="OutputErrorCodes"/>, these are frozen <c>snake_case</c> strings:
/// programmatic callers may switch on them; never rename, only add.
/// </summary>
public static class CaptureReasonCodes
{
    public const string NoDecisionsDetected = "no_decisions_detected";
    public const string AllCandidatesBelowFloor = "all_candidates_below_floor";
    public const string MemoryLimitReached = "memory_limit_reached";
    public const string AllDuplicates = "all_duplicates";
}

/// <summary>
/// Reason codes returned by <c>koshi_memory_sync_vault</c> when the operation
/// is a no-op (e.g. backend is not a vault).
/// </summary>
public static class SyncReasonCodes
{
    public const string NotAVault = "not_a_vault";
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
/// </remarks>
public static class OutputFormatting
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// Environment variable consulted when no explicit <c>format</c> parameter
    /// is passed to a tool. Accepted values: <c>text</c>, <c>json</c>
    /// (case-insensitive). Anything else logs a one-shot warning to stderr at
    /// first call and falls back to text.
    /// </summary>
    public const string FormatEnvVar = "KOSHI_OUTPUT_FORMAT";

    private static readonly Lock _envLock = new();
    private static bool _envDefaultLoaded;
    private static OutputFormat _envDefault = OutputFormat.Text;
    private static string? _envWarning;

    /// <summary>
    /// Test-only reset hook. Forces the env-var default to be re-read on the
    /// next <see cref="Resolve(string?, out string?)"/> call. Production code
    /// should never call this — the env default is intentionally cached so
    /// tools see a stable choice for the process's lifetime.
    /// </summary>
    public static void ResetEnvDefaultForTesting()
    {
        lock (_envLock)
        {
            _envDefaultLoaded = false;
            _envDefault = OutputFormat.Text;
            _envWarning = null;
        }
    }

    private static OutputFormat GetEnvDefault()
    {
        lock (_envLock)
        {
            if (_envDefaultLoaded) return _envDefault;
            _envDefaultLoaded = true;

            var raw = Environment.GetEnvironmentVariable(FormatEnvVar);
            if (string.IsNullOrWhiteSpace(raw))
            {
                _envDefault = OutputFormat.Text;
                return _envDefault;
            }

            var trimmed = raw.Trim();
            if (string.Equals(trimmed, "text", StringComparison.OrdinalIgnoreCase))
            {
                _envDefault = OutputFormat.Text;
            }
            else if (string.Equals(trimmed, "json", StringComparison.OrdinalIgnoreCase))
            {
                _envDefault = OutputFormat.Json;
            }
            else
            {
                _envDefault = OutputFormat.Text;
                _envWarning = $"[koshi] ignoring invalid {FormatEnvVar}='{raw}'; expected 'text' or 'json'. Falling back to text.";
                // Best-effort warning. Console.Error may be redirected/closed (e.g. detached
                // stdio in some MCP hosts) — IOException must not propagate from Resolve().
                try { Console.Error.WriteLine(_envWarning); }
                catch (IOException) { /* stderr unavailable — swallow */ }
                catch (ObjectDisposedException) { /* stderr disposed — swallow */ }
            }

            return _envDefault;
        }
    }

    /// <summary>
    /// Last warning emitted from the env-var parser, or <c>null</c> when the
    /// env var was unset / valid. Exposed for tests.
    /// </summary>
    public static string? LastEnvWarningForTesting
    {
        get { lock (_envLock) return _envWarning; }
    }

    /// <summary>
    /// Resolve the effective <see cref="OutputFormat"/> for a tool call.
    /// </summary>
    /// <param name="formatParam">
    /// The user-supplied <c>format</c> argument. <c>null</c> / whitespace
    /// means "no explicit choice — consult <see cref="FormatEnvVar"/>".
    /// Recognised values (case-insensitive, trimmed): <c>text</c>, <c>json</c>.
    /// </param>
    /// <param name="error">
    /// Populated with a descriptive error when <paramref name="formatParam"/>
    /// is non-null but unrecognised. <c>null</c> on success.
    /// </param>
    /// <returns>
    /// The resolved format. Returns the env-var default when no explicit
    /// param was passed. On unrecognised value: returns <see cref="OutputFormat.Text"/>
    /// unless the input contains "json" (case-insensitive), in which case
    /// returns <see cref="OutputFormat.Json"/> so the error envelope is
    /// JSON-shaped for programmatic callers.
    /// </returns>
    public static OutputFormat Resolve(string? formatParam, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(formatParam))
        {
            return GetEnvDefault();
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
    /// Serialize a success envelope <c>{ schema_version, ok:true, tool, data, error:null }</c>.
    /// </summary>
    public static string Ok<T>(string tool, T data, JsonTypeInfo<JsonEnvelope<T>> envelopeTypeInfo)
    {
        var envelope = new JsonEnvelope<T>(
            SchemaVersion: SchemaVersion,
            Ok: true,
            Tool: tool,
            Data: data,
            Error: null);
        return JsonSerializer.Serialize(envelope, envelopeTypeInfo);
    }

    /// <summary>
    /// Serialize an error envelope <c>{ schema_version, ok:false, tool, data:null, error:{code,message} }</c>.
    /// </summary>
    public static string Error<T>(
        string tool,
        string code,
        string message,
        JsonTypeInfo<JsonEnvelope<T>> envelopeTypeInfo)
    {
        var envelope = new JsonEnvelope<T>(
            SchemaVersion: SchemaVersion,
            Ok: false,
            Tool: tool,
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
/// <param name="Tool">Canonical MCP tool name (e.g. <c>koshi_search</c>). Useful for log aggregation and orchestrator routing.</param>
/// <param name="Data">Per-tool typed payload. Null when <see cref="Ok"/> is false.</param>
/// <param name="Error">Structured error info; null when <see cref="Ok"/> is true. Programmatic callers should switch on <see cref="JsonErrorDetail.Code"/>.</param>
public sealed record JsonEnvelope<T>(
    int SchemaVersion,
    bool Ok,
    string Tool,
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
