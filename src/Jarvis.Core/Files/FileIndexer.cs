using System.Collections.Concurrent;
using Jarvis.Core.Events;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools.Builtin;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Files;

public sealed record IndexProgress(bool Running, string? Root, int Scanned, int Indexed, int Skipped, int Errors, string? Current, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

/// <summary>
/// Keeps the file index up to date: a gentle full scan of the chosen folders (one file at a time, with
/// pauses so the PC stays responsive), then live updates from file-system notifications. Runs only when
/// the user enabled file knowledge in Settings.
/// </summary>
public sealed class FileIndexer(FileIndex index, DocumentExtractor extractor, FilePolicy policy, ISettingsStore settings, IEventBus events, ILogger<FileIndexer> logger) : IDisposable
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "AppData", "$Recycle.Bin", ".cache", ".nuget", ".vs", ".idea", "__pycache__", ".venv", "venv",
        "dist", "build", "target", ".gradle", "packages", "Library", "System Volume Information",
    };

    private readonly ConcurrentDictionary<string, DateTimeOffset> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string?> _projectCache = new(StringComparer.OrdinalIgnoreCase);
    private IndexProgress _progress = new(false, null, 0, 0, 0, 0, null, null, null);

    public IndexProgress Progress => _progress;

    /// <summary>Folders that will be indexed (Documents, Desktop and Downloads unless the user chose others).</summary>
    public IReadOnlyList<string> Roots()
    {
        var s = settings.Current.Files;
        IEnumerable<string> roots = s.IndexRoots.Count > 0
            ? s.IndexRoots.Select(policy.Resolve)
            : new[] { FilePolicy.KnownFolder("documents"), FilePolicy.KnownFolder("desktop"), FilePolicy.KnownFolder("downloads") }.OfType<string>();
        // Never index protected places (credential stores, system folders, JARVIS's own data).
        return roots.Where(Directory.Exists).Where(r => policy.ProtectedReason(r) is null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Indexes (or refreshes) one file. Returns false for skipped/unsupported paths.</summary>
    public async Task<bool> IndexFileAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { index.Remove(path); return false; }
        if (!FileKinds.Indexable(path) || policy.ProtectedReason(info.FullName) is not null || IsHiddenOrTemp(info)) return false;
        if (index.IsCurrent(info.FullName, info.Length, info.LastWriteTime)) return false;
        var doc = await extractor.ExtractAsync(info.FullName, (long)settings.Current.Files.IndexMaxFileMb * 1048576, ct).ConfigureAwait(false);
        index.Upsert(info, doc, ProjectOf(info.DirectoryName));
        return true;
    }

    /// <summary>Full scan of all roots. Safe to call repeatedly; unchanged files are skipped quickly.</summary>
    public async Task ScanAsync(CancellationToken ct)
    {
        if (!await _scanLock.WaitAsync(0, ct).ConfigureAwait(false)) return; // already scanning
        try
        {
            var started = DateTimeOffset.Now;
            int scanned = 0, indexed = 0, skipped = 0, errors = 0;
            foreach (var root in Roots())
            {
                foreach (var file in Enumerate(root, ct))
                {
                    if (!settings.Current.Files.IndexEnabled) return;
                    scanned++;
                    try
                    {
                        if (await IndexFileAsync(file, ct).ConfigureAwait(false))
                        {
                            indexed++;
                            await Task.Delay(15, ct).ConfigureAwait(false); // stay gentle on the CPU and disk
                        }
                        else skipped++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errors++;
                        logger.LogDebug(ex, "Couldn't index {File}", file);
                    }
                    if (scanned % 25 == 0)
                    {
                        _progress = new IndexProgress(true, root, scanned, indexed, skipped, errors, file, started, null);
                        events.Publish(EventTypes.FilesIndexChanged, _progress);
                    }
                }
                index.Prune(root);
            }
            _progress = new IndexProgress(false, null, scanned, indexed, skipped, errors, null, started, DateTimeOffset.Now);
            events.Publish(EventTypes.FilesIndexChanged, _progress);
            logger.LogInformation("File index scan: {Scanned} scanned, {Indexed} (re)indexed, {Errors} errors", scanned, indexed, errors);
        }
        finally { _scanLock.Release(); }
    }

    /// <summary>Starts watching the roots for changes (debounced). Re-call after settings change.</summary>
    public void Watch()
    {
        StopWatching();
        if (!settings.Current.Files.IndexEnabled) return;
        foreach (var root in Roots())
        {
            try
            {
                var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                w.Changed += (_, e) => Queue(e.FullPath);
                w.Created += (_, e) => Queue(e.FullPath);
                w.Deleted += (_, e) => Queue(e.FullPath);
                w.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
            {
                logger.LogWarning(ex, "Can't watch {Root}", root);
            }
        }
    }

    /// <summary>Processes changes that have been quiet for two seconds (files being saved fire many events).</summary>
    public async Task<int> ProcessChangesAsync(CancellationToken ct)
    {
        var ready = _pending.Where(kv => DateTimeOffset.Now - kv.Value > TimeSpan.FromSeconds(2)).Select(kv => kv.Key).ToList();
        var n = 0;
        foreach (var path in ready)
        {
            _pending.TryRemove(path, out _);
            try
            {
                if (File.Exists(path)) { if (await IndexFileAsync(path, ct).ConfigureAwait(false)) n++; }
                else if (index.Remove(path)) n++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _pending[path] = DateTimeOffset.Now; } // still being written
        }
        if (n > 0) events.Publish(EventTypes.FilesIndexChanged, new { action = "updated", count = n });
        return n;
    }

    private void Queue(string path)
    {
        if (FileKinds.Indexable(path) && !path.Split(Path.DirectorySeparatorChar).Any(SkipDirs.Contains)) _pending[path] = DateTimeOffset.Now;
    }

    private IEnumerable<string> Enumerate(string root, CancellationToken ct)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var (dir, depth) = stack.Pop();
            string[] files = [], dirs = [];
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
            foreach (var f in files.Where(FileKinds.Indexable)) yield return f;
            if (depth >= 16) continue;
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (SkipDirs.Contains(name) || name.StartsWith('.')) continue;
                try
                {
                    var attr = File.GetAttributes(d);
                    if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System) || attr.HasFlag(FileAttributes.ReparsePoint)) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                stack.Push((d, depth + 1));
            }
        }
    }

    private static bool IsHiddenOrTemp(FileInfo f) =>
        f.Name.StartsWith("~$") || f.Name.StartsWith('.') || f.Extension is ".tmp" or ".crdownload" or ".part" ||
        f.Attributes.HasFlag(FileAttributes.Hidden) || f.Attributes.HasFlag(FileAttributes.System);

    /// <summary>The code/work project a file belongs to: nearest folder with a project marker.</summary>
    private string? ProjectOf(string? dir)
    {
        if (dir is null) return null;
        return _projectCache.GetOrAdd(dir, d =>
        {
            var cur = new DirectoryInfo(d);
            for (var i = 0; i < 8 && cur is not null; i++, cur = cur.Parent)
            {
                try
                {
                    if (Directory.Exists(Path.Combine(cur.FullName, ".git")) ||
                        cur.EnumerateFiles().Any(f => f.Name is "package.json" or "pyproject.toml" or "Cargo.toml" or "go.mod" || f.Extension is ".sln" or ".slnx" or ".csproj"))
                        return cur.Name;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            }
            return null;
        });
    }

    private void StopWatching()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    public void Dispose() => StopWatching();
}
