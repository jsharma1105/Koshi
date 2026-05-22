using System.Threading;
using Koshi.Core.Memory;

namespace Koshi.Mcp.Internal;

/// <summary>
/// Vault-mode memory backend — one Markdown file per memory under a vault directory.
/// File layout (subdirs, filename scheme) is delegated to a flavor-specific
/// <see cref="IVaultLayoutAdapter"/>; the wire format (YAML frontmatter under
/// <c>---</c> fences) is identical across flavors.
/// Activated by setting <c>KOSHI_MEMORY_VAULT</c>; layout selected via
/// <c>KOSHI_VAULT_FLAVOR</c> (default: obsidian).
/// </summary>
/// <remarks>
/// Identity is <see cref="MemoryRecord.Id"/>; the filename slug is cosmetic. On
/// subject rename, the file is atomically moved to a new path with the same id.
/// <para>
/// External edits / git pulls / Obsidian writes are picked up via a
/// <see cref="FileSystemWatcher"/> scoped to the adapter's watch region; when an
/// event is observed <see cref="ShouldReload"/> returns true once on the next call.
/// When the watcher cannot attach (e.g. network mount), the backend falls back to
/// "reload on every call" semantics. Disable via <c>KOSHI_VAULT_WATCH=off|false|0</c>.
/// </para>
/// </remarks>
internal sealed class VaultBackend : IMemoryBackend, IDisposable
{
    public string Root { get; }
    public IVaultLayoutAdapter Layout { get; }

    /// <summary>Legacy alias for the Obsidian "koshi/" subdir. For flavors with a
    /// nested koshi/ tree this is its absolute path; for flat flavors this is the
    /// directory the adapter watches/enumerates. Kept for tests / diagnostics only.</summary>
    public string KoshiDir { get; }

    public bool IsEnabled => true;
    public string? Location => Root;
    public string BackendKind => "vault";

    private readonly FileSystemWatcher? _watcher;
    private readonly bool _watchRequested;
    private readonly bool _watcherAttached;
    private int _dirty;

    /// <summary>Diagnostic string for koshi_health output.</summary>
    public string WatcherStatus =>
        !_watchRequested ? "disabled (KOSHI_VAULT_WATCH=off)"
        : _watcherAttached ? "healthy"
        : "unavailable (fallback: reload-per-call)";

    private int _unmanagedNoteCount;
    private List<string> _unmanagedNotePaths = [];
    private int _duplicateIdWarningCount;
    private Dictionary<string, string> _idToPath = new(StringComparer.Ordinal);

    public int UnmanagedNoteCount => _unmanagedNoteCount;
    public IReadOnlyList<string> UnmanagedNotePaths => _unmanagedNotePaths;
    public int DuplicateIdWarningCount => _duplicateIdWarningCount;

    public VaultBackend(string vaultRoot) : this(vaultRoot, watch: true, layout: null) { }
    public VaultBackend(string vaultRoot, bool watch) : this(vaultRoot, watch, layout: null) { }

    public VaultBackend(string vaultRoot, bool watch, IVaultLayoutAdapter? layout)
    {
        Root = Path.GetFullPath(vaultRoot);
        Layout = layout ?? VaultLayout.ResolveFromEnv();
        Layout.EnsureDirs(Root);

        // Legacy: KoshiDir is the watch root for diagnostic purposes.
        var watchSpec = Layout.WatcherSpec(Root);
        KoshiDir = watchSpec?.watchDir ?? Path.Combine(Root, "koshi");

        _watchRequested = watch && WatcherEnvAllows();
        if (_watchRequested && watchSpec is { } spec)
        {
            try
            {
                _watcher = new FileSystemWatcher(spec.watchDir, spec.filter)
                {
                    IncludeSubdirectories = spec.recursive,
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Size
                                 | NotifyFilters.CreationTime,
                };
                _watcher.Created += OnVaultEvent;
                _watcher.Changed += OnVaultEvent;
                _watcher.Deleted += OnVaultEvent;
                _watcher.Renamed += OnVaultEvent;
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;
                _watcherAttached = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[koshi] Vault watcher could not attach to '{spec.watchDir}' " +
                    $"({ex.GetType().Name}: {ex.Message}); falling back to reload-on-every-call.");
                _watcher?.Dispose();
                _watcher = null;
                _watcherAttached = false;
            }
        }
    }

    private static bool WatcherEnvAllows()
    {
        var v = Environment.GetEnvironmentVariable("KOSHI_VAULT_WATCH");
        if (string.IsNullOrEmpty(v)) return true;
        return v.Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "false" or "no" or "disabled" => false,
            _ => true,
        };
    }

    private void OnVaultEvent(object sender, FileSystemEventArgs e) =>
        Interlocked.Exchange(ref _dirty, 1);

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Console.Error.WriteLine(
            $"[koshi] Vault watcher error: {e.GetException().Message}. Marking cache dirty.");
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>
    /// Returns true once when the watcher has seen activity since the last reload,
    /// or unconditionally when no watcher is attached.
    /// </summary>
    public bool ShouldReload()
    {
        if (!_watcherAttached) return true;
        return Interlocked.CompareExchange(ref _dirty, 0, 1) == 1;
    }

    public void Dispose()
    {
        if (_watcher is null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnVaultEvent;
            _watcher.Changed -= OnVaultEvent;
            _watcher.Deleted -= OnVaultEvent;
            _watcher.Renamed -= OnVaultEvent;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
        }
        catch { /* best effort on shutdown */ }
    }

    public List<MemoryRecord> LoadAll()
    {
        var unmanagedAccum = new List<string>();
        int dupAccum = 0;
        var idToPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var idToMtime = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var idToRecord = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);

        foreach (var path in Layout.EnumerateOwnedFiles(Root))
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

        _unmanagedNotePaths = unmanagedAccum;
        _unmanagedNoteCount = unmanagedAccum.Count;
        _duplicateIdWarningCount = dupAccum;
        _idToPath = idToPath;
        return [.. idToRecord.Values];
    }

    public void Upsert(MemoryRecord record, IReadOnlyList<MemoryRecord> snapshot)
    {
        Layout.EnsureDirs(Root);

        var existingPath = LocateById(record.Id);
        string otherFrontmatter = "";
        if (existingPath is not null && File.Exists(existingPath))
        {
            var parsed = VaultDocument.Read(existingPath);
            if (parsed is not null) otherFrontmatter = parsed.OtherFrontmatterText;
        }

        var targetPath = Layout.TargetPath(Root, record);

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
        Layout.EnsureDirs(Root);
        // Delete only koshi-id-tagged files; leave unmanaged user notes alone.
        foreach (var path in Layout.EnumerateOwnedFiles(Root).ToList())
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
        _idToPath.Clear();
        foreach (var r in records) Upsert(r, records);
    }

    private string? LocateById(string id)
    {
        if (_idToPath.TryGetValue(id, out var cached) && File.Exists(cached))
            return cached;

        // Cache miss (or stale) — scan the layout's owned files once.
        foreach (var path in Layout.EnumerateOwnedFiles(Root))
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

    /// <summary>Legacy compatibility: returns the Obsidian-flavored type subdir.</summary>
    internal static string TypeSubdir(MemoryType type) => ObsidianLayout.TypeSubdir(type);
}

