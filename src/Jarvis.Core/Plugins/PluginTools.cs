using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jarvis.Core.AI;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Plugins;

/// <summary>Writes a plugin draft from a description, using the coding model. The draft does nothing until approved.</summary>
public sealed partial class PluginGenerator(ModelRouter router)
{
    private const string Spec = """
        You write JARVIS plugins. Reply with ONE JSON object and nothing else:
        {"manifest": {...}, "code": "..."}

        manifest fields:
          id: lowercase-dashes (2-40 chars); name; version "1.0.0"; description (one sentence);
          permissions: { "http": ["host.example.com"] (hosts it may READ with GET), "httpSend": [] (hosts it may POST to; avoid unless essential),
                         "storage": false, "notify": false } — ask only for what the tools need;
          tools: [{ "name": "snake_case", "description": "...", "risk": "safe" | "sensitive" | "critical",
                    "parameters": [{ "name": "snake_case", "type": "string|number|integer|boolean", "description": "...", "required": true }] }];
          tests: [{ "tool": "<tool name>", "args": {...}, "expect": "text the result must contain" }] — at least one test per tool.

        code: plain JavaScript (no modules, no require, no fetch, no async). Define one function per tool:
          function tool_name(args) { ... return { message: "short answer for the user", data: {...} }; }
        Available API (nothing else exists — no files, processes or other network access):
          jarvis.http.get(url) -> string; jarvis.http.getJson(url) -> object (only https hosts listed in permissions.http)
          jarvis.http.post(url, body) / postJson(url, body) (only hosts in permissions.httpSend)
          jarvis.storage.get(key) / set(key, value) (strings; needs permissions.storage)
          jarvis.notify(text) (needs permissions.notify); jarvis.log(text)
        Prefer free, key-less public APIs. Never ask for or embed API keys or passwords. Validate inputs.
        """;

    public async Task<(string Manifest, string Code)> GenerateAsync(string description, CancellationToken ct)
    {
        var route = await router.RouteAsync(description, ct, ModelRoles.Coding, null).ConfigureAwait(false);
        if (!route.HasModel) throw new PluginException("Writing a plugin needs an AI model (Settings → AI). You can also import one.");
        var reply = await route.Provider!.CompleteAsync(new ChatRequest
        {
            Model = route.Model!,
            Messages = [ChatMessage.System(Spec), ChatMessage.User($"Write a plugin that: {description}")],
            MaxTokens = 3000,
        }, ct).ConfigureAwait(false);
        return Parse(reply.Content ?? "");
    }

