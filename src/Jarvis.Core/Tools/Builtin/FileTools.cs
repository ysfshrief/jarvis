using System.Text;

namespace Jarvis.Core.Tools.Builtin;

public sealed class FileSearchTool(FilePolicy policy) : ToolBase
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "AppData", "$Recycle.Bin", ".cache", ".nuget", ".vs", "__pycache__", ".venv", "venv", "Library",
    };

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_search",
        Category = "files",
        Description = "Find files or folders by name (partial, case-insensitive) under the user's folders. Newest first.",
        Parameters =
        [
            new("query", "string", "Part of the file name, e.g. 'citycrep proposal' or '.pptx'.", true),
            new("root", "string", "Folder to search in (default: the user's allowed folders)."),
            new("max_results", "integer", "Maximum results (default 20)."),
        ],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var summary = $"Search files for \"{args.GetString("query")}\"";
        return args.GetString("root") is { } root
            ? policy.ReadRisk(policy.Resolve(root), ctx.Settings, summary + $" in {root}")
            : new(RiskLevel.Safe, summary);
    }

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var query = args.RequireString("query");
        var terms = Language.TextNormalizer.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var roots = args.GetString("root") is { } r ? [policy.Resolve(r)] : policy.AllowedRoots(ctx.Settings);
        var max = Math.Clamp(args.GetInt("max_results") ?? 20, 1, 200);

        var results = await Task.Run(() =>
        {
            var found = new List<FileSystemInfo>();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            foreach (var root in roots.Where(Directory.Exists))
                Walk(new DirectoryInfo(root), terms, found, deadline, 0, ctx.CancellationToken);
            return found.OrderByDescending(f => f.LastWriteTimeUtc).Take(max).ToList();
        }, ctx.CancellationToken).ConfigureAwait(false);

        if (results.Count == 0)
            return ToolResult.Ok(ctx.T($"I couldn't find any files matching \"{query}\".", $"ملقتش ملفات بالاسم \"{query}\"."), Array.Empty<object>());

        var data = results.Select(f => new
        {
            path = f.FullName,
            name = f.Name,
            isFolder = f is DirectoryInfo,
            modified = f.LastWriteTime,
            sizeBytes = f is FileInfo fi ? fi.Length : (long?)null,
        }).ToList();
        var top = string.Join("\n", data.Take(5).Select(d => $"• {d.name} — {Path.GetDirectoryName(d.path)}"));
        return ToolResult.Ok(ctx.T($"Found {results.Count} match{(results.Count == 1 ? "" : "es")}, newest first:\n{top}",
                                   $"لقيت {results.Count}، الأحدث الأول:\n{top}"), data);
    }

    private static void Walk(DirectoryInfo dir, string[] terms, List<FileSystemInfo> found, DateTime deadline, int depth, CancellationToken ct)
    {
        if (depth > 12 || found.Count >= 2000 || DateTime.UtcNow > deadline || ct.IsCancellationRequested) return;
        IEnumerable<FileSystemInfo> entries;
        try { entries = dir.EnumerateFileSystemInfos(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return; }

        foreach (var e in entries)
        {
            try
            {
                if (e.Attributes.HasFlag(FileAttributes.Hidden) || e.Attributes.HasFlag(FileAttributes.System)) continue;
                var name = Language.TextNormalizer.Normalize(e.Name);
                if (terms.All(t => name.Contains(t))) found.Add(e);
                if (e is DirectoryInfo sub && !SkipDirs.Contains(sub.Name) && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Walk(sub, terms, found, deadline, depth + 1, ct);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
    }
}

public sealed class FileListTool(FilePolicy policy) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_list",
        Category = "files",
        Description = "List the contents of a folder. Accepts paths and names like 'downloads' or 'desktop'.",
        Parameters = [new("path", "string", "Folder path.", true)],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var p = policy.Resolve(args.RequireString("path"));
        return policy.ReadRisk(p, ctx.Settings, $"List folder {p}");
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var p = policy.Resolve(args.RequireString("path"));
        if (!Directory.Exists(p))
            return Task.FromResult(ToolResult.Fail(ctx.T($"The folder {p} doesn't exist.", $"الفولدر {p} مش موجود."), status: ToolStatus.NotFound));
        var dir = new DirectoryInfo(p);
        var items = dir.EnumerateFileSystemInfos()
            .Where(e => !e.Attributes.HasFlag(FileAttributes.Hidden))
            .OrderByDescending(e => e is DirectoryInfo).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .Select(e => new { name = e.Name, isFolder = e is DirectoryInfo, modified = e.LastWriteTime, sizeBytes = e is FileInfo f ? f.Length : (long?)null })
            .ToList();
        return Task.FromResult(ToolResult.Ok(ctx.T($"{p} contains {items.Count} item(s).", $"الفولدر {p} فيه {items.Count} حاجة."), new { path = p, items }));
    }
}

public sealed class FileReadTool(FilePolicy policy) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_read",
        Category = "files",
        Description = "Read a text file (source code, logs, notes, config). Returns the content (truncated if very large).",
        Parameters =
        [
            new("path", "string", "File path.", true),
            new("max_chars", "integer", "Maximum characters to return (default from settings)."),
        ],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var p = policy.Resolve(args.RequireString("path"));
        return policy.ReadRisk(p, ctx.Settings, $"Read file {p}");
    }

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var p = policy.Resolve(args.RequireString("path"));
        if (!File.Exists(p))
            return ToolResult.Fail(ctx.T($"The file {p} doesn't exist.", $"الملف {p} مش موجود."), status: ToolStatus.NotFound);

        var max = Math.Clamp(args.GetInt("max_chars") ?? ctx.Settings.Files.MaxReadBytes, 1000, 2_000_000);
        var info = new FileInfo(p);
        if (LooksBinary(p))
            return ToolResult.Fail(ctx.T($"{info.Name} is a binary file; I can only read text files with this tool.", $"{info.Name} ملف مش نصي، الأداة دي بتقرا ملفات نصية بس."));

        string content;
        await using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            var buffer = new char[max + 1];
            var read = await reader.ReadBlockAsync(buffer, ctx.CancellationToken).ConfigureAwait(false);
            content = new string(buffer, 0, Math.Min(read, max));
            if (read > max) content += "\n…(truncated)";
        }
        return ToolResult.Ok(ctx.T($"Read {info.Name} ({info.Length:N0} bytes).", $"قريت {info.Name} ({info.Length:N0} بايت)."),
            new { path = p, sizeBytes = info.Length, modified = info.LastWriteTime, content });
    }

    private static bool LooksBinary(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".dll" or ".zip" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".pdf" or ".docx" or ".xlsx" or ".pptx" or ".mp3" or ".mp4" or ".7z" or ".rar" or ".iso" or ".msi")
            return true;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[4096];
        var n = fs.Read(buf, 0, buf.Length);
        // UTF-16 files legitimately contain zero bytes; treat a BOM as text.
        if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return false;
        return buf.AsSpan(0, n).Contains((byte)0);
    }
}

