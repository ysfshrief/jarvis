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
using Jarvis.Core.Workflows;
using Jarvis.Voice;

namespace Jarvis.Runtime;

public sealed record ChatRequestDto(string Text, string? ConversationId, string? Source);
public sealed record ApprovalDecisionDto(bool Approve, bool Remember = false);
public sealed record MemoryDto(string Content, string? Kind, string? Subject, string? Source, double? Confidence, string? Tags);
public sealed record WorkflowStepDto(string Title, List<int>? DependsOn, DateTimeOffset? Due, string? WaitingFor, bool? RequiresApproval);
public sealed record WorkflowDto(string Title, string? Template, string? About, string? Goal, List<WorkflowStepDto>? Steps, DateTimeOffset? Due, string? Repeat);
public sealed record StepDto(string? Title, string? Status, string? Notes, string? Note, string? WaitingFor, double? FollowUpDays, DateTimeOffset? Due, bool? ClearFollowUp, bool? RequiresApproval);
public sealed record NoteDto(string Text, string? StepId);
public sealed record EntityDto(string? Name, string? Type, List<string>? Aliases, string? Notes);
public sealed record RelationDto(string From, string? FromType, string Relation, string To, string? ToType);
public sealed record TaskDto(string? Title, string? Notes, string? State, string? Priority, string? Project, DateTimeOffset? DueAt, bool ClearDue = false, string? Recurrence = null);
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

        // ---- Memory & knowledge ----
        api.MapGet("/memory", async (KnowledgeService knowledge, string? q, string? kind, bool? confirmed, int? limit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.Ok(knowledge.Memories.List(kind, limit ?? 200, confirmed: confirmed).Select(m => MemoryView(m, knowledge, null, null)));
            var hits = await knowledge.RecallAsync(q, limit ?? 50, ct, kind);
            return Results.Ok(hits.Where(h => confirmed is null || h.Memory.IsConfirmed == confirmed)
                .Select(h => MemoryView(h.Memory, knowledge, h.Score, h.Semantic)));
        });
        api.MapGet("/memory/status", async (KnowledgeService knowledge, ISettingsStore settings, CancellationToken ct) =>
        {
            var (total, confirmedCount, inferred) = knowledge.Memories.Counts();
            return Results.Ok(new
            {
                total, confirmed = confirmedCount, inferred, entities = knowledge.Entities.Count(),
                learning = settings.Current.Memory.LearnPatterns, enabled = settings.Current.Memory.Enabled,
                semantic = await knowledge.Semantic.StatusAsync(KnowledgeService.MemoryOwner, total, ct),
            });
        });
        api.MapGet("/memory/{id}", (string id, KnowledgeService knowledge) =>
            knowledge.Memories.Get(id) is { } m ? Results.Ok(MemoryView(m, knowledge, null, null)) : Results.NotFound());
        api.MapPost("/memory", (MemoryDto dto, KnowledgeService knowledge) =>
            Guard(() => MemoryView(knowledge.Remember(new NewMemory(dto.Content, dto.Kind ?? MemoryKinds.Fact, dto.Subject, dto.Source ?? MemorySources.UserExplicit,
                dto.Confidence, dto.Tags, Provenance: new MemoryProvenance("dashboard"))), knowledge, null, null)));
        api.MapPut("/memory/{id}", (string id, MemoryDto dto, KnowledgeService knowledge) =>
            Guard(() => knowledge.Edit(id, new MemoryUpdate(dto.Content, dto.Kind, dto.Subject, dto.Source, dto.Confidence, dto.Tags)) is { } m ? MemoryView(m, knowledge, null, null) : null));
        api.MapPost("/memory/{id}/confirm", (string id, KnowledgeService knowledge, PatternLearner learner, ActivityLog log) =>
        {
            var m = knowledge.Memories.Confirm(id);
            if (m is null) return Results.NotFound();
            learner.MarkConfirmed(id);
            log.Record(ActivityKinds.Memory, $"Confirmed: {m.Content}", status: "ok");
            return Results.Ok(MemoryView(m, knowledge, null, null));
        });
        api.MapPost("/memory/{id}/reject", (string id, PatternLearner learner, KnowledgeService knowledge, ActivityLog log) =>
        {
            var m = knowledge.Memories.Get(id);
            if (m is null || !learner.Reject(id)) return Results.NotFound();
            log.Record(ActivityKinds.Memory, $"Rejected: {m.Content}", status: "ok");
            return Results.Ok(new { rejected = id });
        });
        api.MapPost("/memory/learn", (PatternLearner learner, ActivityLog log) =>
        {
            var r = learner.Run(DateTimeOffset.Now, force: true);
            log.Record(ActivityKinds.Memory, $"Pattern review: {r.Proposed} new, {r.Updated} updated", status: "ok");
            return Results.Ok(r);
        });
        api.MapPost("/memory/reindex", async (KnowledgeService knowledge, CancellationToken ct) =>
        {
            knowledge.Semantic.Invalidate();
            return Results.Ok(new { indexed = await knowledge.BackfillAsync(ct) });
        });
        api.MapDelete("/memory/{id}", (string id, MemoryStore store) =>
            store.Delete(id) ? Results.Ok(new { deleted = id }) : Results.NotFound());
        api.MapDelete("/memory", (MemoryStore store, ActivityLog log, bool? confirm, string? kind) =>
        {
            if (confirm != true) return Results.BadRequest(new { error = "Pass confirm=true to clear memory." });
            var n = store.Clear(kind);
            log.Record(ActivityKinds.Memory, $"Cleared {(kind ?? "all")} memory ({n} items)", status: "ok");
            return Results.Ok(new { deleted = n });
        });

        api.MapGet("/entities", (EntityStore entities, string? type, string? q) =>
            Results.Ok(entities.List(type, q).Select(e => new { e.Id, e.Type, e.Name, e.Aliases, e.Notes, e.Source, e.UpdatedAt, memories = entities.MemoryIdsOf(e.Id).Count })));
        api.MapGet("/entities/{id}", (string id, KnowledgeService knowledge, Jarvis.Core.Files.FileIndex files) =>
            knowledge.Entities.Get(id) is { } e ? Results.Ok(ProfileView(knowledge.Profile(e), knowledge, files)) : Results.NotFound());
        api.MapPost("/entities", (EntityDto dto, KnowledgeService knowledge) => Guard(() =>
        {
            var e = knowledge.Entities.Upsert(dto.Type ?? EntityTypes.Topic, dto.Name ?? "", MemorySources.UserExplicit);
            knowledge.LinkExisting(e);
            return e;
        }));
        api.MapPut("/entities/{id}", (string id, EntityDto dto, KnowledgeService knowledge) => Guard(() =>
        {
            var e = knowledge.Entities.Update(id, dto.Name, dto.Type, dto.Aliases, dto.Notes);
            if (e is not null) knowledge.LinkExisting(e); // new aliases (e.g. the Arabic spelling) pick up more memories
            return e;
        }));
        api.MapDelete("/entities/{id}", (string id, EntityStore entities) =>
            entities.Delete(id) ? Results.Ok(new { deleted = id }) : Results.NotFound());
        api.MapPost("/relations", (RelationDto dto, KnowledgeService knowledge) =>
            Guard(() => knowledge.Relate(dto.From, dto.FromType ?? EntityTypes.Person, dto.Relation, dto.To, dto.ToType ?? EntityTypes.Organization,
                MemorySources.UserExplicit, new MemoryProvenance("dashboard"))));
        api.MapDelete("/relations/{id}", (string id, EntityStore entities) =>
            entities.DeleteRelation(id) ? Results.Ok(new { deleted = id }) : Results.NotFound());

        // ---- Workflows ----
        api.MapGet("/workflows", (WorkflowStore store, bool? all) => Results.Ok(store.List(all ?? false)));
        api.MapGet("/workflows/templates", () => Results.Ok(WorkflowTemplates.All.Select(t => new { t.Id, t.Name, t.Description, steps = t.Steps.Select(x => x.Title), t.EntityType })));
        api.MapGet("/workflows/{id}", (string id, WorkflowStore store) =>
            store.Get(id) is { } wf ? Results.Ok(new { workflow = wf, history = store.History(id) }) : Results.NotFound());
        api.MapPost("/workflows", (WorkflowDto dto, WorkflowService workflows) => Guard(() =>
            workflows.Start(dto.Title, dto.Template ?? (dto.Steps is { Count: > 0 } ? "custom" : "deal"), dto.About, dto.Goal,
                dto.Steps?.Select(x => new NewStep(x.Title, x.DependsOn, x.Due, x.WaitingFor, x.RequiresApproval ?? false)).ToList(),
                dto.Due, string.IsNullOrWhiteSpace(dto.Repeat) ? null : dto.Repeat)));
        api.MapPut("/workflows/{id}/steps/{stepId}", (string id, string stepId, StepDto dto, WorkflowStore store) => Guard(() =>
            store.UpdateStep(stepId, new StepChange(dto.Status, dto.Notes, dto.WaitingFor,
                dto.FollowUpDays is { } days ? DateTimeOffset.Now.AddDays(days) : dto.Status == StepStatus.Waiting ? DateTimeOffset.Now + WorkflowService.DefaultFollowUp : null,
                dto.Due, dto.ClearFollowUp ?? false, dto.Title), dto.Note)));
        api.MapPost("/workflows/{id}/steps", (string id, StepDto dto, WorkflowStore store) => Guard(() =>
            store.AddStep(id, new NewStep(dto.Title ?? "", DueAt: dto.Due, WaitingFor: dto.WaitingFor, RequiresApproval: dto.RequiresApproval ?? false))));
        api.MapPost("/workflows/{id}/steps/{stepId}/run", async (string id, string stepId, WorkflowRunner runner, ISettingsStore settings, CancellationToken ct) =>
        {
            var ctx = new ToolContext { Lang = settings.Current.General.Language == "ar" ? Lang.Ar : Lang.En, Settings = settings.Current, ConversationId = "workflow-" + id, Via = "dashboard", CancellationToken = ct };
            var (ok, message) = await runner.RunStepAsync(stepId, ctx);
            return Results.Ok(new { ok, message });
        });
        api.MapPost("/workflows/{id}/notes", (string id, NoteDto dto, WorkflowStore store) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(dto.Text)) return Results.BadRequest(new { error = "Note is empty." });
            store.Note(id, dto.Text.Trim(), dto.StepId);
            return Results.Ok();
        });
        api.MapPost("/workflows/{id}/cancel", (string id, WorkflowStore store) => store.Cancel(id) ? Results.Ok() : Results.NotFound());
        api.MapDelete("/workflows/{id}", (string id, WorkflowStore store) => store.Delete(id) ? Results.Ok() : Results.NotFound());

        // ---- File knowledge ----
        api.MapGet("/files/status", async (Jarvis.Core.Files.FileIndex index, Jarvis.Core.Files.FileIndexer indexer, Jarvis.Core.Files.IOcrEngine ocr, KnowledgeService knowledge, ISettingsStore settings, CancellationToken ct) =>
        {
            var (files, withText, chars, lastIndexed) = index.Stats();
            return Results.Ok(new
            {
                enabled = settings.Current.Files.IndexEnabled, roots = indexer.Roots(), files, withText, chars, lastIndexed,
                kinds = index.CountsByKind(), progress = indexer.Progress, ocr = new { ocr.IsAvailable, ocr.Name },
                semantic = await knowledge.Semantic.StatusAsync(Jarvis.Core.Files.FileIndex.ChunkOwner, 0, ct),
            });
        });
        api.MapGet("/files/search", async (Jarvis.Core.Files.FileIndex index, string q, string? kind, int? limit, CancellationToken ct) =>
            Results.Ok((await index.SearchAsync(q, limit ?? 25, kind, ct)).Select(h => new { file = h.File, h.Snippet, h.Score, h.Semantic })));
        api.MapGet("/files/latest", (Jarvis.Core.Files.FileIndex index, string? kind, string? entity, int? limit) =>
            Results.Ok(index.Latest(limit ?? 20, kind, entityId: entity)));
        api.MapGet("/files/{id}", (string id, Jarvis.Core.Files.FileIndex index) =>
        {
            var f = index.Get(id);
            if (f is null) return Results.NotFound();
            var text = index.Text(id);
            return Results.Ok(new
            {
                file = f, entities = index.EntitiesOf(id), keyPoints = Jarvis.Core.Files.TextAnalysis.KeySentences(text, 6),
                preview = text.Length > 4000 ? text[..4000] + "…" : text,
                previous = File.Exists(f.Path) && Jarvis.Core.Tools.Builtin.FileCompareTool.PreviousVersion(f.Path) is { } prev ? index.GetByPath(prev) : null,
            });
        });
        // Compares two indexed versions using their indexed text (nothing is re-read from disk).
        api.MapGet("/files/{id}/compare/{otherId}", (string id, string otherId, Jarvis.Core.Files.FileIndex index) =>
        {
            var (a, b) = (index.Get(id), index.Get(otherId));
            if (a is null || b is null) return Results.NotFound();
            var (older, newer) = a.ModifiedAt <= b.ModifiedAt ? (a, b) : (b, a);
            var diff = Jarvis.Core.Files.TextAnalysis.Compare(index.Text(older.Id), index.Text(newer.Id));
            return Results.Ok(new
            {
                older, newer, diff.Added, diff.Removed, diff.Unchanged, diff.Identical,
                addedLines = diff.AddedLines.Take(60), removedLines = diff.RemovedLines.Take(60),
            });
        });
        api.MapPost("/files/scan", (Jarvis.Core.Files.FileIndexer indexer, ISettingsStore settings, IHostApplicationLifetime life) =>
        {
            if (!settings.Current.Files.IndexEnabled) return Results.BadRequest(new { error = "Turn on file knowledge first (Settings → Files)." });
            _ = Task.Run(() => indexer.ScanAsync(life.ApplicationStopping));
            return Results.Accepted();
        });
        api.MapDelete("/files/index", (Jarvis.Core.Files.FileIndex index, ActivityLog log) =>
        {
            index.ClearAll();
            log.Record(ActivityKinds.System, "File index cleared", status: "ok");
            return Results.Ok();
        });

        // ---- Tasks ----
        api.MapGet("/tasks", (TaskStore store, bool? all) => Results.Ok(store.List(all ?? false)));
        api.MapPost("/tasks", (TaskDto dto, TaskStore store) =>
            Guard(() => store.Create(new NewTask(dto.Title ?? "", dto.Notes, dto.Priority ?? "normal", dto.Project,
                dto.DueAt ?? (string.IsNullOrWhiteSpace(dto.Recurrence) ? null : new DateTimeOffset(DateTime.Today.AddHours(9))),
                string.IsNullOrWhiteSpace(dto.Recurrence) ? null : dto.Recurrence))));
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
        api.MapPost("/ui/console", (IEventBus events) => { events.Publish(EventTypes.UiConsole, new { reason = "request" }); return Results.Ok(); });

        api.MapGet("/presence", (PresenceTracker presence) => Results.Ok(new { supported = presence.IsSupported, snapshot = presence.Current }));
        api.MapPost("/connectivity/simulate", (SimulateDto dto, ConnectivityMonitor monitor) =>
        {
            monitor.Set(!dto.Offline);
            return Results.Ok(new { online = monitor.IsOnline });
        });
        // ---- System health (sampled only when a UI asks, so an idle JARVIS costs nothing) ----
        api.MapGet("/system/metrics", (Jarvis.Core.Monitoring.SystemMetrics metrics) =>
        {
            var latest = metrics.Latest;
            var current = latest is not null && DateTimeOffset.Now - latest.Timestamp < TimeSpan.FromSeconds(1) ? latest : metrics.Sample();
            return Results.Ok(new { source = metrics.SourceName, current, history = metrics.History });
        });
        api.MapGet("/system/disks", () => Results.Ok(Jarvis.Core.Monitoring.SystemMetrics.Disks()));
        api.MapGet("/system/processes", (Jarvis.Core.Monitoring.SystemMetrics metrics, string? sort, int? limit) =>
            Results.Ok(metrics.Processes(limit ?? 15, sort ?? "memory")));
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
            honorificAr = settings.General.HonorificAr,
            userName = settings.General.UserName,
        };
    }

    private static object MemoryView(MemoryItem m, KnowledgeService knowledge, double? score, bool? semantic) => new
    {
        m.Id, m.Kind, m.Content, m.Subject, m.Source, m.Confidence, m.Tags, m.CreatedAt, m.UpdatedAt, m.ExpiresAt, m.LastUsedAt, m.UseCount,
        m.Provenance, m.ConfirmedAt, m.IsConfirmed, score, semantic,
        entities = knowledge.Entities.EntitiesOf(m.Id).Select(e => new { e.Id, e.Name, e.Type }),
    };

    private static object ProfileView(EntityProfile p, KnowledgeService knowledge, Jarvis.Core.Files.FileIndex files) => new
    {
        files = files.AboutEntity(p.Entity.Id, 12),
        entity = p.Entity,
        memories = p.Memories.Select(m => MemoryView(m, knowledge, null, null)),
        relations = p.Relations,
        tasks = p.Tasks,
        reminders = p.Reminders,
    };

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
