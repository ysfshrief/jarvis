using Jarvis.Core.Files;
using Jarvis.Core.Memory;

namespace Jarvis.Core.Tools.Builtin;

/// <summary>Shared helpers: turning "the CityCrep proposal" into a file path, and live folder scans.</summary>
public sealed class FileFinder(FileIndex index, FilePolicy policy, EntityStore entities)
{
    /// <summary>A path, an indexed file matching the words, or a name match in the user's folders.</summary>
    public async Task<string?> ResolveAsync(string query, ToolContext ctx)
    {
        var q = query.Trim().Trim('"', '“', '”');
        try
        {
            var full = policy.Resolve(q);
            if (File.Exists(full)) return full;
        }
        catch (ArgumentException) { }
        if (ctx.Settings.Files.IndexEnabled)
        {
            var hits = await index.SearchAsync(q, 1, null, ctx.CancellationToken).ConfigureAwait(false);
            if (hits.Count > 0 && File.Exists(hits[0].File.Path)) return hits[0].File.Path;
        }
        return NameSearch(q, ctx).FirstOrDefault(f => f is FileInfo)?.FullName;
    }

    public List<FileSystemInfo> NameSearch(string query, ToolContext ctx, int max = 20)
    {
        var terms = Language.TextNormalizer.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t is not ("the" or "my" or "file" or "ملف")).ToArray();
        if (terms.Length == 0) return [];
        var found = new List<FileSystemInfo>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        foreach (var root in policy.AllowedRoots(ctx.Settings).Where(Directory.Exists))
            FileSearchTool.Walk(new DirectoryInfo(root), terms, found, deadline, 0, ctx.CancellationToken);
        return found.OrderByDescending(f => f.LastWriteTimeUtc).Take(max).ToList();
    }

    /// <summary>Newest files directly in a folder and two levels below (works without the index).</summary>
    public static List<FileInfo> LatestIn(string folder, string? kind, int count, CancellationToken ct)
    {
        var list = new List<FileInfo>();
        void Walk(DirectoryInfo d, int depth)
        {
            if (ct.IsCancellationRequested || depth > 2) return;
            try
            {
                foreach (var f in d.EnumerateFiles())
                    if (!f.Attributes.HasFlag(FileAttributes.Hidden) && !f.Name.StartsWith("~$") && (kind is null || FileKinds.Of(f.FullName) == kind)) list.Add(f);
                foreach (var sub in d.EnumerateDirectories())
                    if (!sub.Attributes.HasFlag(FileAttributes.Hidden) && !sub.Name.StartsWith('.') && sub.Name is not ("node_modules" or "bin" or "obj")) Walk(sub, depth + 1);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        Walk(new DirectoryInfo(folder), 0);
        return list.OrderByDescending(f => f.LastWriteTime).Take(count).ToList();
    }

    public Entity? EntityNamed(string? name) => string.IsNullOrWhiteSpace(name) ? null : entities.Find(name) ?? entities.Mentioned(name).FirstOrDefault();
}

public sealed class FileFindTool(FileIndex index, FileFinder finder) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_find",
        Category = "files",
        Description = "Find documents by what they contain or are about (contracts, proposals, invoices, notes) — searches file contents, titles and names, by words and meaning. Returns paths with the matching passage.",
        Parameters =
        [
            new("query", "string", "What the document is about, e.g. 'CityCrep contract renewal terms'.", true),
            new("kind", "string", "Limit to a kind of file.", false, FileKinds.All),
            new("max_results", "integer", "Maximum results (default 8)."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Find documents: {args.GetString("query")}";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var query = args.RequireString("query");
        var max = Math.Clamp(args.GetInt("max_results") ?? 8, 1, 50);
        var kind = args.GetString("kind") is { } k && FileKinds.All.Contains(k) ? k : null;
        if (!ctx.Settings.Files.IndexEnabled)
        {
            var byName = finder.NameSearch(query, ctx, max).OfType<FileInfo>().Where(f => kind is null || FileKinds.Of(f.FullName) == kind).ToList();
            var note = ctx.T("Searching inside documents is off (Settings → Files → file knowledge), so I matched names only.",
                             "البحث جوه الملفات مقفول (الإعدادات ← الملفات)، فدورت بالأسامي بس.");
            if (byName.Count == 0) return ToolResult.Ok(ctx.T($"Nothing named like “{query}”. ", $"مفيش ملف اسمه زي «{query}». ") + note, Array.Empty<object>());
            var lines = string.Join("\n", byName.Take(5).Select(f => $"• {f.Name} — {f.DirectoryName}"));
            return ToolResult.Ok($"{note}\n{lines}", byName.Select(f => new { path = f.FullName, name = f.Name, modified = f.LastWriteTime }));
        }

        var hits = await index.SearchAsync(query, max, kind, ctx.CancellationToken).ConfigureAwait(false);
        if (hits.Count == 0)
            return ToolResult.Ok(ctx.T($"I couldn't find documents about “{query}” in the indexed folders.", $"ملقتش ملفات عن «{query}» في الفولدرات المفهرسة."), Array.Empty<object>());
        var text = string.Join("\n", hits.Take(5).Select(h => $"• {h.File.Name} ({h.File.ModifiedAt:d MMM yyyy}){(h.Snippet is { } s ? $" — “{s}”" : "")}"));
        return ToolResult.Ok(ctx.T($"Found {hits.Count}:\n{text}", $"لقيت {hits.Count}:\n{text}"),
            hits.Select(h => new { path = h.File.Path, name = h.File.Name, kind = h.File.Kind, modified = h.File.ModifiedAt, snippet = h.Snippet, semantic = h.Semantic }));
    }
}

public sealed class FileLatestTool(FileIndex index, FileFinder finder, FilePolicy policy) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_latest",
        Category = "files",
        Description = "The most recently changed files — overall, in a folder (e.g. Downloads), of a kind (pdf, spreadsheet, presentation…) or about a client/project.",
        Parameters =
        [
            new("folder", "string", "Folder name or path, e.g. downloads, desktop, documents."),
            new("kind", "string", "Kind of file.", false, FileKinds.All),
            new("about", "string", "A client, project or person the files are about."),
            new("count", "integer", "How many (default 5)."),
        ],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        if (args.GetString("folder") is { } folder)
        {
            var path = FilePolicy.KnownFolder(folder) ?? policy.Resolve(folder);
            return policy.ReadRisk(path, ctx.Settings, $"List latest files in {path}");
        }
        return new(RiskLevel.Safe, "List latest files");
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var count = Math.Clamp(args.GetInt("count") ?? 5, 1, 50);
        var kind = args.GetString("kind") is { } k && FileKinds.All.Contains(k) ? k : null;
        var about = finder.EntityNamed(args.GetString("about"));
        List<(string Path, string Name, DateTimeOffset Modified)> files;
        if (args.GetString("folder") is { } folder)
        {
            var path = FilePolicy.KnownFolder(folder) ?? policy.Resolve(folder);
            if (!Directory.Exists(path)) return Task.FromResult(ToolResult.Fail(ctx.T($"The folder {path} doesn't exist.", $"الفولدر {path} مش موجود."), status: ToolStatus.NotFound));
            files = FileFinder.LatestIn(path, kind, count, ctx.CancellationToken).Select(f => (f.FullName, f.Name, (DateTimeOffset)f.LastWriteTime)).ToList();
        }
        else if (ctx.Settings.Files.IndexEnabled)
        {
            files = index.Latest(count, kind, entityId: about?.Id).Select(f => (f.Path, f.Name, f.ModifiedAt)).ToList();
        }
        else
        {
            files = new[] { "downloads", "desktop", "documents" }.Select(FilePolicy.KnownFolder).OfType<string>().Where(Directory.Exists)
                .SelectMany(d => FileFinder.LatestIn(d, kind, count, ctx.CancellationToken))
                .OrderByDescending(f => f.LastWriteTime).Take(count).Select(f => (f.FullName, f.Name, (DateTimeOffset)f.LastWriteTime)).ToList();
        }
        if (files.Count == 0)
            return Task.FromResult(ToolResult.Ok(ctx.T("I didn't find any matching files.", "ملقتش ملفات مطابقة."), Array.Empty<object>()));
        var lines = string.Join("\n", files.Select(f => $"• {f.Name} — {f.Modified:ddd d MMM HH:mm}"));
        var head = about is null ? ctx.T("Most recent:", "الأحدث:") : ctx.T($"Most recent about {about.Name}:", $"الأحدث عن {about.Name}:");
        return Task.FromResult(ToolResult.Ok($"{head}\n{lines}", files.Select(f => new { path = f.Path, name = f.Name, modified = f.Modified })));
    }
}