public sealed class FileWriteTool(FilePolicy policy) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_write",
        Category = "files",
        Risk = RiskLevel.Sensitive,
        Description = "Create or overwrite a text file, or append to it. Requires approval.",
        Parameters =
        [
            new("path", "string", "File path.", true),
            new("content", "string", "Text to write.", true),
            new("append", "boolean", "Append instead of overwrite."),
        ],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var p = policy.Resolve(args.RequireString("path"));
        var verb = args.GetBool("append") == true ? "Append to" : File.Exists(p) ? "Overwrite" : "Create";
        return policy.WriteRisk(p, ctx.Settings, $"{verb} file {p}");
    }

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var p = policy.Resolve(args.RequireString("path"));
        var content = args.GetString("content") ?? "";
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        if (args.GetBool("append") == true) await File.AppendAllTextAsync(p, content, ctx.CancellationToken).ConfigureAwait(false);
        else await File.WriteAllTextAsync(p, content, ctx.CancellationToken).ConfigureAwait(false);
        var size = new FileInfo(p).Length;
        return ToolResult.Ok(ctx.T($"Saved {Path.GetFileName(p)}.", $"اتحفظ {Path.GetFileName(p)}."), new { path = p, sizeBytes = size });
    }
}

public sealed class FileMoveTool(FilePolicy policy) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_move",
        Category = "files",
        Risk = RiskLevel.Sensitive,
        Description = "Move, rename or copy a file or folder. Requires approval.",
        Parameters =
        [
            new("source", "string", "Existing path.", true),
            new("destination", "string", "New path.", true),
            new("copy", "boolean", "Copy instead of move."),
        ],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var src = policy.Resolve(args.RequireString("source"));
        var dst = policy.Resolve(args.RequireString("destination"));
        var verb = args.GetBool("copy") == true ? "Copy" : "Move";
        var a = policy.WriteRisk(dst, ctx.Settings, $"{verb} {src} → {dst}");
        var b = args.GetBool("copy") == true ? policy.ReadRisk(src, ctx.Settings, a.Summary) : policy.WriteRisk(src, ctx.Settings, a.Summary);
        return a.Level >= b.Level ? a : b;
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var src = policy.Resolve(args.RequireString("source"));
        var dst = policy.Resolve(args.RequireString("destination"));
        var copy = args.GetBool("copy") == true;
        if (!File.Exists(src) && !Directory.Exists(src))
            return Task.FromResult(ToolResult.Fail(ctx.T($"{src} doesn't exist.", $"{src} مش موجود."), status: ToolStatus.NotFound));
        if (File.Exists(dst) || Directory.Exists(dst))
            return Task.FromResult(ToolResult.Fail(ctx.T($"{dst} already exists; I won't overwrite it.", $"{dst} موجود بالفعل، مش هكتب فوقه.")));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(src))
        {
            if (copy) File.Copy(src, dst); else File.Move(src, dst);
        }
        else
        {
            if (copy) CopyDirectory(src, dst); else Directory.Move(src, dst);
        }
        var ok = File.Exists(dst) || Directory.Exists(dst);
        return Task.FromResult(ok
            ? ToolResult.Ok(ctx.T(copy ? "Copied." : "Moved.", copy ? "اتنسخ." : "اتنقل."), new { source = src, destination = dst })
            : ToolResult.Fail(ctx.T("The operation didn't produce the destination file.", "العملية مخلصتش صح.")));
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}

