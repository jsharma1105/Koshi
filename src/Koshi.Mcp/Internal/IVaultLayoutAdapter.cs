using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Per-flavor strategy for file layout inside a vault. The wire-level memory
/// format (YAML frontmatter under <c>---</c> fences) is identical across flavors;
/// only file naming/placement differs. Selected via <c>KOSHI_VAULT_FLAVOR</c>.
/// </summary>
/// <remarks>
/// All adapters obey the same identity rule: <see cref="MemoryRecord.Id"/> is
/// embedded as <c>--&lt;id&gt;</c> in the filename and is the source of truth.
/// Adapters that flatten the layout (Logseq, Dendron) use prefix/extension
/// patterns to keep Koshi-owned files distinguishable from user notes.
/// </remarks>
internal interface IVaultLayoutAdapter
{
    /// <summary>Stable identifier for diagnostics. e.g. "obsidian", "logseq", "dendron", "foam".</summary>
    string FlavorName { get; }

    /// <summary>
    /// Create any subdirectories the layout requires under <paramref name="vaultRoot"/>.
    /// Called from the <see cref="VaultBackend"/> ctor and before every Upsert.
    /// </summary>
    void EnsureDirs(string vaultRoot);

    /// <summary>
    /// Target full path for writing <paramref name="record"/>. Implementations must
    /// produce a stable path for a given (subject, type, id) triple; subject rename
    /// produces a different path (caller moves/deletes the old one).
    /// </summary>
    string TargetPath(string vaultRoot, MemoryRecord record);

    /// <summary>
    /// Enumerates every <c>.md</c> file under <paramref name="vaultRoot"/> that
    /// belongs to this layout. Files whose frontmatter parses to a <see cref="MemoryRecord"/>
    /// are "owned" by Koshi; others matching the layout's pattern are reported as
    /// unmanaged notes.
    /// </summary>
    IEnumerable<string> EnumerateOwnedFiles(string vaultRoot);

    /// <summary>
    /// Where the <see cref="FileSystemWatcher"/> should listen, what filter to use,
    /// and whether to recurse. Returns null when no directory is watchable yet
    /// (caller should treat as fallback / log + skip watcher).
    /// </summary>
    (string watchDir, string filter, bool recursive)? WatcherSpec(string vaultRoot);
}

/// <summary>
/// Static helpers for resolving the active vault flavor from environment.
/// </summary>
internal static class VaultLayout
{
    public const string EnvVar = "KOSHI_VAULT_FLAVOR";

    /// <summary>Selects an adapter based on the <see cref="EnvVar"/> env var. Default: obsidian.</summary>
    public static IVaultLayoutAdapter ResolveFromEnv() =>
        Resolve(Environment.GetEnvironmentVariable(EnvVar));

    public static IVaultLayoutAdapter Resolve(string? flavor)
    {
        var normalized = (flavor ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "obsidian" => new ObsidianLayout(),
            "foam" => new FoamLayout(),
            "logseq" => new LogseqLayout(),
            "dendron" => new DendronLayout(),
            _ => UnknownFallback(flavor),
        };
    }

    private static IVaultLayoutAdapter UnknownFallback(string? flavor)
    {
        Console.Error.WriteLine(
            $"[koshi] Unknown {EnvVar}='{flavor}'. Valid: obsidian, foam, logseq, dendron. " +
            "Falling back to obsidian.");
        return new ObsidianLayout();
    }
}

/// <summary>
/// Default layout. <c>&lt;vault&gt;/koshi/{facts,decisions,patterns,preferences}/&lt;slug&gt;--&lt;id&gt;.md</c>.
/// Backwards-compatible with v0.6.0 / v0.6.1 vaults.
/// </summary>
internal class ObsidianLayout : IVaultLayoutAdapter
{
    public virtual string FlavorName => "obsidian";

    public string KoshiDir(string vaultRoot) => Path.Join(vaultRoot, "koshi");

    public void EnsureDirs(string vaultRoot)
    {
        var dir = KoshiDir(vaultRoot);
        Directory.CreateDirectory(dir);
        foreach (MemoryType t in Enum.GetValues<MemoryType>())
            Directory.CreateDirectory(Path.Join(dir, TypeSubdir(t)));
    }