public sealed class FileExtractTool(DocumentExtractor extractor, FilePolicy policy) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_extract",
        Category = "files",
        Description = "Read the text of a document of any common type (PDF, Word, Excel, PowerPoint, OpenDocument, text, code; image text where OCR is available) plus its metadata.",
        Parameters =
        [
            new("path", "string", "File path.", true),
            new("max_chars", "integer", "Maximum characters of text to return (default 20000)."),
        ],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx) =>
        policy.ReadRisk(policy.Resolve(args.RequireString("path")), ctx.Settings, $"Read document {args.GetString("path")}");

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var path = policy.Resolve(args.RequireString("path"));
        if (!File.Exists(path)) return ToolResult.Fail(ctx.T($"{path} doesn't exist.", $"{path} مش موجود."), status: ToolStatus.NotFound);
        var doc = await extractor.ExtractAsync(path, 200L * 1048576, ctx.CancellationToken).ConfigureAwait(false);
        if (doc.Status is not ("ok" or "empty"))
            return ToolResult.Fail(ctx.T($"I couldn't read {Path.GetFileName(path)}: {doc.Note}", $"مقدرتش أقرا {Path.GetFileName(path)}: {doc.Note}"));
        var max = Math.Clamp(args.GetInt("max_chars") ?? 20000, 500, 200_000);
        var text = doc.Text.Length > max ? doc.Text[..max] + "\n…(truncated)" : doc.Text;
        return ToolResult.Ok(ctx.T($"Read {Path.GetFileName(path)} ({doc.Text.Length:N0} characters{(doc.Pages is { } p ? $", {p} pages" : "")}).",
                                   $"قريت {Path.GetFileName(path)} ({doc.Text.Length:N0} حرف{(doc.Pages is { } p2 ? $"، {p2} صفحة" : "")})."),
            new { path, doc.Title, doc.Author, doc.Pages, doc.Metadata, doc.Note, text });
    }
}

