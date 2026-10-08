using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Core.Activity;
using Jarvis.Core.AI;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Memory;
using Jarvis.Core.Permissions;
using Jarvis.Core.Presence;
using Jarvis.Core.Settings;
using Jarvis.Core.Tasks;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Agent;

[JsonConverter(typeof(JsonStringEnumConverter<InputSource>))]
public enum InputSource { Text, Voice, Api, Scheduler, Remote }

public sealed record UserInput(string Text, string? ConversationId = null, InputSource Source = InputSource.Text);

public sealed record AgentTurnResult
{
    public required string ConversationId { get; init; }
    public required string Reply { get; init; }
    public required string Lang { get; init; }
    /// <summary>"deterministic", "ai", or "none" (no model available).</summary>
    public required string Route { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<ToolStep> Steps { get; init; } = [];
    public bool Success { get; init; } = true;
    public long DurationMs { get; init; }
    public InputSource Source { get; init; }
}

/// <summary>Information about the host platform, shown to the AI and in the UI.</summary>
public sealed record PlatformInfo(string Name, string Description);

/// <summary>Short-term conversational context (the "working memory" of a conversation).</summary>
public sealed class ConversationContext(string id)
{
    public string Id { get; } = id;
    public List<ChatMessage> History { get; } = [];
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.Now;
    public Lang? ForcedLang { get; set; }
    public Lang LastLang { get; set; } = Lang.En;
    public SemaphoreSlim HistoryLock { get; } = new(1, 1);
}

/// <summary>
/// The core agent loop:
/// input → language → (approval answer | deterministic intent | AI planning with tools)
/// → permission-checked execution → verification → reply → memory/activity.
/// </summary>
public sealed class AgentOrchestrator(
    ToolExecutor executor,
    IToolRegistry tools,
    ModelRouter router,
    ApprovalBroker approvals,
    MemoryStore memory,
    TaskStore tasks,
    ConversationStore conversations,
    ActivityLog activity,
    PresenceTracker presence,
    IConnectivity connectivity,
    ISettingsStore settings,
    IEventBus events,
    PlatformInfo platform,
    ILogger<AgentOrchestrator> logger)
{
    private const int MaxHistoryMessages = 40;
    private readonly ConcurrentDictionary<string, ConversationContext> _contexts = new();
    private string _activeConversationId = NewId();

    public string ActiveConversationId => _activeConversationId;

    /// <summary>Start a fresh conversation context (UI "new chat").</summary>
    public string NewConversation()
    {
        _activeConversationId = NewId();
        return _activeConversationId;
    }

    public async Task<AgentTurnResult> HandleAsync(UserInput input, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var s = settings.Current;
        var conv = Resolve(input.ConversationId, s);
        var text = input.Text.Trim();
        var lang = DetermineLang(text, conv, s);
        conv.LastLang = lang;
        var phr = new ToolCtx(lang, lang == Lang.Ar ? s.General.HonorificAr : s.General.Honorific);
        var toolCtx = new ToolContext { Lang = lang, Settings = s, ConversationId = conv.Id, CancellationToken = ct };

        events.Publish(EventTypes.TurnStarted, new { conversationId = conv.Id, text, source = input.Source });
        if (s.Memory.StoreConversations)
            conversations.Append(conv.Id, "user", text, LangCode(lang), input.Source.ToString().ToLowerInvariant());

        AgentTurnResult result;
        try
        {
            result = await HandleCoreAsync(text, input, conv, lang, phr, toolCtx, s, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = Result(conv, lang, phr.T("Cancelled.", "اتلغى."), "none", input.Source, success: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent turn failed");
            activity.Record(ActivityKinds.Error, $"Request failed: {Trim(text, 120)}", details: ex.ToString(), conversationId: conv.Id);
            result = Result(conv, lang,
                phr.T($"Sorry{phr.CommaSir}, something went wrong on my side: {ex.Message}", $"معلش{phr.CommaSir}، حصلت مشكلة عندي: {ex.Message}"),
                "none", input.Source, success: false);
        }

        result = result with { DurationMs = sw.ElapsedMilliseconds };
        conv.LastActivity = DateTimeOffset.Now;
        if (s.Memory.StoreConversations)
        {
            var meta = JsonSerializer.Serialize(new { result.Route, result.Model, Steps = result.Steps.Select(s => s with { Data = null }) }, JsonOpts);
            conversations.Append(conv.Id, "assistant", result.Reply, result.Lang, input.Source.ToString().ToLowerInvariant(), meta);
        }
        activity.Record(ActivityKinds.Request, Trim(text, 200), status: result.Success ? "ok" : "failed",
            details: $"route={result.Route}{(result.Model is null ? "" : $" model={result.Model}")} steps={result.Steps.Count}",
            conversationId: conv.Id, durationMs: result.DurationMs);
        events.Publish(EventTypes.TurnCompleted, result);
        return result;
    }

    private async Task<AgentTurnResult> HandleCoreAsync(string text, UserInput input, ConversationContext conv, Lang lang,
        ToolCtx phr, ToolContext toolCtx, JarvisSettings s, CancellationToken ct)
    {
        var pendingHere = approvals.PendingFor(conv.Id);
        var intent = IntentEngine.Match(text, DateTimeOffset.Now, approvalPending: approvals.Pending.Count > 0);

        switch (intent)
        {
            case ApprovalAnswerIntent answer:
            {
                // Prefer approvals from this conversation, else the oldest pending one (e.g. voice answering a UI request).
                var target = pendingHere.FirstOrDefault() ?? approvals.Pending.FirstOrDefault();
                var reply = target is not null && approvals.Resolve(target.Id, answer.Approve)
                    ? Persona.ApprovalAnswered(phr, answer.Approve)
                    : Persona.NothingPending(phr);
                return Remember(conv, text, Result(conv, lang, reply, "deterministic", input.Source));
            }
            case LanguageIntent li:
            {
                conv.ForcedLang = li.Lang;
                var p2 = new ToolCtx(li.Lang, li.Lang == Lang.Ar ? s.General.HonorificAr : s.General.Honorific);
                return Remember(conv, text, Result(conv, li.Lang, Persona.LanguageSwitched(p2), "deterministic", input.Source));
            }
            case ReplyIntent ri:
            {
                var now = DateTimeOffset.Now;
                var reply = ri.Kind switch
                {
                    "greeting" => Persona.Greeting(phr, now),
                    "help" => Persona.Help(phr),
                    "presence" => Persona.Presence(phr),
                    "time" => Persona.Time(phr, now),
                    "date" => Persona.Date(phr, now),
                    _ => Persona.Help(phr),
                };
                return Remember(conv, text, Result(conv, lang, reply, "deterministic", input.Source));
            }
            case ToolIntent { PreferAi: true } preferAi:
            {
                var aiRoute = await router.RouteAsync(text, ct).ConfigureAwait(false);
                if (aiRoute.HasModel)
                    return await RunAiAsync(text, input, conv, lang, phr, toolCtx, s, aiRoute, ct).ConfigureAwait(false);
                var (r, st) = await executor.ExecuteAsync(preferAi.Tool, preferAi.Args, toolCtx).ConfigureAwait(false);
                // The build failing is the answer to "why is it failing", not an error of JARVIS.
                return Remember(conv, text, Result(conv, lang, r.Message, "deterministic", input.Source, [st], r.Status is not (ToolStatus.NotFound or ToolStatus.Denied)));
            }
            case ToolIntent ti:
            {
                var (res, step) = await executor.ExecuteAsync(ti.Tool, ti.Args, toolCtx).ConfigureAwait(false);
                var canFallBack = ti.FallBackToAiOnFailure && !res.Success &&
                                  res.Status is ToolStatus.Failed or ToolStatus.NotFound;
                if (!canFallBack)
                    return Remember(conv, text, Result(conv, lang, res.Message, "deterministic", input.Source, [step], res.Success));

                var decision = await router.RouteAsync(text, ct).ConfigureAwait(false);
                if (!decision.HasModel)
                    return Remember(conv, text, Result(conv, lang, res.Message, "deterministic", input.Source, [step], false));
                logger.LogInformation("Deterministic {Tool} failed; handing over to AI", ti.Tool);
                return await RunAiAsync(text, input, conv, lang, phr, toolCtx, s, decision, ct).ConfigureAwait(false);
            }
        }

        var route = await router.RouteAsync(text, ct).ConfigureAwait(false);
        if (!route.HasModel)
            return Remember(conv, text, Result(conv, lang, Persona.NoModel(phr, route.Reason), "none", input.Source, success: false));

        return await RunAiAsync(text, input, conv, lang, phr, toolCtx, s, route, ct).ConfigureAwait(false);
    }

    private async Task<AgentTurnResult> RunAiAsync(string text, UserInput input, ConversationContext conv, Lang lang,
        ToolCtx phr, ToolContext toolCtx, JarvisSettings s, RouteDecision route, CancellationToken ct)
    {
        var relevant = s.Memory.Enabled ? RelevantMemories(text) : [];
        if (relevant.Count > 0) memory.MarkUsed(relevant.Select(m => m.Id));
        var system = Persona.SystemPrompt(s, lang, input.Source == InputSource.Voice, connectivity.IsOnline,
            presence.Current, relevant, tasks.List().Take(8).ToList(), platform.Description);
        var available = tools.AvailableFor(s);

        await conv.HistoryLock.WaitAsync(ct).ConfigureAwait(false);
        List<ChatMessage> working;
        try
        {
            conv.History.Add(ChatMessage.User(text));
            Prune(conv.History);
            working = [ChatMessage.System(system), .. conv.History];
        }
        finally { conv.HistoryLock.Release(); }

        var steps = new List<ToolStep>();
        var provider = route.Provider!;
        var model = route.Model!;
        var added = new List<ChatMessage>();

        for (var round = 0; round < s.Ai.MaxAgentSteps; round++)
        {
            ChatResponse response;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(s.Ai.RequestTimeoutSeconds));
            try
            {
                response = await provider.CompleteAsync(new ChatRequest
                {
                    Model = model,
                    Messages = working,
                    Tools = available,
                    MaxTokens = input.Source == InputSource.Voice ? 1024 : 4096,
                }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AiProviderException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                var msg = ex is AiProviderException ? ex.Message : "timed out";
                activity.Record(ActivityKinds.Ai, $"Model call failed: {provider.Id}/{model}", status: "failed", details: ex.Message, conversationId: conv.Id);
                Commit(conv, added);
                return Result(conv, lang, Persona.ModelFailed(phr, msg), "ai", input.Source, steps, false, route.Label);
            }

            var assistant = ChatMessage.Assistant(response.Content, response.ToolCalls.Count > 0 ? response.ToolCalls : null) with
            {
                ProviderData = response.ProviderData,
            };
            working.Add(assistant);
            added.Add(assistant);

            if (response.ToolCalls.Count == 0)
            {
                Commit(conv, added);
                var reply = string.IsNullOrWhiteSpace(response.Content)
                    ? phr.T("Done.", "تمام.")
                    : response.Content.Trim();
                return Result(conv, lang, reply, "ai", input.Source, steps, steps.All(x => x.Status == ToolStatus.Ok), route.Label);
            }

            foreach (var call in response.ToolCalls)
            {
                ToolResult toolResult;
                ToolStep step;
                try
                {
                    (toolResult, step) = await executor.ExecuteAsync(call.Name, ToolArgs.Parse(call.ArgumentsJson), toolCtx).ConfigureAwait(false);
                }
                catch (ToolArgumentException ex)
                {
                    toolResult = ToolResult.Fail(ex.Message);
                    step = new ToolStep(call.Name, call.Name, RiskLevel.Safe, ToolStatus.Failed, ex.Message, 0, null);
                }
                steps.Add(step);
                var toolMessage = ChatMessage.ToolResult(call.Id, call.Name, SerializeForModel(toolResult), !toolResult.Success);
                working.Add(toolMessage);
                added.Add(toolMessage);
            }
        }

        Commit(conv, added);
        return Result(conv, lang, Persona.StepLimit(phr, s.Ai.MaxAgentSteps), "ai", input.Source, steps, false, route.Label);
    }

    /// <summary>Recent conversation history for the UI.</summary>
    public IReadOnlyList<ChatMessage> History(string conversationId) =>
        _contexts.TryGetValue(conversationId, out var c) ? c.History.ToList() : [];

    private IReadOnlyList<MemoryItem> RelevantMemories(string text)
    {
        var found = memory.Search(text, 6).ToList();
        // Confirmed preferences are always relevant to how JARVIS behaves.
        foreach (var p in memory.List(MemoryKinds.Preference, 6))
            if (found.All(f => f.Id != p.Id)) found.Add(p);
        return found.Take(12).ToList();
    }

    private ConversationContext Resolve(string? requestedId, JarvisSettings s)
    {
        var id = string.IsNullOrWhiteSpace(requestedId) ? _activeConversationId : requestedId;
        var ctx = _contexts.GetOrAdd(id, i => new ConversationContext(i));
        var idle = DateTimeOffset.Now - ctx.LastActivity;
        if (string.IsNullOrWhiteSpace(requestedId) && ctx.History.Count > 0 && idle > TimeSpan.FromMinutes(s.General.ConversationTimeoutMinutes))
        {
            // The shared default conversation went quiet: start a new one rather than dragging stale context along.
            _activeConversationId = NewId();
            ctx = _contexts.GetOrAdd(_activeConversationId, i => new ConversationContext(i) { ForcedLang = ctx.ForcedLang });
        }
        return ctx;
    }

    private static Lang DetermineLang(string text, ConversationContext conv, JarvisSettings s)
    {
        if (s.General.Language == "en") return Lang.En;
        if (s.General.Language == "ar") return Lang.Ar;
        if (conv.ForcedLang is { } forced)
        {
            // An explicit switch sticks until the user clearly writes in the other script.
            var detected = LanguageDetector.Detect(text, forced);
            if (detected != forced && IntentEngine.Match(text, DateTimeOffset.Now) is LanguageIntent) return detected;
            return forced;
        }
        return LanguageDetector.Detect(text, conv.LastLang);
    }

    /// <summary>Keep deterministic exchanges in the AI's short-term history so follow-ups make sense.</summary>
    private AgentTurnResult Remember(ConversationContext conv, string userText, AgentTurnResult result)
    {
        conv.HistoryLock.Wait();
        try
        {
            conv.History.Add(ChatMessage.User(userText));
            conv.History.Add(ChatMessage.Assistant(result.Reply));
            Prune(conv.History);
        }
        finally { conv.HistoryLock.Release(); }
        return result;
    }

    private static void Commit(ConversationContext conv, List<ChatMessage> added)
    {
        conv.HistoryLock.Wait();
        try
        {
            conv.History.AddRange(added);
            Prune(conv.History);
        }
        finally { conv.HistoryLock.Release(); }
    }

    /// <summary>Drop old messages without orphaning tool results from their tool calls.</summary>
    private static void Prune(List<ChatMessage> history)
    {
        while (history.Count > MaxHistoryMessages)
        {
            history.RemoveAt(0);
            while (history.Count > 0 && (history[0].Role == ChatRole.Tool || history[0].Role == ChatRole.Assistant))
                history.RemoveAt(0);
        }
    }

    private static string SerializeForModel(ToolResult r)
    {
        string? data = null;
        if (r.Data is not null)
        {
            data = JsonSerializer.Serialize(r.Data, JsonOpts);
            if (data.Length > 8000) data = data[..8000] + "…(truncated)";
        }
        return JsonSerializer.Serialize(new { success = r.Success, status = r.Status.ToString(), message = r.Message, data }, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static AgentTurnResult Result(ConversationContext conv, Lang lang, string reply, string route, InputSource source,
        IReadOnlyList<ToolStep>? steps = null, bool success = true, string? model = null) => new()
    {
        ConversationId = conv.Id,
        Reply = reply,
        Lang = LangCode(lang),
        Route = route,
        Model = model,
        Steps = steps ?? [],
        Success = success,
        Source = source,
    };

    private static string LangCode(Lang l) => l == Lang.Ar ? "ar" : "en";
    private static string NewId() => Guid.NewGuid().ToString("n")[..12];
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
