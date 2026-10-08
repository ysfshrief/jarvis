using System.Text.RegularExpressions;
using Jarvis.Core.Memory;

namespace Jarvis.Core.Tools.Builtin;

public sealed partial class MemoryRememberTool(KnowledgeService knowledge) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "memory_remember",
        Category = "memory",
        Description = "Save something to JARVIS's long-term memory. Use only when the user asks you to remember something, or states a clear lasting preference. Never store guesses as facts.",
        Parameters =
        [
            new("content", "string", "The fact or preference to remember, as a complete self-contained sentence.", true),
            new("kind", "string", "Kind of memory.", false, MemoryKinds.All),
            new("subject", "string", "Who/what it is about, e.g. a person or project name."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Remember: {args.GetString("content")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var s = ctx.Settings.Memory;
        if (!s.Enabled)
            return Task.FromResult(ToolResult.Fail(ctx.T("Memory is turned off in Settings.", "الذاكرة مقفولة من الإعدادات.")));

        var kind = args.GetString("kind") ?? MemoryKinds.Fact;
        if (!MemoryKinds.All.Contains(kind)) kind = MemoryKinds.Fact;
        if (!s.AllowedKinds.Contains(kind))
            return Task.FromResult(ToolResult.Fail(ctx.T($"You've told me not to store '{kind}' memories.", $"انت قايلي مخزنش حاجات من نوع '{kind}'.")));

        // Only an explicit "remember…" from the user makes a fact. If the AI decided to store something
        // on its own, it is kept as "derived" — a hint the user can confirm or delete.
        var explicitAsk = ctx.RequestText is null || ExplicitAsk().IsMatch(Language.TextNormalizer.Normalize(ctx.RequestText));
        var source = explicitAsk ? MemorySources.UserExplicit : MemorySources.Derived;
        var provenance = new MemoryProvenance(ctx.Via, ctx.ConversationId, ctx.TurnId, Quote(ctx.RequestText),
            explicitAsk ? null : "The assistant inferred this from the conversation; it hasn't been confirmed.", Definition.Name);
        var item = knowledge.Remember(new NewMemory(args.RequireString("content"), kind, args.GetString("subject"), source, Provenance: provenance));
        var msg = explicitAsk
            ? ctx.T($"Noted{ctx.CommaSir}.", $"اتسجلت{ctx.CommaSir}.")
            : ctx.T("I've noted that as unconfirmed — you can confirm or delete it in Memory.", "سجلتها كحاجة مش متأكدة منها — تقدر تأكدها أو تمسحها من الذاكرة.");
        return Task.FromResult(ToolResult.Ok(msg, new { item.Id, item.Kind, item.Content, item.Source }));
    }

    private static string? Quote(string? text) => text is null ? null : text.Length <= 300 ? text : text[..297] + "...";

    [GeneratedRegex(@"\b(remember|don'?t forget|note that|make a note|keep in mind|save (this|that)|for the record|from now on|always|never)\b|افتكر|فاكر|متنساش|ماتنساش|خلي في بالك|خد بالك ان|سجل|احفظ|دايما|ابدا|من هنا ورايح")]
    private static partial Regex ExplicitAsk();
}

public sealed class MemorySearchTool(KnowledgeService knowledge) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "memory_search",
        Category = "memory",
        Description = "Search JARVIS's long-term memory (facts, preferences, people, organisations, projects and how they relate) for anything related to the query.",
        Parameters = [new("query", "string", "What to look for.", true)],
    };

    protected override string Describe(ToolArgs args) => $"Search memory: {args.GetString("query")}";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var query = args.RequireString("query");
        var entity = knowledge.Entities.Find(query) ?? knowledge.Entities.Mentioned(query).FirstOrDefault();
        var hits = await knowledge.RecallAsync(query, 10, ctx.CancellationToken).ConfigureAwait(false);
        var profile = entity is null ? null : knowledge.Profile(entity);

        var lines = new List<string>();
        foreach (var h in hits.Take(6))
            lines.Add("• " + h.Memory.Content + (h.Memory.IsConfirmed ? "" : ctx.T(" (unconfirmed)", " (مش متأكد)")));
        if (profile is not null)
        {
            foreach (var r in profile.Relations.Take(5))
            {
                var line = $"• {r.FromName} {r.Type.Replace('_', ' ')} {r.ToName}";
                if (!lines.Contains(line)) lines.Add(line);
            }
            if (profile.Tasks.Count > 0)
                lines.Add(ctx.T($"• Open tasks: {string.Join(", ", profile.Tasks.Take(3).Select(t => t.Title))}", $"• مهام مفتوحة: {string.Join("، ", profile.Tasks.Take(3).Select(t => t.Title))}"));
        }
        if (lines.Count == 0)
        {
            var none = ctx.T($"I don't have anything stored about \"{query}\".", $"معنديش أي حاجة متسجلة عن \"{query}\".");
            // "who is X" with nothing stored lets the AI answer from general knowledge instead.
            return args.GetBool("require") == true ? ToolResult.Fail(none, status: ToolStatus.NotFound) : ToolResult.Ok(none, Array.Empty<object>());
        }

        var msg = ctx.T($"Here's what I have on \"{query}\":\n{string.Join("\n", lines)}", $"ده اللي عندي عن \"{query}\":\n{string.Join("\n", lines)}");
        return ToolResult.Ok(msg, new
        {
            memories = hits.Select(h => new { h.Memory.Id, h.Memory.Kind, h.Memory.Content, h.Memory.Subject, h.Memory.Source, h.Memory.Confidence, h.Semantic }),
            entity = profile is null ? null : new
            {
                profile.Entity.Name, profile.Entity.Type,
                relations = profile.Relations.Select(r => new { r.FromName, r.Type, r.ToName, r.Source }),
                tasks = profile.Tasks.Select(t => new { t.Title, t.State, t.Priority }),
            },
        });
    }
}

