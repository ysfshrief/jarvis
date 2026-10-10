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

/// <param name="ConfirmChanges">Set when it isn't certain the user meant JARVIS (e.g. speech heard in the follow-up
/// window without the wake word): anything sensitive it leads to is confirmed first, with this reason.</param>
public sealed record UserInput(string Text, string? ConversationId = null, InputSource Source = InputSource.Text, string? ConfirmChanges = null);

public sealed record AgentTurnResult
{
    public required string ConversationId { get; init; }
    public string TurnId { get; init; } = "";
    public required string Reply { get; init; }
    public required string Lang { get; init; }
    /// <summary>"deterministic", "ai", or "none" (no model available).</summary>
    public required string Route { get; init; }
    public string? Model { get; init; }
    public IReadOnlyList<ToolStep> Steps { get; init; } = [];
    public bool Success { get; init; } = true;
    public long DurationMs { get; init; }
    public InputSource Source { get; init; }
    /// <summary>Set when the first model failed and another one answered.</summary>
    public string? FallbackFrom { get; init; }
    /// <summary>True when older turns were condensed to fit the model's context window.</summary>
    public bool ContextTrimmed { get; init; }
    /// <summary>Memories given to the model for this answer, so the UI can show why it knew something.</summary>
    public IReadOnlyList<UsedMemory> UsedMemories { get; init; } = [];
}

public sealed record UsedMemory(string Id, string Kind, string Source, string Content);

public static class TurnPhases
{
    public const string Understanding = "understanding";
    public const string Analyzing = "analyzing";
    public const string SelectingTool = "selecting_tool";
    public const string Executing = "executing";
    public const string Completed = "completed";
    public const string Failed = "failed";
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
    KnowledgeService knowledge,
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
    private const int MaxHistoryMessages = 60;
    private const int MaxFallbacks = 2;
    /// <summary>Extra seconds for a local model to load into memory (several GB from disk) on top of the usual limit.</summary>
    private const int ModelLoadSeconds = 300;
    /// <summary>One "second" of the AI time limits; tests shrink it to exercise timeouts quickly.</summary>
    internal TimeSpan TimeoutUnit { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Context assumed for cloud models; they're far larger, this just bounds cost.</summary>
    private const int CloudContextTokens = 100_000;
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
        var turnId = NewId();
        var toolCtx = new ToolContext
        {
            Lang = lang, Settings = s, ConversationId = conv.Id, TurnId = turnId, CancellationToken = ct,
            RequestText = text, Via = input.Source.ToString().ToLowerInvariant(),
        };
        toolCtx.Turn.ConfirmChanges = input.ConfirmChanges;

        events.Publish(EventTypes.TurnStarted, new { conversationId = conv.Id, turnId, text, source = input.Source });
        Phase(toolCtx, TurnPhases.Understanding);
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

        result = result with { DurationMs = sw.ElapsedMilliseconds, TurnId = turnId };
        Phase(toolCtx, result.Success ? TurnPhases.Completed : TurnPhases.Failed);
        conv.LastActivity = DateTimeOffset.Now;
        if (s.Memory.StoreConversations)
        {
            var meta = JsonSerializer.Serialize(new { result.Route, result.Model, Steps = result.Steps.Select(s => s with { Data = null }), result.UsedMemories, result.FallbackFrom }, JsonOpts);
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
                    "name" => Persona.Name(phr, s.General.UserName),
                    _ => Persona.Help(phr),
                };
                return Remember(conv, text, Result(conv, lang, reply, "deterministic", input.Source));
            }
            case ToolIntent { PreferAi: true } preferAi:
            {
                Phase(toolCtx, TurnPhases.Analyzing);
                var aiRoute = await router.RouteAsync(text, ct).ConfigureAwait(false);
                // The tool runs first either way, so the model answers from what was really read or
                // observed (a small model asked to "summarise X" would otherwise happily invent it).
                Phase(toolCtx, TurnPhases.Executing, preferAi.Tool);
                var (r, st) = await executor.ExecuteAsync(preferAi.Tool, preferAi.Args, toolCtx).ConfigureAwait(false);
                if (aiRoute.HasModel && r.Status is not (ToolStatus.NotFound or ToolStatus.Denied))
                    return await RunAiAsync(text, input, conv, lang, phr, toolCtx, s, aiRoute, ct, new Grounding(preferAi.Tool, preferAi.Args, r, st)).ConfigureAwait(false);
                // The build failing is the answer to "why is it failing", not an error of JARVIS.
                return Remember(conv, text, Result(conv, lang, r.Message, "deterministic", input.Source, [st], r.Status is not (ToolStatus.NotFound or ToolStatus.Denied)));
            }
            case ToolIntent ti:
            {
                Phase(toolCtx, TurnPhases.Executing, ti.Tool);
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

        Phase(toolCtx, TurnPhases.Analyzing);
        var route = await router.RouteAsync(text, ct).ConfigureAwait(false);
        if (!route.HasModel)
            return Remember(conv, text, Result(conv, lang, Persona.NoModel(phr, route.Reason), "none", input.Source, success: false));

        return await RunAiAsync(text, input, conv, lang, phr, toolCtx, s, route, ct).ConfigureAwait(false);
    }

