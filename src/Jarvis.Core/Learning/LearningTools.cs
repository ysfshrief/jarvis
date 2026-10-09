using Jarvis.Core.Tools;

namespace Jarvis.Core.Learning;

public sealed class ResearchTopicTool(KnowledgeIngestion ingestion) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "research_topic",
        Category = "learning",
        RequiresInternet = true,
        ReadsUntrustedContent = true,
        Description = "Research a topic on the web: read a few good sources and save the key facts as unconfirmed notes that cite their source, flagging any that contradict what JARVIS already knows. Use when the user asks to research, learn about or look into something to remember it.",
        Parameters =
        [
            new("topic", "string", "What to research.", true),
            new("sources", "integer", "How many sources to read (1-5, default 3)."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Research: {args.GetString("topic")}";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        try
        {
            var report = await ingestion.ResearchAsync(args.RequireString("topic"), args.GetInt("sources") ?? 3, ctx).ConfigureAwait(false);
            return LearningReply.From(report, ctx);
        }
        catch (LearningException ex) { return ToolResult.Fail(ex.Message); }
    }
}

public sealed class LearnFromSourceTool(KnowledgeIngestion ingestion) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "learn_from_source",
        Category = "learning",
        ReadsUntrustedContent = true,
        Description = "Read one web page or document (PDF, Word, …) and save its key facts as unconfirmed notes that cite it, flagging contradictions with what JARVIS already knows.",
        Parameters =
        [
            new("source", "string", "A web address or a file path.", true),
            new("topic", "string", "Optional focus, e.g. 'pricing'."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Learn from {args.GetString("source")}";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        try
        {
            var report = await ingestion.LearnFromSourceAsync(args.RequireString("source"), args.GetString("topic"), ctx).ConfigureAwait(false);
            return LearningReply.From(report, ctx);
        }
        catch (LearningException ex) { return ToolResult.Fail(ex.Message); }
    }
}

internal static class LearningReply
{
    public static ToolResult From(LearnReport r, ToolContext ctx)
    {
        if (r.Facts.Count == 0)
            return ToolResult.Fail(ctx.T($"I read {r.Sources.Count} source(s) about “{r.Topic}” but found no clear facts worth keeping.",
                                         $"قريت {r.Sources.Count} مصدر عن «{r.Topic}» بس ملقيتش حقايق واضحة تستاهل أحفظها."));
        var conflicts = r.Facts.Count(f => f.Conflict is not null);
        var lines = r.Facts.Take(8).Select(f => $"• {f.Fact} [{f.Source.Index}]{(f.Conflict is null ? "" : " ⚠")}");
        var sources = r.Sources.Select(s => $"[{s.Index}] {s.Title} — {s.Location}");
        var head = ctx.T($"Here's what I learned about “{r.Topic}” ({r.Facts.Count} fact(s) from {r.Sources.Count} source(s)):",
                         $"ده اللي اتعلمته عن «{r.Topic}» ({r.Facts.Count} معلومة من {r.Sources.Count} مصدر):");
        var tail = ctx.T("Saved as unconfirmed notes — confirm or reject them in Memory → To review.",
                         "اتحفظوا كملاحظات مش مؤكدة — أكدها أو ارفضها في الذاكرة ← للمراجعة.");
        if (conflicts > 0)
            tail = ctx.T($"⚠ {conflicts} contradict what I already had; those are marked for you to settle. ", $"⚠ {conflicts} بيعارضوا حاجات عندي؛ معلّمين عشان تحسمهم. ") + tail;
        var message = $"{head}\n{string.Join("\n", lines)}\n\n{string.Join("\n", sources)}\n\n{tail}";
        return ToolResult.Ok(message, new
        {
            topic = r.Topic,
            sources = r.Sources.Select(s => new { s.Index, s.Title, s.Location }),
            facts = r.Facts.Select(f => new { f.Fact, f.Subject, source = f.Source.Index, memoryId = f.Memory.Id, f.Conflict }),
            problems = r.Problems,
        });
    }
}
