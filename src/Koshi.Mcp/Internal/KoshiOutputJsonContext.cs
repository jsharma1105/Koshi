using System.Text.Json;
using System.Text.Json.Serialization;
using static Koshi.Mcp.Internal.JsonShapes;

namespace Koshi.Mcp.Internal;

/// <summary>
/// AOT-safe source-generated JSON context for the structured-output envelopes
/// introduced in issue #66 (Phase 1).
/// </summary>
/// <remarks>
/// <para>
/// Every <c>JsonEnvelope&lt;T&gt;</c> closed generic shape used by Phase 1 tools
/// must be enumerated here. Adding a new tool with a new payload type means:
/// (1) define the DTO in <see cref="JsonShapes"/>, (2) register
/// <c>JsonEnvelope&lt;NewPayload&gt;</c> below.
/// </para>
/// <para>
/// Keep this SEPARATE from <see cref="KoshiJsonContext"/> — the latter has
/// <c>WriteIndented = true</c> and <c>DefaultIgnoreCondition = WhenWritingNull</c>
/// because it serialises persistence envelopes for humans. The output here is
/// machine-readable, never indented (cheaper bytes over the wire) and ALWAYS
/// emits nulls so the <c>error</c> / <c>data</c> envelope contract is stable.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(JsonEnvelope<SearchResultData>))]
[JsonSerializable(typeof(JsonEnvelope<RecallResultData>))]
[JsonSerializable(typeof(JsonEnvelope<CompileContextResultData>))]
[JsonSerializable(typeof(JsonEnvelope<HealthResultData>))]
[JsonSerializable(typeof(JsonEnvelope<ScoreTurnResultData>))]
[JsonSerializable(typeof(JsonEnvelope<VersionResultData>))]
[JsonSerializable(typeof(JsonEnvelope<TokenCountResultData>))]
[JsonSerializable(typeof(JsonEnvelope<BudgetPlanResultData>))]
[JsonSerializable(typeof(JsonEnvelope<MemoryStatsResultData>))]
[JsonSerializable(typeof(JsonEnvelope<ListIndexedResultData>))]
[JsonSerializable(typeof(JsonEnvelope<ListTeamsResultData>))]
[JsonSerializable(typeof(JsonEnvelope<TeamDashboardResultData>))]
[JsonSerializable(typeof(JsonEnvelope<AnalyzeFeedbackResultData>))]
internal sealed partial class KoshiOutputJsonContext : JsonSerializerContext;
