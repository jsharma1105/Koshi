using System.Text.Json.Serialization;
using Koshi.Core.Context;
using Koshi.Core.Memory;
using Koshi.Core.Models;
using Koshi.Core.Team;

namespace Koshi.Mcp.Internal;

/// <summary>
/// AOT-safe source-generated JSON serialization context for Koshi.Mcp.
/// Every concrete type passed to <c>JsonSerializer.Serialize</c> / <c>Deserialize</c>
/// inside Koshi.Mcp must be enumerated here so the source generator emits
/// reflection-free metadata for it. Adding a new <c>JsonSerializer</c> call site
/// without a matching <c>[JsonSerializable]</c> line will trigger an IL2026/IL3050
/// warning under <c>IsAotCompatible</c>, which is treated as an error.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [
        typeof(JsonStringEnumConverter<MemoryType>),
        typeof(JsonStringEnumConverter<MemoryTier>),
        typeof(JsonStringEnumConverter<TeamRole>),
        typeof(JsonStringEnumConverter<PositioningStrategy>),
        typeof(JsonStringEnumConverter<FeedbackIssue>),
    ])]
[JsonSerializable(typeof(PersistenceEnvelope))]
[JsonSerializable(typeof(MemoryRecord))]
[JsonSerializable(typeof(List<MemoryRecord>))]
[JsonSerializable(typeof(MemoryScope))]
[JsonSerializable(typeof(MemoryType))]
[JsonSerializable(typeof(MemoryTier))]
[JsonSerializable(typeof(DocInput))]
[JsonSerializable(typeof(List<DocInput>))]
[JsonSerializable(typeof(IndexEnvelope))]
[JsonSerializable(typeof(IndexEnumerationParams))]
[JsonSerializable(typeof(Chunk))]
[JsonSerializable(typeof(ChunkMetadata))]
[JsonSerializable(typeof(List<Chunk>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(TeamsEnvelope))]
[JsonSerializable(typeof(TeamProfile))]
[JsonSerializable(typeof(List<TeamProfile>))]
[JsonSerializable(typeof(TeamConfig))]
[JsonSerializable(typeof(TeamMember))]
[JsonSerializable(typeof(List<TeamMember>))]
[JsonSerializable(typeof(QualityScore))]
[JsonSerializable(typeof(List<QualityScore>))]
[JsonSerializable(typeof(QualityFeedback))]
[JsonSerializable(typeof(List<QualityFeedback>))]
[JsonSerializable(typeof(Dictionary<string, List<QualityScore>>))]
[JsonSerializable(typeof(Dictionary<string, List<QualityFeedback>>))]
internal sealed partial class KoshiJsonContext : JsonSerializerContext;