public sealed class FileDeleteTool(FilePolicy policy, IFileTrash trash) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_delete",
        Category = "files",
        Risk = RiskLevel.Critical,
        Description = "Delete a file or folder by moving it to the Recycle Bin. Always requires approval.",
        Parameters = [new("path", "string", "Path to delete.", true)],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var p = policy.Resolve(args.RequireString("path"));
        return new(RiskLevel.Critical, $"Delete {p} (to {trash.Name})", policy.ProtectedReason(p) ?? "Deleting data always needs your approval.");
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var p = policy.Resolve(args.RequireString("path"));
        if (!File.Exists(p) && !Directory.Exists(p))
            return Task.FromResult(ToolResult.Fail(ctx.T($"{p} doesn't exist.", $"{p} مش موجود."), status: ToolStatus.NotFound));
        trash.Trash(p);
        var gone = !File.Exists(p) && !Directory.Exists(p);
        return Task.FromResult(gone
            ? ToolResult.Ok(ctx.T($"Moved {Path.GetFileName(p)} to the {trash.Name}.", $"نقلت {Path.GetFileName(p)} لـ{trash.Name}."), new { path = p })
            : ToolResult.Fail(ctx.T("The file is still there; deletion didn't complete.", "الملف لسه موجود، المسح مكملش.")));
    }
}
