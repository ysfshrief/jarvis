using System.IO.Compression;
using System.Text;
using Jarvis.Core.Files;
using Jarvis.Core.Memory;
using Jarvis.Core.Settings;
using UglyToad.PdfPig.Writer;

namespace Jarvis.Core.Tests;

/// <summary>Creates small but real Office/PDF files, so extraction is tested against the actual formats.</summary>
internal static class SampleDocs
{
    public static void Docx(string path, string title, params string[] paragraphs)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "[Content_Types].xml", """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="application/xml"/></Types>""");
        var body = string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>"));
        Write(zip, "word/document.xml", $"""<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>""");
        Write(zip, "docProps/core.xml", $"""<?xml version="1.0"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{title}</dc:title><dc:creator>Mona Adel</dc:creator><cp:lastModifiedBy>Ahmed</cp:lastModifiedBy></cp:coreProperties>""");
    }

    public static void Xlsx(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "xl/workbook.xml", """<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Budget" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        Write(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"/></Relationships>""");
        Write(zip, "xl/sharedStrings.xml", """<?xml version="1.0"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Marketing</t></si><si><t>Office rent</t></si></sst>""");
        Write(zip, "xl/worksheets/sheet1.xml", """<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1"><v>12500</v></c></row><row r="2"><c r="A2" t="s"><v>1</v></c><c r="B2"><v>40000</v></c></row></sheetData></worksheet>""");
    }

    public static void Pptx(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "ppt/slides/slide1.xml", """<?xml version="1.0"?><p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:p><a:r><a:t>Atlas launch plan</a:t></a:r></a:p></p:sld>""");
        Write(zip, "ppt/slides/slide2.xml", """<?xml version="1.0"?><p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:p><a:r><a:t>Go live in March with three pilot customers</a:t></a:r></a:p></p:sld>""");
    }

    public static void Pdf(string path, params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var y = 780;
        foreach (var l in lines)
        {
            page.AddText(l, 12, new UglyToad.PdfPig.Core.PdfPoint(50, y), font);
            y -= 20;
        }
        File.WriteAllBytes(path, builder.Build());
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var s = zip.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}