    public string TargetPath(string vaultRoot, MemoryRecord record) => Path.Join(
        KoshiDir(vaultRoot),
        TypeSubdir(record.Type),
        $"{Slug.Make(record.Subject)}--{record.Id}.md");

    public IEnumerable<string> EnumerateOwnedFiles(string vaultRoot)
    {
        var dir = KoshiDir(vaultRoot);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories);
    }

    public (string watchDir, string filter, bool recursive)? WatcherSpec(string vaultRoot) =>
        (KoshiDir(vaultRoot), "*.md", true);

    internal static string TypeSubdir(MemoryType type) => type switch
    {
        MemoryType.Fact => "facts",
        MemoryType.Decision => "decisions",
        MemoryType.Pattern => "patterns",
        MemoryType.Preference => "preferences",
        _ => "facts",
    };
}

/// <summary>
/// Foam: identical to Obsidian (Foam is built on Obsidian-compatible markdown).
/// Separate class for clarity in diagnostics + tests.
/// </summary>
internal sealed class FoamLayout : ObsidianLayout
{
    public override string FlavorName => "foam";
}

/// <summary>
/// Logseq layout. Logseq prefers a flat <c>pages/</c> directory; all Koshi files
/// live there with the prefix <c>koshi-&lt;type&gt;-&lt;slug&gt;--&lt;id&gt;.md</c> so
/// they're filterable from user pages.
/// </summary>
internal sealed class LogseqLayout : IVaultLayoutAdapter
{
    public string FlavorName => "logseq";

    public string PagesDir(string vaultRoot) => Path.Join(vaultRoot, "pages");

    public void EnsureDirs(string vaultRoot) => Directory.CreateDirectory(PagesDir(vaultRoot));

    public string TargetPath(string vaultRoot, MemoryRecord record) => Path.Join(
        PagesDir(vaultRoot),
        $"koshi-{TypeStem(record.Type)}-{Slug.Make(record.Subject)}--{record.Id}.md");

    public IEnumerable<string> EnumerateOwnedFiles(string vaultRoot)
    {
        var dir = PagesDir(vaultRoot);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "koshi-*.md", SearchOption.TopDirectoryOnly);
    }

    public (string watchDir, string filter, bool recursive)? WatcherSpec(string vaultRoot) =>
        (PagesDir(vaultRoot), "koshi-*.md", false);

    internal static string TypeStem(MemoryType type) => type switch
    {
        MemoryType.Fact => "fact",
        MemoryType.Decision => "decision",
        MemoryType.Pattern => "pattern",
        MemoryType.Preference => "preference",
        _ => "fact",
    };
}

/// <summary>
/// Dendron layout. Dot-namespaced filenames at the vault root, matching Dendron's
/// idiomatic <c>&lt;hierarchy&gt;.&lt;leaf&gt;.md</c> scheme: <c>koshi.&lt;type&gt;.&lt;slug&gt;--&lt;id&gt;.md</c>.
/// </summary>
internal sealed class DendronLayout : IVaultLayoutAdapter
{
    public string FlavorName => "dendron";

    public void EnsureDirs(string vaultRoot) => Directory.CreateDirectory(vaultRoot);

    public string TargetPath(string vaultRoot, MemoryRecord record) => Path.Join(
        vaultRoot,
        $"koshi.{TypeStem(record.Type)}.{Slug.Make(record.Subject)}--{record.Id}.md");

    public IEnumerable<string> EnumerateOwnedFiles(string vaultRoot)
    {
        if (!Directory.Exists(vaultRoot)) return [];
        return Directory.EnumerateFiles(vaultRoot, "koshi.*.md", SearchOption.TopDirectoryOnly);
    }

    public (string watchDir, string filter, bool recursive)? WatcherSpec(string vaultRoot) =>
        (vaultRoot, "koshi.*.md", false);

    internal static string TypeStem(MemoryType type) => type switch
    {
        MemoryType.Fact => "fact",
        MemoryType.Decision => "decision",
        MemoryType.Pattern => "pattern",
        MemoryType.Preference => "preference",
        _ => "fact",
    };
}