public sealed class MemoryRelateTool(KnowledgeService knowledge) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "memory_relate",
        Category = "memory",
        Description = "Record how two people/organisations/projects relate, e.g. Ahmed works_at CityCrep, Sara manages Project Atlas. Use only for relationships the user stated.",
        Parameters =
        [
            new("from", "string", "First entity name, e.g. Ahmed.", true),
            new("from_type", "string", "Type of the first entity.", false, EntityTypes.All),
            new("relation", "string", "Relationship in snake_case, e.g. works_at, manages, client_of, member_of, reports_to, owns, related_to.", true),
            new("to", "string", "Second entity name, e.g. CityCrep.", true),
            new("to_type", "string", "Type of the second entity.", false, EntityTypes.All),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Remember: {args.GetString("from")} {args.GetString("relation")?.Replace('_', ' ')} {args.GetString("to")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        if (!ctx.Settings.Memory.Enabled)
            return Task.FromResult(ToolResult.Fail(ctx.T("Memory is turned off in Settings.", "الذاكرة مقفولة من الإعدادات.")));
        string Type(string? t, string fallback) => t is not null && EntityTypes.All.Contains(t) ? t : fallback;
        var provenance = new MemoryProvenance(ctx.Via, ctx.ConversationId, ctx.TurnId, ctx.RequestText, null, Definition.Name);
        Relation rel;
        try
        {
            rel = knowledge.Relate(args.RequireString("from"), Type(args.GetString("from_type"), EntityTypes.Person), args.RequireString("relation"),
                args.RequireString("to"), Type(args.GetString("to_type"), EntityTypes.Organization), MemorySources.UserExplicit, provenance);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ToolResult.Fail(ex.Message));
        }
        return Task.FromResult(ToolResult.Ok(ctx.T($"Noted: {rel.FromName} {rel.Type.Replace('_', ' ')} {rel.ToName}.", $"اتسجل: {rel.FromName} {rel.Type.Replace('_', ' ')} {rel.ToName}."),
            new { rel.Id, rel.FromName, rel.Type, rel.ToName }));
    }
}

public sealed class MemoryForgetTool(KnowledgeService knowledge) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "memory_forget",
        Category = "memory",
        Risk = RiskLevel.Sensitive,
        Description = "Delete memories matching a query (or a specific memory id).",
        Parameters =
        [
            new("query", "string", "Text identifying what to forget."),
            new("id", "string", "Exact memory id, if known."),
        ],
    };

    private MemoryStore Store => knowledge.Memories;

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var id = args.GetString("id");
        if (id is not null)
        {
            var item = Store.Get(id);
            return new(RiskLevel.Sensitive, $"Forget: {item?.Content ?? id}");
        }
        var matches = Store.Search(args.RequireString("query"), 5);
        var preview = matches.Count == 0 ? "(nothing matches)" : string.Join("; ", matches.Select(m => m.Content));
        return new(RiskLevel.Sensitive, $"Forget {matches.Count} memor{(matches.Count == 1 ? "y" : "ies")}: {preview}");
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var id = args.GetString("id");
        var targets = id is not null
            ? (Store.Get(id) is { } one ? [one] : new List<MemoryItem>())
            : Store.Search(args.RequireString("query"), 5).ToList();
        if (targets.Count == 0)
            return Task.FromResult(ToolResult.Ok(ctx.T("There was nothing matching to forget.", "مكانش فيه حاجة زي كده أنساها.")));
        var n = targets.Count(t => Store.Delete(t.Id));
        return Task.FromResult(ToolResult.Ok(ctx.T($"Forgotten ({n}).", $"اتمسحت ({n})."), new { deleted = n }));
    }
}
