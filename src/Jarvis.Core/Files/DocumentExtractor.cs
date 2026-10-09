using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Jarvis.Core.Files;

public static class FileKinds
{
    public const string Pdf = "pdf";
    public const string Document = "document";
    public const string Spreadsheet = "spreadsheet";
    public const string Presentation = "presentation";
    public const string Image = "image";
    public const string Code = "code";
    public const string Text = "text";
    public const string Archive = "archive";
    public const string Other = "other";

    public static readonly string[] All = [Pdf, Document, Spreadsheet, Presentation, Image, Code, Text, Archive, Other];

    private static readonly HashSet<string> CodeExt = [".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".kt", ".go", ".rs", ".cpp", ".cc", ".c", ".h", ".hpp", ".swift",
        ".rb", ".php", ".sql", ".ps1", ".sh", ".bat", ".css", ".scss", ".html", ".vue", ".dart", ".lua", ".r", ".m", ".csproj", ".sln", ".gradle"];
    private static readonly HashSet<string> TextExt = [".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log", ".ini", ".toml", ".cfg", ".conf", ".rtf", ".srt", ".vtt", ".eml"];
    private static readonly HashSet<string> ImageExt = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"];

    public static string Of(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => Pdf,
            ".docx" or ".doc" or ".odt" => Document,
            ".xlsx" or ".xls" or ".ods" => Spreadsheet,
            ".pptx" or ".ppt" or ".odp" => Presentation,
            ".zip" or ".7z" or ".rar" => Archive,
            _ when ImageExt.Contains(ext) => Image,
            _ when CodeExt.Contains(ext) => Code,
            _ when TextExt.Contains(ext) => Text,
            _ => Other,
        };
    }

    /// <summary>Extensions the indexer looks at (anything else is skipped entirely).</summary>
    public static bool Indexable(string path) => Of(path) != Other;
}