public sealed class FileSummarizeTool(DocumentExtractor extractor, FileFinder finder, FilePolicy policy) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_summarize",
        Category = "files",
        Description = "Key points of a document (the most representative sentences, extracted — not rewritten) plus its title, author, size and pages. Give a path or a description.",
        Parameters =
        [
            new("file", "string", "Path, file name or what the document is about.", true),
            new("points", "integer", "How many key sentences (default 5)."),
        ],
    };

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var f = args.RequireString("file");
        try
        {
            var p = policy.Resolve(f);
            if (File.Exists(p)) return policy.ReadRisk(p, ctx.Settings, $"Summarize {p}");
        }
        catch (ArgumentException) { }
        return new(RiskLevel.Safe, $"Summarize {f}");
    }

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var path = await finder.ResolveAsync(args.RequireString("file"), ctx).ConfigureAwait(false);
        if (path is null) return ToolResult.Fail(ctx.T($"I couldn't find “{args.GetString("file")}”.", $"ملقتش «{args.GetString("file")}»."), status: ToolStatus.NotFound);
        if (policy.ProtectedReason(path) is { } reason) return ToolResult.Fail(reason, status: ToolStatus.Denied);
        var doc = await extractor.ExtractAsync(path, 200L * 1048576, ctx.CancellationToken).ConfigureAwait(false);
        if (doc.Status != "ok") return ToolResult.Fail(ctx.T($"I couldn't read {Path.GetFileName(path)}: {doc.Note}", $"مقدرتش أقرا {Path.GetFileName(path)}: {doc.Note}"));
        var points = TextAnalysis.KeySentences(doc.Text, Math.Clamp(args.GetInt("points") ?? 5, 1, 15));
        var info = new FileInfo(path);
        var meta = string.Join(" · ", new[] { doc.Title, doc.Author is { } a ? ctx.T($"by {a}", $"كتبه {a}") : null, doc.Pages is { } p ? ctx.T($"{p} pages", $"{p} صفحة") : null,
            ctx.T($"changed {info.LastWriteTime:d MMM yyyy}", $"اتعدل {info.LastWriteTime:d/M/yyyy}") }.Where(x => x is not null));
        var body = points.Count == 0 ? ctx.T("(no clear sentences to extract)", "(مفيش جمل واضحة)") : string.Join("\n", points.Select(s => "• " + s));
        return ToolResult.Ok(ctx.T($"{info.Name} — {meta}\nKey points (extracted from the text):\n{body}", $"{info.Name} — {meta}\nأهم النقط (من النص نفسه):\n{body}"),
            new { path, doc.Title, doc.Author, doc.Pages, points, chars = doc.Text.Length });
    }
}

