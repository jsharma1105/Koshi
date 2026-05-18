using Koshi.Core.Models;

namespace Koshi.Mcp.Internal;

/// <summary>
/// On-disk envelope for the BM25 index snapshot (<c>KOSHI_INDEX_FILE</c>).
/// Persists the chunked corpus plus enough provenance metadata to validate
/// the snapshot against the live source directory on load. BM25 statistics
/// (IDF / TF tables) are NOT serialized — they are deterministic in the
/// chunk content, so on load we replay <see cref="Koshi.Core.Retrieval.KeywordRetriever.Index"/>.
/// This keeps the snapshot smaller and tolerant of future BM25 tweaks.
/// </summary>
internal sealed class IndexEnvelope
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset SavedAt { get; set; }

    /// <summary>
    /// The directory that produced the snapshot, or <c>"in-memory"</c> when
    /// the corpus came from <c>koshi_index</c> (no on-disk source).
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Fingerprint of the source directory at save time. Null when the
    /// corpus has no on-disk source. On load we recompute and compare —
    /// a mismatch invalidates the snapshot and forces a re-index.
    /// </summary>
    public string? ContentFingerprint { get; set; }

    /// <summary>
    /// Enumeration parameters used to produce this snapshot. Re-applied on
    /// load when recomputing <see cref="ContentFingerprint"/> so the same
    /// view of the directory is hashed at both save and load time.
    /// Null when the corpus has no on-disk source.
    /// </summary>
    public IndexEnumerationParams? Enumeration { get; set; }

    public List<Chunk> Chunks { get; set; } = [];
}

/// <summary>Parameters that determined which files <c>koshi_index_directory</c> enumerated.</summary>
internal sealed class IndexEnumerationParams
{
    public string? Pattern { get; set; }
    public long MaxFileSizeBytes { get; set; }
    public int MaxFiles { get; set; }
}