    internal static (string Manifest, string Code) Parse(string reply)
    {
        var text = Fence().Match(reply) is { Success: true } f ? f.Groups[1].Value : reply;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new PluginException("The model didn't return a plugin.");
        JsonObject root;
        try { root = JsonNode.Parse(text[start..(end + 1)]) as JsonObject ?? throw new PluginException("The model didn't return a plugin."); }
        catch (System.Text.Json.JsonException ex) { throw new PluginException($"The model's plugin wasn't valid JSON: {ex.Message}"); }
        var manifest = root["manifest"] as JsonObject ?? throw new PluginException("The plugin has no manifest.");
        var code = root["code"]?.GetValue<string>() ?? throw new PluginException("The plugin has no code.");
        return (manifest.ToJsonString(), code);
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```")]
    private static partial Regex Fence();
}

public sealed class PluginCreateTool(PluginGenerator generator, PluginManager plugins) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "plugin_create",
        Category = "plugins",
        Description = "Write a new JARVIS plugin (a small sandboxed tool) from a description, check it and run its offline tests. It is NOT installed: the user reviews its permissions and approves installation.",
        Parameters = [new("description", "string", "What the plugin should do.", true)],
    };

    protected override string Describe(ToolArgs args) => $"Draft a plugin: {args.GetString("description")}";

    public override async Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        try
        {
            var (manifest, code) = await generator.GenerateAsync(args.RequireString("description"), ctx.CancellationToken).ConfigureAwait(false);
            var draft = plugins.SaveDraft(manifest, code, "generated");
            var checkd = plugins.Check(draft.Id, ctx.CancellationToken);
            if (checkd.Update is { } u)
            {
                // A new version of something already installed: the old one keeps running until the user approves.
                var ok = u.Report?.Ok == true;
                return new ToolResult
                {
                    Success = ok,
                    Message = ok
                        ? ctx.T($"I wrote version {u.Manifest.Version} of “{u.Manifest.Name}” and it passed its checks. What changes: {string.Join("; ", u.Changes)}. Version {checkd.Manifest.Version} keeps running until you approve the update in Plugins.",
                                $"كتبت نسخة {u.Manifest.Version} من «{u.Manifest.Name}» وعدّت الاختبارات. اللي هيتغير: {string.Join("؛ ", u.Changes)}. نسخة {checkd.Manifest.Version} شغالة لحد ما توافق على التحديث في الإضافات.")
                        : ctx.T($"I wrote version {u.Manifest.Version} of “{u.Manifest.Name}”, but it didn't pass its checks; the installed version is unchanged.",
                                $"كتبت نسخة {u.Manifest.Version} من «{u.Manifest.Name}» بس معدّتش الاختبارات؛ النسخة المتركبة زي ما هي."),
                    Data = new { plugin = checkd.Id, update = u.Manifest.Version, u.Status, u.Changes, u.Report },
                };
            }
            var r = checkd.Report!;
            var summary = r.Ok
                ? ctx.T($"I wrote the “{checkd.Manifest.Name}” plugin and it passed its checks. {PluginManager.Describe(checkd.Manifest)} It's not installed — review it in Plugins and approve if you want it.",
                        $"كتبت إضافة «{checkd.Manifest.Name}» وعدّت الاختبارات. {PluginManager.Describe(checkd.Manifest)} لسه متركبتش — راجعها في الإضافات ووافق لو عايزها.")
                : ctx.T($"I wrote “{checkd.Manifest.Name}”, but it didn't pass: {string.Join("; ", r.Problems.Concat(r.Tests.Where(t => !t.Passed && !t.Skipped).Select(t => $"{t.Tool}: {t.Detail}")).Take(4))}",
                        $"كتبت «{checkd.Manifest.Name}» بس معدّتش: {string.Join("؛ ", r.Problems.Concat(r.Tests.Where(t => !t.Passed && !t.Skipped).Select(t => $"{t.Tool}: {t.Detail}")).Take(4))}");
            return new ToolResult { Success = r.Ok, Message = summary, Data = new { plugin = checkd.Id, status = checkd.Status, report = r } };
        }
        catch (PluginException ex) { return ToolResult.Fail(ex.Message); }
    }
}

public sealed class PluginInstallTool(PluginManager plugins) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "plugin_install",
        Category = "plugins",
        Risk = RiskLevel.Critical,
        Description = "Install a plugin that passed its checks. Always asks the user, showing exactly what it may do.",
        Parameters = [new("plugin", "string", "Plugin id.", true)],
    };

    // Adding new capabilities to JARVIS is always the user's call.
    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var p = plugins.Get(args.RequireString("plugin"));
        return new(RiskLevel.Critical, p is null ? "Install a plugin" : $"Install the “{p.Manifest.Name}” plugin {p.Manifest.Version}",
            p is null ? null : PluginManager.Describe(p.Manifest));
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        try
        {
            var p = plugins.Install(args.RequireString("plugin"));
            return Task.FromResult(ToolResult.Ok(ctx.T($"Installed “{p.Manifest.Name}”. Its tools: {string.Join(", ", p.Manifest.Tools.Select(t => t.Name))}.", $"ركّبت «{p.Manifest.Name}». أدواتها: {string.Join("، ", p.Manifest.Tools.Select(t => t.Name))}."),
                new { plugin = p.Id, tools = p.Manifest.Tools.Select(p.Manifest.ToolName) }));
        }
        catch (PluginException ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
    }
}

public sealed class PluginUpdateTool(PluginManager plugins) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "plugin_update",
        Category = "plugins",
        Risk = RiskLevel.Critical,
        Description = "Replace an installed plugin with its checked update. Always asks the user, showing what changes.",
        Parameters = [new("plugin", "string", "Plugin id.", true)],
    };

    // Changing what an installed capability can do is always the user's call, even when nothing new is asked for.
    public override RiskAssessment Assess(ToolArgs args, ToolContext ctx)
    {
        var p = plugins.Get(args.RequireString("plugin"));
        if (p?.Update is not { } u) return new(RiskLevel.Critical, "Update a plugin");
        return new(RiskLevel.Critical, $"Update “{p.Manifest.Name}” {p.Manifest.Version} → {u.Manifest.Version}",
            (u.MorePermissions ? "It asks for more than before. " : "") + string.Join("; ", u.Changes) + ". " + PluginManager.Describe(u.Manifest));
    }

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        try
        {
            var p = plugins.ApplyUpdate(args.RequireString("plugin"));
            return Task.FromResult(ToolResult.Ok(ctx.T($"Updated “{p.Manifest.Name}” to {p.Manifest.Version}.", $"حدّثت «{p.Manifest.Name}» لنسخة {p.Manifest.Version}."),
                new { plugin = p.Id, version = p.Manifest.Version }));
        }
        catch (PluginException ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
    }
}

public sealed class PluginListTool(PluginManager plugins) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "plugin_list",
        Category = "plugins",
        Description = "List plugins: installed, waiting for approval, failed.",
    };

    protected override string Describe(ToolArgs args) => "List plugins";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var list = plugins.List();
        if (list.Count == 0) return Task.FromResult(ToolResult.Ok(ctx.T("No plugins yet. Ask me to make one, e.g. “make a plugin that converts currencies”.", "مفيش إضافات لسه. اطلب مني أعمل واحدة، مثلاً «اعمل إضافة تحول العملات».")));
        return Task.FromResult(ToolResult.Ok(string.Join("\n", list.Select(p => $"• {p.Manifest.Name} {p.Manifest.Version} — {p.Status}{(p.Update is { } u ? $" (update {u.Manifest.Version}: {u.Status})" : "")}")),
            new { plugins = list.Select(p => new { p.Id, p.Manifest.Name, p.Status }) }));
    }
}