    private async Task<AgentTurnResult> RunAiAsync(string text, UserInput input, ConversationContext conv, Lang lang,
        ToolCtx phr, ToolContext toolCtx, JarvisSettings s, RouteDecision route, CancellationToken ct, Grounding? grounding = null)
    {
        var relevant = s.Memory.Enabled ? await RelevantMemoriesAsync(text, ct).ConfigureAwait(false) : [];
        if (relevant.Count > 0) memory.MarkUsed(relevant.Select(m => m.Id));
        var used = relevant.Select(m => new UsedMemory(m.Id, m.Kind, m.Source, m.Content.Length > 160 ? m.Content[..159] + "…" : m.Content)).ToList();
        var system = Persona.SystemPrompt(s, platform.Description);
        var openTasks = tasks.List().Take(8).ToList();

        await conv.HistoryLock.WaitAsync(ct).ConfigureAwait(false);
        List<ChatMessage> history;
        try
        {
            conv.History.Add(ChatMessage.User(text));
            Prune(conv.History);
            history = [.. conv.History];
        }
        finally { conv.HistoryLock.Release(); }

        var steps = new List<ToolStep>();
        var added = new List<ChatMessage>();
        List<ChatMessage> grounded = [];
        string? groundingNote = null;
        if (grounding is not null)
        {
            // Tool-capable models see it exactly as if they had called the tool; others get it as context.
            var call = new ToolCall($"call_{Guid.NewGuid():n}", grounding.Tool, grounding.Args.ToString());
            var content = SerializeForModel(grounding.Result);
            grounded = [ChatMessage.Assistant(null, [call]), ChatMessage.ToolResult(call.Id, call.Name, content, !grounding.Result.Success)];
            groundingNote = $"Already done for this request — {grounding.Tool} returned (answer from this, do not invent anything else):\n{content}";
            steps.Add(grounding.Step);
        }
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? fallbackFrom = null;
        var trimmedAny = false;
        var spoken = input.Source == InputSource.Voice;
        Phase(toolCtx, TurnPhases.Analyzing, route.Label);

        var rounds = 0;
        while (rounds < s.Ai.MaxAgentSteps)
        {
            var provider = route.Provider!;
            var model = route.Model!;
            // Models that can't call tools still converse; deterministic commands cover the actions.
            var available = route.SupportsTools ? ToolSelector.Select(text, tools.AvailableFor(s), compact: provider.IsLocal) : [];
            var sys = route.SupportsTools ? system : system + "\n" + Persona.NoToolsNote;
            // What changes every message (time, memories, tasks, language) rides with the user's message, so the
            // system prompt and tool list stay identical and a local model can reuse its work on them.
            var context = Persona.TurnContext(lang, spoken, connectivity.IsOnline, presence.Current, relevant, openTasks,
                route.SupportsTools ? null : groundingNote);
            var current = history.FindLastIndex(m => m.Role == ChatRole.User);
            List<ChatMessage> turn = [.. history];
            if (current >= 0) turn[current] = turn[current] with { Content = Persona.WithContext(context, turn[current].Content ?? "") };
            var contextTokens = provider.IsLocal
                ? Math.Min(s.Ai.LocalContextTokens, route.Info?.ContextLength ?? int.MaxValue)
                : CloudContextTokens;
            var maxTokens = provider.IsLocal ? (spoken ? 512 : 2048) : (spoken ? 1024 : 4096);
            var fitted = ContextBudget.Fit(sys, route.SupportsTools ? [.. turn, .. grounded, .. added] : [.. turn, .. added], available, contextTokens, Math.Min(maxTokens, contextTokens / 4));
            trimmedAny |= fitted.Trimmed;

            var stream = s.Ai.StreamResponses ? new DeltaStream(events, toolCtx, rounds) : null;
            ChatResponse response;
            var limit = TimeoutUnit * s.Ai.RequestTimeoutSeconds;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var loading = false;
            try
            {
                // A model that isn't in memory yet is loaded first, as its own step with its own allowance: reading
                // gigabytes from disk isn't the model failing to answer.
                if (provider is IModelLoader loader && !await loader.IsLoadedAsync(model, ct).ConfigureAwait(false))
                {
                    loading = true;
                    Phase(toolCtx, TurnPhases.Analyzing, phr.T($"loading {model} into memory", $"بحمّل {model} في الذاكرة"));
                    timeout.CancelAfter(TimeoutUnit * (s.Ai.RequestTimeoutSeconds + ModelLoadSeconds));
                    await loader.LoadAsync(model, contextTokens, timeout.Token).ConfigureAwait(false);
                    loading = false;
                    Phase(toolCtx, TurnPhases.Analyzing, route.Label);
                }
                // The limit is on silence, not on the whole answer: everything the model streams restarts it, so a slow
                // PC that is still writing isn't cut off mid-reply.
                timeout.CancelAfter(limit);
                response = await provider.CompleteAsync(new ChatRequest
                {
                    Model = model,
                    Messages = fitted.Messages,
                    Tools = available,
                    MaxTokens = maxTokens,
                    ContextTokens = provider.IsLocal ? contextTokens : null,
                    OnTextDelta = stream is null ? null : stream.Add,
                    OnProgress = () => timeout.CancelAfter(limit),
                }, timeout.Token).ConfigureAwait(false);
                stream?.Flush();
            }
            catch (Exception ex) when (ex is AiProviderException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                stream?.Reset();
                var timedOut = ex is OperationCanceledException;
                var msg = ex is AiProviderException ? ex.Message : loading ? "timed out loading the model" : "timed out";
                activity.Record(ActivityKinds.Ai, $"Model call failed: {route.Label}", status: "failed", details: timedOut ? msg : ex.Message, conversationId: conv.Id);
                failed.Add(route.Key);
                if (failed.Count <= MaxFallbacks)
                {
                    await router.ReportFailureAsync(route, ct).ConfigureAwait(false);
                    var next = await router.RouteAsync(text, ct, route.Role, failed).ConfigureAwait(false);
                    // A local model that ran out of time means this PC is too busy or slow for it: loading another local
                    // model would push the first out of memory and take even longer. Other providers are still tried.
                    if (next.HasModel && !(timedOut && provider.IsLocal && next.Provider?.Id == provider.Id))
                    {
                        logger.LogWarning("Model {Failed} failed ({Error}); falling back to {Next}", route.Label, msg, next.Label);
                        activity.Record(ActivityKinds.Ai, $"Switched to {next.Label} after {route.Label} failed", conversationId: conv.Id);
                        fallbackFrom ??= route.Label;
                        route = next;
                        Phase(toolCtx, TurnPhases.Analyzing, route.Label);
                        continue;
                    }
                }
                Commit(conv, added);
                var failure = timedOut && provider.IsLocal
                    ? Persona.ModelTooSlow(phr, model, s.Ai.RequestTimeoutSeconds + (loading ? ModelLoadSeconds : 0), loading,
                        suggestSmaller: (ModelRouter.ParseBillions(route.Info?.ParameterSize) ?? ModelRouter.ParseBillions(model.Split(':').ElementAtOrDefault(1)) ?? 8) > 4)
                    : Persona.ModelFailed(phr, msg);
                return Result(conv, lang, failure, "ai", input.Source, steps, false, route.Label) with
                {
                    FallbackFrom = fallbackFrom,
                    ContextTrimmed = trimmedAny,
                    UsedMemories = used,
                };
            }

            rounds++;
            var assistant = ChatMessage.Assistant(response.Content, response.ToolCalls.Count > 0 ? response.ToolCalls : null) with
            {
                ProviderData = response.ProviderData,
            };
            added.Add(assistant);

            if (response.ToolCalls.Count == 0)
            {
                Commit(conv, added);
                var reply = string.IsNullOrWhiteSpace(response.Content)
                    ? phr.T("Done.", "تمام.")
                    : response.Content.Trim();
                return Result(conv, lang, reply, "ai", input.Source, steps, steps.All(x => x.Status == ToolStatus.Ok), route.Label) with
                {
                    FallbackFrom = fallbackFrom,
                    ContextTrimmed = trimmedAny,
                    UsedMemories = used,
                };
            }

            foreach (var call in response.ToolCalls)
            {
                Phase(toolCtx, TurnPhases.SelectingTool, call.Name);
                ToolResult toolResult;
                ToolStep step;
                try
                {
                    var args = ToolArgs.Parse(call.ArgumentsJson);
                    Phase(toolCtx, TurnPhases.Executing, call.Name);
                    (toolResult, step) = await executor.ExecuteAsync(call.Name, args, toolCtx).ConfigureAwait(false);
                }
                catch (ToolArgumentException ex)
                {
                    toolResult = ToolResult.Fail(ex.Message);
                    step = new ToolStep(call.Name, call.Name, RiskLevel.Safe, ToolStatus.Failed, ex.Message, 0, null);
                }
                steps.Add(step);
                added.Add(ChatMessage.ToolResult(call.Id, call.Name, SerializeForModel(toolResult), !toolResult.Success));
            }
            Phase(toolCtx, TurnPhases.Analyzing, route.Label);
        }

        Commit(conv, added);
        return Result(conv, lang, Persona.StepLimit(phr, s.Ai.MaxAgentSteps), "ai", input.Source, steps, false, route.Label) with
        {
            FallbackFrom = fallbackFrom,
            ContextTrimmed = trimmedAny,
            UsedMemories = used,
        };
    }