/// <summary>What could be read from a file.</summary>
public sealed record ExtractedDocument
{
    public string Status { get; init; } = "ok"; // ok | unsupported | error | too_large | empty
    public string Text { get; init; } = "";
    public string? Title { get; init; }
    public string? Author { get; init; }
    public int? Pages { get; init; }
    public string? Note { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Reads text out of an image (Windows OCR on Windows; unavailable elsewhere).</summary>
public interface IOcrEngine
{
    bool IsAvailable { get; }
    string Name { get; }
    Task<string?> RecognizeAsync(string imagePath, CancellationToken ct);
    /// <summary>Text of each page of a scanned PDF (rendered and recognised), or null where that isn't possible.</summary>
    Task<IReadOnlyList<string>?> RecognizePdfAsync(string pdfPath, int maxPages, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);
}

public sealed class NullOcrEngine : IOcrEngine
{
    public bool IsAvailable => false;
    public string Name => "none";
    public Task<string?> RecognizeAsync(string imagePath, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>
/// Turns files into plain text plus metadata, locally and without Office installed: PDF (PdfPig),
/// Word/PowerPoint/Excel (Office Open XML), OpenDocument, text and code, ZIP listings, and images
/// (dimensions; text via OCR when available). Legacy binary .doc/.xls/.ppt are reported as such.
/// </summary>
public sealed partial class DocumentExtractor(IOcrEngine ocr)
{
    public const int MaxChars = 400_000;

    public async Task<ExtractedDocument> ExtractAsync(string path, long maxBytes, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return new ExtractedDocument { Status = "error", Note = "File not found." };
        if (info.Length > maxBytes) return new ExtractedDocument { Status = "too_large", Note = $"Larger than {maxBytes / 1048576} MB; indexed by name only." };
        var ext = info.Extension.ToLowerInvariant();
        try
        {
            var doc = ext switch
            {
                ".pdf" => await PdfAsync(path, ct).ConfigureAwait(false),
                ".docx" => Docx(path),
                ".pptx" => Pptx(path),
                ".xlsx" => Xlsx(path),
                ".odt" or ".odp" or ".ods" => Odf(path),
                ".doc" or ".xls" or ".ppt" => new ExtractedDocument { Status = "unsupported", Note = "Legacy Office format: save it as .docx/.xlsx/.pptx to make its content searchable." },
                ".zip" => Zip(path),
                ".7z" or ".rar" => new ExtractedDocument { Status = "unsupported", Note = "Only ZIP archives are listed." },
                ".rtf" => new ExtractedDocument { Text = Rtf(await ReadTextAsync(path, ct).ConfigureAwait(false)) },
                _ when FileKinds.Of(path) == FileKinds.Image => await ImageAsync(path, ct).ConfigureAwait(false),
                _ => new ExtractedDocument { Text = await ReadTextAsync(path, ct).ConfigureAwait(false) },
            };
            if (doc.Status == "ok" && string.IsNullOrWhiteSpace(doc.Text) && FileKinds.Of(path) != FileKinds.Image)
                doc = doc with { Status = "empty", Note = ext == ".pdf" ? "No text layer (probably a scanned PDF)." : "No text found." };
            return doc.Text.Length > MaxChars ? doc with { Text = doc.Text[..MaxChars], Note = "Only the first part is indexed." } : doc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or FormatException
                                       or InvalidOperationException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
        {
            return new ExtractedDocument { Status = "error", Note = ex.Message };
        }
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxChars];
        var n = await reader.ReadBlockAsync(buffer, ct).ConfigureAwait(false);
        var text = new string(buffer, 0, n);
        return text.Contains('\0') ? "" : text; // binary file with a text-like extension
    }

    public const int MaxOcrPages = 20;

    /// <summary>PDF text layer; a scanned PDF (no text layer) is read page by page with OCR where available.</summary>
    private async Task<ExtractedDocument> PdfAsync(string path, CancellationToken ct)
    {
        var doc = Pdf(path);
        if (!string.IsNullOrWhiteSpace(doc.Text) || !ocr.IsAvailable) return doc;
        IReadOnlyList<string>? pages = null;
        try { pages = await ocr.RecognizePdfAsync(path, MaxOcrPages, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return doc with { Note = $"No text layer, and OCR failed: {ex.Message}" }; }
        if (pages is null || pages.All(string.IsNullOrWhiteSpace)) return doc;
        var text = string.Join("\n\n", pages.Where(p => !string.IsNullOrWhiteSpace(p)));
        var note = doc.Pages > MaxOcrPages
            ? $"Scanned PDF: text read with {ocr.Name} from the first {MaxOcrPages} of {doc.Pages} pages."
            : $"Scanned PDF: text read with {ocr.Name}.";
        return doc with { Text = text, Note = note };
    }

    private static ExtractedDocument Pdf(string path)
    {
        using var pdf = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            if (sb.Length > MaxChars) break;
            sb.AppendLine(ContentOrderTextExtractor.GetText(page));
            sb.AppendLine();
        }
        var meta = new Dictionary<string, string>();
        if (pdf.Information.Creator is { Length: > 0 } c) meta["creator"] = c;
        if (pdf.Information.CreationDate is { Length: > 0 } d) meta["created"] = d;
        return new ExtractedDocument
        {
            Text = sb.ToString(), Title = Blank(pdf.Information.Title), Author = Blank(pdf.Information.Author), Pages = pdf.NumberOfPages, Metadata = meta,
        };
    }

    private static ExtractedDocument Docx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var body = Paragraphs(zip.GetEntry("word/document.xml"), "p", "t", "tab");
        // Headers/footers rarely matter for search; footnotes and comments sometimes do.
        var extra = string.Join("\n", zip.Entries.Where(e => e.FullName is "word/footnotes.xml" or "word/comments.xml").Select(e => Paragraphs(e, "p", "t", "tab")));
        return WithCore(zip, new ExtractedDocument { Text = string.IsNullOrWhiteSpace(extra) ? body : body + "\n" + extra });
    }

    private static ExtractedDocument Pptx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var slides = zip.Entries.Where(e => SlideRegex().IsMatch(e.FullName))
            .OrderBy(e => int.Parse(SlideRegex().Match(e.FullName).Groups[1].Value)).ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < slides.Count; i++)
        {
            sb.AppendLine($"--- Slide {i + 1} ---");
            sb.AppendLine(Paragraphs(slides[i], "p", "t", null));
        }
        return WithCore(zip, new ExtractedDocument { Text = sb.ToString(), Pages = slides.Count });
    }

    private static ExtractedDocument Xlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } ss)
        {
            using var r = XmlReader.Create(ss.Open(), Safe);
            var current = new StringBuilder();
            var inSi = false;
            r.Read();
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && inSi && !r.IsEmptyElement) { current.Append(r.ReadElementContentAsString()); continue; }
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "si") { inSi = true; current.Clear(); }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "si") { shared.Add(current.ToString()); inSi = false; }
                r.Read();
            }
        }
        // Sheet names in workbook order, mapped to their part files through the relationships.
        var names = new List<(string Name, string Rel)>();
        if (zip.GetEntry("xl/workbook.xml") is { } wb)
        {
            using var r = XmlReader.Create(wb.Open(), Safe);
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "sheet")
                    names.Add((r.GetAttribute("name") ?? "Sheet", r.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? ""));
        }
        var rels = new Dictionary<string, string>();
        if (zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rel)
        {
            using var r = XmlReader.Create(rel.Open(), Safe);
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.GetAttribute("Id") is { } id && r.GetAttribute("Target") is { } target)
                    rels[id] = "xl/" + target.TrimStart('/').Replace("xl/", "");
        }
        var sb = new StringBuilder();
        var sheetIndex = 0;
        foreach (var (name, relId) in names.Count > 0 ? names : zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet")).Select(e => (e.Name, e.FullName)).ToList())
        {
            sheetIndex++;
            var part = rels.GetValueOrDefault(relId) ?? (relId.StartsWith("xl/") ? relId : $"xl/worksheets/sheet{sheetIndex}.xml");
            if (zip.GetEntry(part) is not { } sheet) continue;
            sb.AppendLine($"--- Sheet: {name} ---");
            using var r = XmlReader.Create(sheet.Open(), Safe);
            var row = new List<string>();
            var rows = 0;
            string? cellType = null;
            r.Read();
            while (!r.EOF && rows < 5000)
            {
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "v" && !r.IsEmptyElement)
                {
                    var v = r.ReadElementContentAsString();
                    row.Add(cellType == "s" && int.TryParse(v, out var si) && si < shared.Count ? shared[si] : v);
                    continue;
                }
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "t" && cellType == "inlineStr" && !r.IsEmptyElement) { row.Add(r.ReadElementContentAsString()); continue; }
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "c") cellType = r.GetAttribute("t");
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "row")
                {
                    if (row.Count > 0) sb.AppendLine(string.Join("\t", row));
                    row.Clear();
                    rows++;
                }
                r.Read();
            }
        }
        return WithCore(zip, new ExtractedDocument { Text = sb.ToString(), Pages = names.Count == 0 ? null : names.Count });
    }

    private static ExtractedDocument Odf(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var text = Paragraphs(zip.GetEntry("content.xml"), "p", null, "tab", allText: true);
        string? title = null;
        if (zip.GetEntry("meta.xml") is { } meta)
        {
            using var r = XmlReader.Create(meta.Open(), Safe);
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "title" && !r.IsEmptyElement) { title = Blank(r.ReadElementContentAsString()); break; }
        }
        return new ExtractedDocument { Text = text, Title = title };
    }

    private static ExtractedDocument Zip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Where(e => e.Length > 0).Take(2000).Select(e => e.FullName).ToList();
        return new ExtractedDocument { Text = "Archive contents:\n" + string.Join("\n", names), Pages = zip.Entries.Count, Note = $"{zip.Entries.Count} entries" };
    }

    private async Task<ExtractedDocument> ImageAsync(string path, CancellationToken ct)
    {
        var meta = new Dictionary<string, string>();
        if (ImageSize(path) is { } size) meta["dimensions"] = $"{size.W}×{size.H}";
        if (!ocr.IsAvailable) return new ExtractedDocument { Metadata = meta, Note = "Image (no OCR on this system)." };
        var text = await ocr.RecognizeAsync(path, ct).ConfigureAwait(false);
        return new ExtractedDocument { Text = text ?? "", Metadata = meta, Note = string.IsNullOrWhiteSpace(text) ? "No text found in the image." : $"Text read with {ocr.Name}." };
    }

    /// <summary>Width/height from PNG, JPEG, GIF or BMP headers (no image library needed).</summary>
    internal static (int W, int H)? ImageSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var h = new byte[32];
            if (fs.Read(h, 0, h.Length) < 24) return null;
            if (h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G')
                return ((h[16] << 24) | (h[17] << 16) | (h[18] << 8) | h[19], (h[20] << 24) | (h[21] << 16) | (h[22] << 8) | h[23]);
            if (h[0] == 'G' && h[1] == 'I' && h[2] == 'F') return (h[6] | (h[7] << 8), h[8] | (h[9] << 8));
            if (h[0] == 'B' && h[1] == 'M') return (BitConverter.ToInt32(h, 18), Math.Abs(BitConverter.ToInt32(h, 22)));
            if (h[0] == 0xFF && h[1] == 0xD8)
            {
                fs.Position = 2;
                while (fs.Position < fs.Length)
                {
                    if (fs.ReadByte() != 0xFF) return null;
                    var marker = fs.ReadByte();
                    var len = (fs.ReadByte() << 8) | fs.ReadByte();
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    {
                        fs.ReadByte();
                        var height = (fs.ReadByte() << 8) | fs.ReadByte();
                        var width = (fs.ReadByte() << 8) | fs.ReadByte();
                        return (width, height);
                    }
                    fs.Position += len - 2;
                }
            }
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>Text of an XML part, one line per paragraph element.</summary>
    private static string Paragraphs(ZipArchiveEntry? entry, string paragraph, string? textElement, string? tab, bool allText = false)
    {
        if (entry is null) return "";
        var sb = new StringBuilder();
        using var r = XmlReader.Create(entry.Open(), Safe);
        r.Read();
        while (!r.EOF && sb.Length <= MaxChars)
        {
            // ReadElementContentAsString already moves to the next node, so don't Read() again after it.
            if (r.NodeType == XmlNodeType.Element && textElement is not null && r.LocalName == textElement && !r.IsEmptyElement)
            {
                sb.Append(r.ReadElementContentAsString());
                continue;
            }
            if (r.NodeType == XmlNodeType.Element && tab is not null && r.LocalName == tab) sb.Append('\t');
            else if (allText && r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace) sb.Append(r.Value);
            else if (r.NodeType == XmlNodeType.EndElement && (r.LocalName == paragraph || r.LocalName == "h")) sb.Append('\n');
            r.Read();
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>Office core properties: title, creator, last modified by.</summary>
    private static ExtractedDocument WithCore(ZipArchive zip, ExtractedDocument doc)
    {
        if (zip.GetEntry("docProps/core.xml") is not { } core) return doc;
        string? title = null, creator = null, modifiedBy = null, modified = null;
        using (var r = XmlReader.Create(core.Open(), Safe))
        {
            r.Read();
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element && !r.IsEmptyElement && r.LocalName is "title" or "creator" or "lastModifiedBy" or "modified")
                {
                    var name = r.LocalName;
                    var value = Blank(r.ReadElementContentAsString());
                    switch (name)
                    {
                        case "title": title = value; break;
                        case "creator": creator = value; break;
                        case "lastModifiedBy": modifiedBy = value; break;
                        case "modified": modified = value; break;
                    }
                    continue;
                }
                r.Read();
            }
        }
        var meta = new Dictionary<string, string>(doc.Metadata);
        if (modifiedBy is not null) meta["lastModifiedBy"] = modifiedBy;
        if (modified is not null) meta["modified"] = modified;
        return doc with { Title = doc.Title ?? title, Author = doc.Author ?? creator, Metadata = meta };
    }

    internal static string Rtf(string rtf)
    {
        var text = RtfControl().Replace(rtf, m => m.Value.StartsWith("\\'") && m.Value.Length == 4
            ? ((char)Convert.ToInt32(m.Value[2..], 16)).ToString()
            : m.Value is "\\par" or "\\line" ? "\n" : "");
        return text.Replace("{", "").Replace("}", "").Trim();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };

    [GeneratedRegex(@"ppt/slides/slide(\d+)\.xml$")]
    private static partial Regex SlideRegex();

    [GeneratedRegex(@"\\'[0-9a-fA-F]{2}|\\[a-z]+-?\d* ?|\\[{}\\]")]
    private static partial Regex RtfControl();
}