public class FileKnowledgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jarvis-docs", Guid.NewGuid().ToString("n"));

    public FileKnowledgeTests()
    {
        Directory.CreateDirectory(_dir);
        SampleDocs.Docx(Path.Combine(_dir, "CityCrep contract.docx"), "Service agreement",
            "This agreement is between JARVIS Labs and CityCrep.", "The annual renewal fee is 120,000 EGP, payable in January.",
            "Either party may terminate with ninety days notice.");
        SampleDocs.Xlsx(Path.Combine(_dir, "Budget 2026.xlsx"));
        SampleDocs.Pptx(Path.Combine(_dir, "Atlas launch.pptx"));
        SampleDocs.Pdf(Path.Combine(_dir, "Invoice 1042.pdf"), "Invoice number 1042", "Customer: CityCrep", "Total due: 15,000 EGP");
        File.WriteAllText(Path.Combine(_dir, "ملاحظات الاجتماع.txt"), "اتفقنا مع سيتي كريب على تسليم العرض يوم الخميس");
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        File.WriteAllText(Path.Combine(_dir, "node_modules", "readme.md"), "should never be indexed");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private TestHost Host() => new(s => { s.Files.IndexEnabled = true; s.Files.IndexRoots = [_dir]; s.Files.AllowedRoots = [_dir]; });

    [Fact]
    public async Task Extracts_text_and_metadata_from_real_formats()
    {
        var x = new DocumentExtractor(new NullOcrEngine());
        var docx = await x.ExtractAsync(Path.Combine(_dir, "CityCrep contract.docx"), 10_000_000, default);
        Assert.Equal("ok", docx.Status);
        Assert.Contains("renewal fee is 120,000 EGP", docx.Text);
        Assert.Equal("Service agreement", docx.Title);
        Assert.Equal("Mona Adel", docx.Author);
        Assert.Equal("Ahmed", docx.Metadata["lastModifiedBy"]);

        var xlsx = await x.ExtractAsync(Path.Combine(_dir, "Budget 2026.xlsx"), 10_000_000, default);
        Assert.Contains("--- Sheet: Budget ---", xlsx.Text);
        Assert.Contains("Office rent\t40000", xlsx.Text);

        var pptx = await x.ExtractAsync(Path.Combine(_dir, "Atlas launch.pptx"), 10_000_000, default);
        Assert.Equal(2, pptx.Pages);
        Assert.Contains("Go live in March", pptx.Text);

        var pdf = await x.ExtractAsync(Path.Combine(_dir, "Invoice 1042.pdf"), 10_000_000, default);
        Assert.Equal("ok", pdf.Status);
        Assert.Equal(1, pdf.Pages);
        Assert.Contains("Total due", pdf.Text);

        var legacy = Path.Combine(_dir, "old.doc");
        File.WriteAllBytes(legacy, [0xD0, 0xCF, 0x11, 0xE0]);
        Assert.Equal("unsupported", (await x.ExtractAsync(legacy, 10_000_000, default)).Status);
        Assert.Equal("too_large", (await x.ExtractAsync(Path.Combine(_dir, "Invoice 1042.pdf"), 10, default)).Status);
    }

    [Fact]
    public async Task Indexes_folders_and_finds_documents_by_content_name_and_client()
    {
        using var host = Host();
        host.Get<EntityStore>().Upsert(EntityTypes.Organization, "CityCrep");
        await host.Get<FileIndexer>().ScanAsync(default);
        var index = host.Get<FileIndex>();
        var (files, withText, _, _) = index.Stats();
        Assert.Equal(5, files);
        Assert.Equal(5, withText);
        Assert.Null(index.GetByPath(Path.Combine(_dir, "node_modules", "readme.md")));

        var hits = await index.SearchAsync("renewal fee", 5, null, default);
        Assert.Equal("CityCrep contract.docx", hits[0].File.Name);
        Assert.Contains("renewal fee", hits[0].Snippet);

        Assert.Equal("Budget 2026.xlsx", (await index.SearchAsync("office rent", 5, null, default))[0].File.Name);
        Assert.Equal("ملاحظات الاجتماع.txt", (await index.SearchAsync("تسليم العرض", 5, null, default))[0].File.Name);
        Assert.Equal("Atlas launch.pptx", (await index.SearchAsync("atlas", 5, FileKinds.Presentation, default)).Single().File.Name);

        // Files mentioning a known client are linked to it.
        var city = host.Get<EntityStore>().Find("CityCrep")!;
        var about = index.AboutEntity(city.Id).Select(f => f.Name).ToList();
        Assert.Contains("CityCrep contract.docx", about);
        Assert.Contains("Invoice 1042.pdf", about);

        // Through the assistant (no AI needed).
        var r = await host.Say("find documents about the renewal fee");
        Assert.Contains("CityCrep contract.docx", r.Reply);
    }

    [Fact]
    public async Task Index_follows_changes_and_deletions()
    {
        using var host = Host();
        var indexer = host.Get<FileIndexer>();
        await indexer.ScanAsync(default);
        var index = host.Get<FileIndex>();
        var notes = Path.Combine(_dir, "ملاحظات الاجتماع.txt");
        File.WriteAllText(notes, "The meeting moved to Sunday at noon");
        File.SetLastWriteTime(notes, DateTime.Now.AddMinutes(1));
        Assert.True(await indexer.IndexFileAsync(notes, default));
        Assert.NotEmpty(await index.SearchAsync("sunday noon", 5, null, default));
        Assert.False(await indexer.IndexFileAsync(notes, default)); // unchanged → skipped

        File.Delete(Path.Combine(_dir, "Budget 2026.xlsx"));
        Assert.Equal(1, index.Prune(_dir));
        Assert.Empty(await index.SearchAsync("office rent", 5, null, default));
    }

    [Fact]
    public async Task Latest_summary_and_version_comparison()
    {
        using var host = Host();
        var v1 = Path.Combine(_dir, "Proposal v1.docx");
        var v2 = Path.Combine(_dir, "Proposal v2.docx");
        SampleDocs.Docx(v1, "Proposal", "Scope: website redesign.", "Price: 80,000 EGP.", "Timeline: 8 weeks.");
        File.SetLastWriteTime(v1, DateTime.Now.AddDays(-2));
        SampleDocs.Docx(v2, "Proposal", "Scope: website redesign.", "Price: 95,000 EGP.", "Timeline: 8 weeks.", "Includes a mobile app.");
        await host.Get<FileIndexer>().ScanAsync(default);

        var latest = await host.Say("latest file");
        Assert.Contains("Proposal v2.docx", latest.Reply.Split('\n')[1]);

        var cmp = await host.Say("compare Proposal v2.docx");
        Assert.True(cmp.Success, cmp.Reply);
        Assert.Contains("Proposal v2.docx vs the earlier Proposal v1.docx: 2 line(s) added, 1 removed", cmp.Reply);
        Assert.Contains("Price: 95,000 EGP.", cmp.Reply);
        Assert.Contains("Price: 80,000 EGP.", cmp.Reply);

        var sum = await host.Say($"summarize {Path.Combine(_dir, "CityCrep contract.docx")}");
        Assert.True(sum.Success, sum.Reply);
        Assert.Contains("Key points", sum.Reply);
        Assert.Contains("renewal fee", sum.Reply);
        Assert.Contains("by Mona Adel", sum.Reply);
    }

    [Fact]
    public void Version_stems_and_text_diff()
    {
        Assert.Equal("proposal", TextAnalysis.VersionStem("Proposal v2 (final).docx"));
        Assert.Equal("proposal", TextAnalysis.VersionStem("proposal_2026-10-01.docx"));
        Assert.Equal("proposal", TextAnalysis.VersionStem("Proposal - Copy.docx"));
        var d = TextAnalysis.Compare("a\nb\nc", "a\nc\nd");
        Assert.Equal(["d"], d.AddedLines);
        Assert.Equal(["b"], d.RemovedLines);
        Assert.Equal(2, d.Unchanged);
    }

    [Fact]
    public async Task Without_the_index_searches_fall_back_to_names_and_say_so()
    {
        using var host = new TestHost(s => s.Files.AllowedRoots = [_dir]);
        var r = await host.Say("find documents about atlas");
        Assert.Contains("off", r.Reply);
        Assert.Contains("Atlas launch.pptx", r.Reply);
    }
}