    /// <summary>A tool already run for this request, whose result the model must answer from.</summary>
    private sealed record Grounding(string Tool, ToolArgs Args, ToolResult Result, ToolStep Step);

    private void Phase(ToolContext ctx, string phase, string? detail = null) =>
        events.Publish(EventTypes.TurnPhase, new { conversationId = ctx.ConversationId, turnId = ctx.TurnId, phase, detail });

    /// <summary>
    /// Batches streamed text into a few events per second; one WebSocket message per token would
    /// make the UI re-render far more than the eye can follow.
    /// </summary>
    private sealed class DeltaStream(IEventBus events, ToolContext ctx, int round)
    {
        private readonly System.Text.StringBuilder _pending = new();
        private readonly Stopwatch _since = Stopwatch.StartNew();
        private readonly object _gate = new();

        public void Add(string piece)
        {
            lock (_gate)
            {
                _pending.Append(piece);
                if (_since.ElapsedMilliseconds >= 60 || _pending.Length >= 48) FlushLocked();
            }
        }

        public void Flush() { lock (_gate) FlushLocked(); }

        /// <summary>The model failed mid-answer: tell the UI to drop what it showed.</summary>
        public void Reset()
        {
            lock (_gate) _pending.Clear();
            events.Publish(EventTypes.TurnDelta, new { conversationId = ctx.ConversationId, turnId = ctx.TurnId, round, text = "", reset = true });
        }