public sealed class FileCompareTool(DocumentExtractor extractor, FileFinder finder, FilePolicy policy) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "file_compare",
        Category = "files",
        Description = "Compare two versions of a document (or a document with its previous version in the same folder) and report what was added and removed.",
        Parameters =
        [
            new("file", "string", "The (newer) document: path, name or description.", true),
            new("other", "string", "The document to compare with; omit to use its previous version."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Compare {args.GetString("file")}{(args.GetString("other") is { } o ? $" with {o}" : " with its previous version")}";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var a = await finder.ResolveAsync(args.RequireString("file"), ctx).ConfigureAwait(false);
        if (a is null) return ToolResult.Fail(ctx.T($"I couldn't find “{args.GetString("file")}”.", $"ملقتش «{args.GetString("file")}»."), status: ToolStatus.NotFound);
        string? b = args.GetString("other") is { } other ? await finder.ResolveAsync(other, ctx).ConfigureAwait(false) : PreviousVersion(a);
        if (b is null)
            return ToolResult.Fail(ctx.T($"I couldn't find an earlier version of {Path.GetFileName(a)} next to it. Tell me which file to compare with.",
                                         $"ملقتش نسخة أقدم من {Path.GetFileName(a)} جنبه. قولي أقارنه بأنهي ملف."), status: ToolStatus.NotFound);
        foreach (var p in new[] { a, b })
            if (policy.ProtectedReason(p) is { } reason) return ToolResult.Fail(reason, status: ToolStatus.Denied);
        // Newer file is "after".
        var (oldPath, newPath) = File.GetLastWriteTime(a) >= File.GetLastWriteTime(b) ? (b, a) : (a, b);
        var oldDoc = await extractor.ExtractAsync(oldPath, 200L * 1048576, ctx.CancellationToken).ConfigureAwait(false);
        var newDoc = await extractor.ExtractAsync(newPath, 200L * 1048576, ctx.CancellationToken).ConfigureAwait(false);
        if (oldDoc.Status != "ok" || newDoc.Status != "ok")
            return ToolResult.Fail(ctx.T("I couldn't read one of the files.", "مقدرتش أقرا واحد من الملفين."));
        var diff = TextAnalysis.Compare(oldDoc.Text, newDoc.Text);
        var oldName = Path.GetFileName(oldPath);
        var newName = Path.GetFileName(newPath);
        if (diff.Identical)
            return ToolResult.Ok(ctx.T($"{newName} and {oldName} have the same text.", $"{newName} و{oldName} نفس النص."), diff);
        string Sample(IReadOnlyList<string> lines) => string.Join("\n", lines.Take(5).Select(l => "  " + (l.Length > 160 ? l[..157] + "…" : l)));
        var msg = ctx.T(
            $"{newName} vs the earlier {oldName}: {diff.Added} line(s) added, {diff.Removed} removed, {diff.Unchanged} unchanged." +
            (diff.Added > 0 ? $"\nAdded:\n{Sample(diff.AddedLines)}" : "") + (diff.Removed > 0 ? $"\nRemoved:\n{Sample(diff.RemovedLines)}" : ""),
            $"{newName} مقارنة بـ{oldName} الأقدم: {diff.Added} سطر جديد، {diff.Removed} اتشال، {diff.Unchanged} زي ما هو." +
            (diff.Added > 0 ? $"\nالجديد:\n{Sample(diff.AddedLines)}" : "") + (diff.Removed > 0 ? $"\nاللي اتشال:\n{Sample(diff.RemovedLines)}" : ""));
        return ToolResult.Ok(msg, new { newer = newPath, older = oldPath, diff.Added, diff.Removed, diff.Unchanged, addedLines = diff.AddedLines.Take(50), removedLines = diff.RemovedLines.Take(50) });
    }

    /// <summary>The newest other file in the same folder with the same name stem and type, older than this one.</summary>
    public static string? PreviousVersion(string path)
    {
        var info = new FileInfo(path);
        if (info.Directory is null) return null;
        var stem = TextAnalysis.VersionStem(info.Name);
        if (stem.Length == 0) return null;
        return info.Directory.EnumerateFiles("*" + info.Extension)
            .Where(f => f.FullName != info.FullName && TextAnalysis.VersionStem(f.Name) == stem && f.LastWriteTime <= info.LastWriteTime)
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault()?.FullName;
    }
}
