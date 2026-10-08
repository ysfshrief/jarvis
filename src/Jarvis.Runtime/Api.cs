using System.Globalization;
using System.Text.Json;
using Jarvis.Core;
using Jarvis.Core.Activity;
using Jarvis.Core.Agent;
using Jarvis.Core.AI;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Memory;
using Jarvis.Core.Notifications;
using Jarvis.Core.Permissions;
using Jarvis.Core.Presence;
using Jarvis.Core.Scheduling;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Jarvis.Core.Tasks;
using Jarvis.Core.Tools;
using Jarvis.Core.Voice;
using Jarvis.Voice;

namespace Jarvis.Runtime;

public sealed record ChatRequestDto(string Text, string? ConversationId, string? Source);
public sealed record ApprovalDecisionDto(bool Approve, bool Remember = false);
public sealed record MemoryDto(string Content, string? Kind, string? Subject, string? Source, double? Confidence, string? Tags);
public sealed record TaskDto(string? Title, string? Notes, string? State, string? Priority, string? Project, DateTimeOffset? DueAt, bool ClearDue = false);
public sealed record ReminderDto(string Text, DateTimeOffset? DueAt, double? InMinutes);
public sealed record SecretDto(string Value);
public sealed record PinDto(string? CurrentPin, string? NewPin);
public sealed record UnlockDto(string Pin);
public sealed record PolicyDto(ToolPolicy Policy);
public sealed record SpeakDto(string Text, string? Lang);
public sealed record SimulateDto(bool Offline);
public sealed record PullDto(string? Model);

/// <summary>The local HTTP API. Same contract for the dashboard, the desktop shell and future companion apps.</summary>
public static class Api
{
    public static void MapJarvisApi(this WebApplication app, JsonSerializerOptions json)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok", version = RuntimeState.Version }));

        api.MapGet("/status", (IServiceProvider sp) => Results.Ok(Status(sp)));

        // ---- Auth / PIN lock ----
        api.MapGet("/auth/state", (RuntimeState rs) => Results.Ok(new { pinSet = rs.PinSet, locked = rs.Locked }));
        api.MapPost("/auth/unlock", (UnlockDto dto, RuntimeState rs, ActivityLog log) =>
        {
            if (rs.TryUnlock(dto.Pin)) return Results.Ok(new { locked = false });
            log.Record(ActivityKinds.System, "Wrong PIN entered", status: "denied");
            return Results.Json(new { error = "Wrong PIN." }, statusCode: 403);
        });
        api.MapPost("/auth/lock", (RuntimeState rs) => { rs.Lock(); return Results.Ok(new { locked = rs.Locked }); });
        api.MapPut("/auth/pin", (PinDto dto, RuntimeState rs, ISettingsStore settings, ActivityLog log) =>
        {
            if (rs.PinSet && (dto.CurrentPin is null || !PinHasher.Verify(dto.CurrentPin, settings.Current.Security.PinHash)))
                return Results.Json(new { error = "Current PIN is wrong." }, statusCode: 403);
            if (!string.IsNullOrEmpty(dto.NewPin) && (dto.NewPin.Length < 4 || dto.NewPin.Length > 32))
                return Results.BadRequest(new { error = "PIN must be 4–32 characters." });
            settings.Update(s => s.Security.PinHash = string.IsNullOrEmpty(dto.NewPin) ? "" : PinHasher.Hash(dto.NewPin));
            rs.Touch();
            log.Record(ActivityKinds.System, string.IsNullOrEmpty(dto.NewPin) ? "PIN removed" : "PIN set", status: "ok");
            return Results.Ok(new { pinSet = rs.PinSet });
        });