        private void FlushLocked()
        {
            if (_pending.Length == 0) return;
            events.Publish(EventTypes.TurnDelta, new { conversationId = ctx.ConversationId, turnId = ctx.TurnId, round, text = _pending.ToString(), reset = false });
            _pending.Clear();
            _since.Restart();
        }
    }

    /// <summary>Recent conversation history for the UI.</summary>
    public IReadOnlyList<ChatMessage> History(string conversationId) =>
        _contexts.TryGetValue(conversationId, out var c) ? c.History.ToList() : [];

    private async Task<IReadOnlyList<MemoryItem>> RelevantMemoriesAsync(string text, CancellationToken ct)
    {
        // Keyword + meaning + everything linked to people/things named in the request.
        var found = (await knowledge.RecallAsync(text, 6, ct).ConfigureAwait(false)).Select(h => h.Memory).ToList();
        // Confirmed preferences are always relevant to how JARVIS behaves.
        foreach (var p in memory.List(MemoryKinds.Preference, 6))
            if (found.All(f => f.Id != p.Id)) found.Add(p);
        return found.Take(12).ToList();
    }

    private ConversationContext Resolve(string? requestedId, JarvisSettings s)
    {
        var id = string.IsNullOrWhiteSpace(requestedId) ? _activeConversationId : requestedId;
        var ctx = _contexts.GetOrAdd(id, i => Restore(i, s));
        var idle = DateTimeOffset.Now - ctx.LastActivity;
        if (string.IsNullOrWhiteSpace(requestedId) && ctx.History.Count > 0 && idle > TimeSpan.FromMinutes(s.General.ConversationTimeoutMinutes))
        {
            // The shared default conversation went quiet: start a new one rather than dragging stale context along.
            _activeConversationId = NewId();
            ctx = _contexts.GetOrAdd(_activeConversationId, i => new ConversationContext(i) { ForcedLang = ctx.ForcedLang });
        }
        return ctx;
    }

    /// <summary>
    /// After a restart the in-memory context is gone; continue a stored conversation from its
    /// recent text turns (tool details aren't replayed — they may be stale).
    /// </summary>
    private ConversationContext Restore(string id, JarvisSettings s)
    {
        var ctx = new ConversationContext(id);
        if (!s.Memory.StoreConversations) return ctx;
        try
        {
            var stored = conversations.Messages(id, 30);
            foreach (var m in stored.TakeLast(20))
            {
                if (m.Role == "user") ctx.History.Add(ChatMessage.User(m.Content));
                else if (m.Role == "assistant") ctx.History.Add(ChatMessage.Assistant(m.Content));
            }
            while (ctx.History.Count > 0 && ctx.History[0].Role != ChatRole.User) ctx.History.RemoveAt(0);
            if (stored.Count > 0)
            {
                ctx.LastActivity = stored[^1].CreatedAt;
                ctx.LastLang = stored[^1].Lang == "ar" ? Lang.Ar : Lang.En;
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Couldn't restore conversation {Id}", id); }
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
