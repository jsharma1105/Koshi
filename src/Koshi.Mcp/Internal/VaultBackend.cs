using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Vault-mode memory backend — one Markdown file per memory under
/// <c>&lt;vault&gt;/koshi/{facts,decisions,patterns,preferences}/</c>.
/// Activated by setting <c>KOSHI_MEMORY_VAULT</c>.
/// </summary>
/// <remarks>
/// Identity is <see cref="MemoryRecord.Id"/>; the filename slug is cosmetic. On
/// subject rename, the file is atomically moved to a new path with the same id.
/// <para>
/// <see cref="LoadAll"/> rescans the directory tree every call; <see cref="MemoryStore"/>
/// invokes it on every tool-call entry so external edits / git pulls / Obsidian
/// changes are picked up without restarting the MCP server.
/// </para>
/// </remarks>
internal sealed class VaultBackend : IMemoryBackend
{
    public string Root { get; }
    public string KoshiDir { get; }

    public bool IsEnabled => true;
    public string? Location => Root;
    public string BackendKind => "vault";
    public bool RequiresReloadPerCall => true;

    private int _unmanagedNoteCount;
    private List<string> _unmanagedNotePaths = [];
    private int _duplicateIdWarningCount;
    private Dictionary<string, string> _idToPath = new(StringComparer.Ordinal);

    public int UnmanagedNoteCount => _unmanagedNoteCount;
    public IReadOnlyList<string> UnmanagedNotePaths => _unmanagedNotePaths;
    public int DuplicateIdWarningCount => _duplicateIdWarningCount;

    public VaultBackend(string vaultRoot)
    {
        Root = Path.GetFullPath(vaultRoot);
        KoshiDir = Path.Combine(Root, "koshi");
        EnsureKoshiDirs();
    }

    private void EnsureKoshiDirs()
    {
        Directory.CreateDirectory(KoshiDir);
        foreach (MemoryType t in Enum.GetValues<MemoryType>())
            Directory.CreateDirectory(Path.Combine(KoshiDir, TypeSubdir(t)));
    }

    public List<MemoryRecord> LoadAll()
    {
        var unmanagedAccum = new List<string>();
        int dupAccum = 0;
        var idToPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var idToMtime = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var idToRecord = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);

        if (Directory.Exists(KoshiDir))
        {
            foreach (var path in Directory.EnumerateFiles(KoshiDir, "*.md", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(path) is not ".md") continue;
                if (path.EndsWith(".tmp", StringComparison.Ordinal)) continue;

                var parsed = VaultDocument.Read(path);
                if (parsed is null)
                {
                    unmanagedAccum.Add(path);
                    continue;
                }
                var mtime = File.GetLastWriteTimeUtc(path);
                var id = parsed.Record.Id;

                if (idToMtime.TryGetValue(id, out var existingMtime))
                {
                    dupAccum++;
                    var existingPath = idToPath[id];
                    Console.Error.WriteLine(
                        $"[koshi] Duplicate memory id '{id}' in vault. Newest wins:\n" +
                        $"  {existingPath} (mtime: {existingMtime:u})\n" +
                        $"  {path} (mtime: {mtime:u})");
                    if (mtime > existingMtime)
                    {
                        idToMtime[id] = mtime;
                        idToPath[id] = path;
                        idToRecord[id] = parsed.Record;
                    }
                }
                else
                {
                    idToMtime[id] = mtime;
                    idToPath[id] = path;
                    idToRecord[id] = parsed.Record;
                }
            }
        }

        _unmanagedNotePaths = unmanagedAccum;
        _unmanagedNoteCount = unmanagedAccum.Count;
        _duplicateIdWarningCount = dupAccum;
        _idToPath = idToPath;
        return [.. idToRecord.Values];
    }

    public void Upsert(MemoryRecord record, IReadOnlyList<MemoryRecord> snapshot)
    {
        EnsureKoshiDirs();

        var existingPath = LocateById(record.Id);
        string otherFrontmatter = "";
        if (existingPath is not null && File.Exists(existingPath))
        {
            var parsed = VaultDocument.Read(existingPath);
            if (parsed is not null) otherFrontmatter = parsed.OtherFrontmatterText;
        }

        var targetPath = Path.Combine(
            KoshiDir,
            TypeSubdir(record.Type),
            $"{Slug.Make(record.Subject)}--{record.Id}.md");

        try
        {
            VaultDocument.Write(targetPath, record, otherFrontmatter);
            _idToPath[record.Id] = targetPath;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[koshi] Failed to write '{targetPath}': {ex.Message}");
            return;
        }

        if (existingPath is not null
            && !PathsEqual(existingPath, targetPath))
        {
            try { File.Delete(existingPath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[koshi] Could not delete old memory file '{existingPath}': {ex.Message}");
            }
        }
    }

    public void Delete(string id, IReadOnlyList<MemoryRecord> snapshot)
    {
        var path = LocateById(id);
        if (path is null) return;
        try
        {
            File.Delete(path);
            _idToPath.Remove(id);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[koshi] Could not delete memory file '{path}': {ex.Message}");
        }
    }

    public void ReplaceAll(IReadOnlyList<MemoryRecord> records)
    {
        EnsureKoshiDirs();
        // Delete only koshi-id-tagged files; leave unmanaged user notes alone.
        if (Directory.Exists(KoshiDir))
        {
            foreach (var path in Directory.EnumerateFiles(KoshiDir, "*.md", SearchOption.AllDirectories).ToList())
            {
                if (path.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                var parsed = VaultDocument.Read(path);
                if (parsed is not null)
                {
                    try { File.Delete(path); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[koshi] Could not delete '{path}' during ReplaceAll: {ex.Message}");
                    }
                }
            }
        }
        _idToPath.Clear();
        foreach (var r in records) Upsert(r, records);
    }

    private string? LocateById(string id)
    {
        if (_idToPath.TryGetValue(id, out var cached) && File.Exists(cached))
            return cached;

        // Cache miss (or stale) — scan the tree once.
        if (!Directory.Exists(KoshiDir)) return null;
        foreach (var path in Directory.EnumerateFiles(KoshiDir, "*.md", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            var parsed = VaultDocument.Read(path);
            if (parsed is not null && parsed.Record.Id == id)
            {
                _idToPath[id] = path;
                return path;
            }
        }
        return null;
    }

    private static bool PathsEqual(string a, string b)
    {
        var comp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comp);
    }

    internal static string TypeSubdir(MemoryType type) => type switch
    {
        MemoryType.Fact => "facts",
        MemoryType.Decision => "decisions",
        MemoryType.Pattern => "patterns",
        MemoryType.Preference => "preferences",
        _ => "facts",
    };
}