        // ---- Conversation ----
        api.MapPost("/chat", async (ChatRequestDto dto, AgentOrchestrator agent, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Text)) return Results.BadRequest(new { error = "Text is empty." });
            if (dto.Text.Length > 20_000) return Results.BadRequest(new { error = "Message is too long." });
            var source = Enum.TryParse<InputSource>(dto.Source, true, out var s) ? s : InputSource.Text;
            // Don't tie the turn to the HTTP request: if the UI reloads mid-approval, the action still completes.
            var result = await agent.HandleAsync(new UserInput(dto.Text, dto.ConversationId, source), CancellationToken.None).WaitAsync(ct);
            return Results.Ok(result);
        });
        api.MapGet("/conversations", (ConversationStore store, AgentOrchestrator agent) =>
            Results.Ok(new { active = agent.ActiveConversationId, conversations = store.Recent() }));
        api.MapGet("/conversations/{id}/messages", (string id, ConversationStore store) => Results.Ok(store.Messages(id)));
        api.MapPost("/conversations/new", (AgentOrchestrator agent) => Results.Ok(new { id = agent.NewConversation() }));
        api.MapDelete("/conversations", (ConversationStore store, ActivityLog log) =>
        {
            var n = store.DeleteAll();
            log.Record(ActivityKinds.Memory, $"Deleted all conversation history ({n})", status: "ok");
            return Results.Ok(new { deleted = n });
        });

        // ---- Approvals ----
        api.MapGet("/approvals", (ApprovalBroker broker) => Results.Ok(broker.Pending));
        api.MapPost("/approvals/{id}", (string id, ApprovalDecisionDto dto, ApprovalBroker broker, ISettingsStore settings) =>
        {
            var req = broker.Pending.FirstOrDefault(p => p.Id == id);
            if (req is null) return Results.NotFound(new { error = "That approval is no longer pending." });
            if (dto.Approve && dto.Remember && req.Risk != RiskLevel.Critical)
                settings.Update(s => s.Permissions.ToolOverrides[req.Tool] = ToolPolicy.Allow);
            broker.Resolve(id, dto.Approve);
            return Results.Ok(new { id, approved = dto.Approve });
        });

        // ---- Activity ----
        api.MapGet("/activity", (ActivityLog log, int? limit, string? kind, string? status) =>
            Results.Ok(log.Recent(limit ?? 100, kind, status)));

        // ---- Memory ----
        api.MapGet("/memory", (MemoryStore store, string? q, string? kind, int? limit) =>
            Results.Ok(string.IsNullOrWhiteSpace(q) ? store.List(kind, limit ?? 200) : store.Search(q, limit ?? 50, kind)));
        api.MapPost("/memory", (MemoryDto dto, MemoryStore store) =>
            Guard(() => store.Add(new NewMemory(dto.Content, dto.Kind ?? MemoryKinds.Fact, dto.Subject, dto.Source ?? MemorySources.UserExplicit, dto.Confidence, dto.Tags))));
        api.MapPut("/memory/{id}", (string id, MemoryDto dto, MemoryStore store) =>
            Guard(() => store.Update(id, new MemoryUpdate(dto.Content, dto.Kind, dto.Subject, dto.Source, dto.Confidence, dto.Tags))));
        api.MapDelete("/memory/{id}", (string id, MemoryStore store) =>
            store.Delete(id) ? Results.Ok(new { deleted = id }) : Results.NotFound());
        api.MapDelete("/memory", (MemoryStore store, ActivityLog log, bool? confirm, string? kind) =>
        {
            if (confirm != true) return Results.BadRequest(new { error = "Pass confirm=true to clear memory." });
            var n = store.Clear(kind);
            log.Record(ActivityKinds.Memory, $"Cleared {(kind ?? "all")} memory ({n} items)", status: "ok");
            return Results.Ok(new { deleted = n });
        });

        // ---- Tasks ----
        api.MapGet("/tasks", (TaskStore store, bool? all) => Results.Ok(store.List(all ?? false)));
        api.MapPost("/tasks", (TaskDto dto, TaskStore store) =>
            Guard(() => store.Create(new NewTask(dto.Title ?? "", dto.Notes, dto.Priority ?? "normal", dto.Project, dto.DueAt))));
        api.MapPut("/tasks/{id}", (string id, TaskDto dto, TaskStore store) =>
            Guard(() => store.Update(id, new TaskUpdate(dto.Title, dto.Notes, dto.State, dto.Priority, dto.Project, dto.DueAt, dto.ClearDue))));
        api.MapDelete("/tasks/{id}", (string id, TaskStore store) => store.Delete(id) ? Results.Ok(new { deleted = id }) : Results.NotFound());

        // ---- Reminders ----
        api.MapGet("/reminders", (ReminderStore store, bool? all) => Results.Ok(store.List(all ?? false)));
        api.MapPost("/reminders", (ReminderDto dto, ReminderStore store) =>
        {
            var due = dto.InMinutes is { } m ? DateTimeOffset.Now.AddMinutes(m) : dto.DueAt;
            if (due is null) return Results.BadRequest(new { error = "Give dueAt or inMinutes." });
            return Guard(() => store.Create(dto.Text, due.Value));
        });
        api.MapDelete("/reminders/{id}", (string id, ReminderStore store) => store.Cancel(id) ? Results.Ok(new { cancelled = id }) : Results.NotFound());

        // ---- Notifications ----
        api.MapGet("/notifications", (NotificationCenter center, int? limit, string? status) => Results.Ok(center.Recent(limit ?? 50, status)));
        api.MapPost("/notifications/{id}/read", (string id, NotificationCenter center) => { center.MarkRead(id); return Results.Ok(); });
        api.MapPost("/notifications/flush", async (NotificationCenter center) => Results.Ok(new { delivered = await center.FlushHeldAsync() }));
        api.MapPost("/notifications/test", async (NotificationCenter center) =>
            Results.Ok(await center.PostAsync(new Notification
            {
                Title = "This is a test notification from JARVIS.",
                Body = "If you can read this, notifications work.",
                Priority = NotificationPriority.Normal,
                Source = "test",
                GroupKey = $"test:{Guid.NewGuid()}",
            })));

        // ---- Offline queue ----
        api.MapGet("/queue", (OfflineQueue queue) => Results.Ok(queue.List()));
        api.MapPost("/queue/{id}/run", async (string id, OfflineQueue queue, ToolExecutor executor, ISettingsStore settings, IConnectivity conn) =>
        {
            var item = queue.Get(id);
            if (item is null || item.Status != "queued") return Results.NotFound();
            if (!conn.IsOnline) return Results.Json(new { error = "Still offline." }, statusCode: 409);
            var ctx = new ToolContext { Lang = item.Lang == "ar" ? Lang.Ar : Lang.En, Settings = settings.Current, ConversationId = item.ConversationId ?? "" };
            var (result, step) = await executor.ExecuteAsync(item.Tool, ToolArgs.Parse(item.Args), ctx, allowQueue: false);
            queue.SetStatus(id, result.Success ? "done" : "failed");
            return Results.Ok(new { result, step });
        });
        api.MapPost("/queue/{id}/discard", (string id, OfflineQueue queue) => { queue.SetStatus(id, "discarded"); return Results.Ok(); });

        // ---- Tools & permissions ----
        api.MapGet("/tools", (IToolRegistry tools, ISettingsStore settings) => Results.Ok(tools.All.Select(t => new
        {
            t.Definition.Name, t.Definition.Description, t.Definition.Category, Risk = t.Definition.Risk.ToString(),
            t.Definition.RequiresInternet,
            Parameters = t.Definition.Parameters,
            Policy = settings.Current.Permissions.ToolOverrides.GetValueOrDefault(t.Definition.Name, ToolPolicy.Default).ToString(),
        })));
        api.MapPut("/tools/{name}/policy", (string name, PolicyDto dto, IToolRegistry tools, ISettingsStore settings, ActivityLog log) =>
        {
            if (tools.Find(name) is null) return Results.NotFound();
            settings.Update(s =>
            {
                if (dto.Policy == ToolPolicy.Default) s.Permissions.ToolOverrides.Remove(name);
                else s.Permissions.ToolOverrides[name] = dto.Policy;
            });
            log.Record(ActivityKinds.System, $"Permission for {name} set to {dto.Policy}", tool: name, status: "ok");
            return Results.Ok();
        });

        // ---- Settings & secrets ----
        api.MapGet("/settings", (ISettingsStore settings) => Results.Ok(settings.Current));
        api.MapPut("/settings", (JarvisSettings incoming, ISettingsStore settings, IEventBus events, ActivityLog log) =>
        {
            // The PIN can only change through /auth/pin.
            incoming.Security ??= new SecuritySettings();
            incoming.Security.PinHash = settings.Current.Security.PinHash;
            settings.Replace(incoming);
            events.Publish(EventTypes.SettingsChanged, new { });
            log.Record(ActivityKinds.System, "Settings updated", status: "ok");
            return Results.Ok(settings.Current);
        });
        api.MapGet("/secrets", (ISecretStore secrets) => Results.Ok(new { names = secrets.Names(), protection = secrets.ProtectionName }));
        api.MapPut("/secrets/{name}", (string name, SecretDto dto, ISecretStore secrets, ActivityLog log) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Value)) return Results.BadRequest(new { error = "Value is empty." });
            secrets.Set(name, dto.Value.Trim());
            log.Record(ActivityKinds.System, $"Secret '{name}' stored", status: "ok");
            return Results.Ok(new { name, stored = true });
        });
        api.MapDelete("/secrets/{name}", (string name, ISecretStore secrets, ActivityLog log) =>
        {
            var removed = secrets.Remove(name);
            if (removed) log.Record(ActivityKinds.System, $"Secret '{name}' removed", status: "ok");
            return removed ? Results.Ok() : Results.NotFound();
        });

        // ---- AI providers ----
        api.MapGet("/ai/providers", (ISettingsStore settings, ProviderRegistry providers) => Results.Ok(settings.Current.Ai.Providers.Select(p => new
        {
            config = p,
            status = providers.CachedStatus(p.Id),
        })));
        api.MapPost("/ai/providers/{id}/check", async (string id, ProviderRegistry providers, CancellationToken ct) =>
            Results.Ok(await providers.CheckAsync(id, ct)));
        api.MapGet("/ai/route", async (string text, ModelRouter router, CancellationToken ct) =>
        {
            var d = await router.RouteAsync(text, ct);
            return Results.Ok(new { d.Role, provider = d.Provider?.Id, d.Model, d.Reason, supportsTools = d.SupportsTools, capabilities = d.Info?.Capabilities });
        });
        api.MapGet("/ai/models", async (ModelManager models, CancellationToken ct) => Results.Ok(new
        {
            providers = await models.ListAsync(ct),
            recommended = ModelManager.Recommended,
            pulls = models.Pulls,
        }));
        api.MapPost("/ai/providers/{id}/pull", (string id, PullDto dto, ModelManager models) =>
            models.StartPull(id, dto.Model?.Trim() ?? "", out var error)
                ? Results.Accepted(value: new { started = dto.Model })
                : Results.BadRequest(new { error }));

        // ---- Voice ----
        api.MapGet("/voice", (VoiceService voice) => Results.Ok(voice.Status));
        api.MapPost("/voice/listen", async (VoiceService voice, CancellationToken ct) =>
        {
            try { return Results.Ok(new { result = await voice.ListenOnceAsync(ct) }); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
        });
        api.MapPost("/voice/speak", async (SpeakDto dto, VoiceService voice, CancellationToken ct) =>
        {
            var lang = dto.Lang == "ar" ? Lang.Ar : dto.Lang == "en" ? Lang.En : LanguageDetector.Detect(dto.Text);
            await voice.SpeakAsync(dto.Text, lang, ct);
            return Results.Ok();
        });
        api.MapPost("/voice/stop", (VoiceService voice) => { voice.StopSpeaking(); return Results.Ok(); });
        api.MapGet("/voice/voices", (ITextToSpeech tts) => Results.Ok(new { engine = tts.EngineName, available = tts.IsAvailable, voices = tts.Voices }));
        api.MapGet("/voice/models", (SpeechModelManager models) => Results.Ok(new { downloading = models.IsDownloading, models = models.List() }));
        api.MapPost("/voice/models/{name}/download", (string name, SpeechModelManager models, ISettingsStore settings, ILoggerFactory lf, IHostApplicationLifetime life) =>
        {
            if (SpeechModelManager.Catalog.All(c => c.Name != name)) return Results.NotFound();
            if (models.IsDownloading) return Results.Json(new { error = "A download is already running." }, statusCode: 409);
            var logger = lf.CreateLogger("SpeechModels");
            _ = Task.Run(async () =>
            {
                try
                {
                    await models.DownloadAsync(name, life.ApplicationStopping);
                    settings.Update(s => s.Voice.SttModel = name);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Speech model download failed"); }
            });
            return Results.Accepted();
        });

        // ---- Runtime control ----
        api.MapPost("/runtime/pause", (RuntimeState rs, VoiceService voice, IEventBus events, ActivityLog log) =>
        {
            rs.Paused = true;
            voice.Pause();
            events.Publish(EventTypes.RuntimeState, new { paused = true });
            log.Record(ActivityKinds.System, "JARVIS paused", status: "ok");
            return Results.Ok(new { paused = true });
        });
        api.MapPost("/runtime/resume", (RuntimeState rs, VoiceService voice, IEventBus events, ActivityLog log) =>
        {
            rs.Paused = false;
            voice.Resume();
            events.Publish(EventTypes.RuntimeState, new { paused = false });
            log.Record(ActivityKinds.System, "JARVIS resumed", status: "ok");
            return Results.Ok(new { paused = false });
        });
        api.MapPost("/runtime/shutdown", (IHostApplicationLifetime life, IEventBus events, ActivityLog log) =>
        {
            log.Record(ActivityKinds.System, "Shutdown requested", status: "ok");
            events.Publish(EventTypes.RuntimeState, new { stopping = true, reason = "shutdown" });
            _ = Task.Run(async () => { await Task.Delay(500); life.StopApplication(); });
            return Results.Ok(new { stopping = true });
        });
        api.MapPost("/ui/show", (IEventBus events) => { events.Publish(EventTypes.UiShow, new { reason = "request" }); return Results.Ok(); });

        api.MapGet("/presence", (PresenceTracker presence) => Results.Ok(new { supported = presence.IsSupported, snapshot = presence.Current }));
        api.MapPost("/connectivity/simulate", (SimulateDto dto, ConnectivityMonitor monitor) =>
        {
            monitor.Set(!dto.Offline);
            return Results.Ok(new { online = monitor.IsOnline });
        });
        api.MapGet("/capabilities", (PlatformInfo platform) => Results.Ok(Capabilities.All(platform.Name == "windows")));

        app.Map("/ws", (HttpContext ctx, EventHub hub) => hub.HandleAsync(ctx, json, Status(ctx.RequestServices)));
    }

    public static object Status(IServiceProvider sp)
    {
        var rs = sp.GetRequiredService<RuntimeState>();
        var settings = sp.GetRequiredService<ISettingsStore>().Current;
        var platform = sp.GetRequiredService<PlatformInfo>();
        if (rs.Locked)
            return new { version = RuntimeState.Version, locked = true, pinSet = true, platform = platform.Name };

        var presence = sp.GetRequiredService<PresenceTracker>();
        var conn = sp.GetRequiredService<IConnectivity>();
        var providers = sp.GetRequiredService<ProviderRegistry>();
        var voice = sp.GetRequiredService<VoiceService>();
        var approvals = sp.GetRequiredService<ApprovalBroker>();
        var queue = sp.GetRequiredService<OfflineQueue>();
        var memory = sp.GetRequiredService<MemoryStore>();
        var tasks = sp.GetRequiredService<TaskStore>();
        var reminders = sp.GetRequiredService<ReminderStore>();
        var secrets = sp.GetRequiredService<ISecretStore>();
        var paths = sp.GetRequiredService<JarvisPaths>();
        var agent = sp.GetRequiredService<AgentOrchestrator>();

        var ai = settings.Ai.Providers.Where(p => p.Enabled).Select(p => new
        {
            p.Id, p.Name, p.IsLocal,
            available = providers.CachedStatus(p.Id)?.Available,
            message = providers.CachedStatus(p.Id)?.Message,
            models = providers.CachedStatus(p.Id)?.Models.Count ?? 0,
        }).ToList();

        return new
        {
            version = RuntimeState.Version,
            locked = false,
            pinSet = rs.PinSet,
            platform = platform.Name,
            platformDescription = platform.Description,
            startedAt = rs.StartedAt,
            uptimeSeconds = (long)(DateTimeOffset.Now - rs.StartedAt).TotalSeconds,
            paused = rs.Paused,
            online = conn.IsOnline,
            presence = new { supported = presence.IsSupported, snapshot = presence.Current },
            voice = voice.Status,
            ai = new { allowCloud = settings.Ai.AllowCloud, providers = ai, anyAvailable = ai.Any(a => a.available == true && (a.IsLocal || settings.Ai.AllowCloud)) },
            pendingApprovals = approvals.Pending.Count,
            queuedActions = queue.List().Count,
            counts = new
            {
                memories = memory.Count(),
                openTasks = tasks.List().Count,
                upcomingReminders = reminders.List().Count,
            },
            activeConversation = agent.ActiveConversationId,
            eventClients = sp.GetRequiredService<EventHub>().ClientCount,
            dataDir = paths.DataDir,
            secretsProtection = secrets.ProtectionName,
            honorific = settings.General.Honorific,
            userName = settings.General.UserName,
        };
    }

    private static IResult Guard<T>(Func<T?> action)
    {
        try
        {
            var r = action();
            return r is null ? Results.NotFound() : Results.Ok(r);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
