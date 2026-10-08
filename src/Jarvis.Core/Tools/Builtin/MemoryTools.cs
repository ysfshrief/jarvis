using Jarvis.Core.Memory;

namespace Jarvis.Core.Tools.Builtin;

public sealed class MemoryRememberTool(MemoryStore store) : ToolBase
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

        // An explicit user request is a confirmed memory; anything the AI decides on its own is "derived".
        var item = store.Add(new NewMemory(args.RequireString("content"), kind, args.GetString("subject"), MemorySources.UserExplicit));
        return Task.FromResult(ToolResult.Ok(ctx.T($"Noted{ctx.CommaSir}.", $"اتسجلت{ctx.CommaSir}."), new { item.Id, item.Kind, item.Content }));
    }
}

public sealed class MemorySearchTool(MemoryStore store) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "memory_search",
        Category = "memory",
        Description = "Search JARVIS's long-term memory (facts, preferences, people, projects) for anything related to the query.",
        Parameters = [new("query", "string", "What to look for.", true)],
    };

    protected override string Describe(ToolArgs args) => $"Search memory: {args.GetString("query")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var query = args.RequireString("query");
        var found = store.Search(query, 10);
        if (found.Count == 0)
            return Task.FromResult(ToolResult.Ok(ctx.T($"I don't have anything stored about \"{query}\".", $"معنديش أي حاجة متسجلة عن \"{query}\"."), Array.Empty<object>()));

        var lines = string.Join("\n", found.Take(5).Select(m => "• " + m.Content));
        var msg = ctx.T($"Here's what I have on \"{query}\":\n{lines}", $"ده اللي عندي عن \"{query}\":\n{lines}");
        return Task.FromResult(ToolResult.Ok(msg, found.Select(m => new { m.Id, m.Kind, m.Content, m.Subject, m.Source, m.Confidence })));
    }
}

public sealed class MemoryForgetTool(MemoryStore store) : ToolBase
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

    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var id = args.GetString("id");
        if (id is not null)
        {
            var item = store.Get(id);
            return new(RiskLevel.Sensitive, $"Forget: {item?.Content ?? id}");
        }
        var matches = store.Search(args.RequireString("query"), 5);
        var preview = matches.Count == 0 ? "(nothing matches)" : string.Join("; ", matches.Select(m => m.Content));
        return new(RiskLevel.Sensitive, $"Forget {matches.Count} memor{(matches.Count == 1 ? "y" : "ies")}: {preview}");
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var id = args.GetString("id");
        var targets = id is not null
            ? (store.Get(id) is { } one ? [one] : new List<MemoryItem>())
            : store.Search(args.RequireString("query"), 5).ToList();
        if (targets.Count == 0)
            return Task.FromResult(ToolResult.Ok(ctx.T("There was nothing matching to forget.", "مكانش فيه حاجة زي كده أنساها.")));
        var n = targets.Count(t => store.Delete(t.Id));
        return Task.FromResult(ToolResult.Ok(ctx.T($"Forgotten ({n}).", $"اتمسحت ({n})."), new { deleted = n }));
    }
}
