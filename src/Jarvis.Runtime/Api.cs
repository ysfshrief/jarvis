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
        api.MapPost("/memory/learn", async (PatternLearner learner, Jarvis.Core.Inbox.InboxService inbox, ActivityLog log, CancellationToken ct) =>
        {
            await inbox.CollectWritingSamplesAsync(ct); // no-op unless writing-style learning is on
            var r = learner.Run(DateTimeOffset.Now, force: true);
            log.Record(ActivityKinds.Memory, $"Pattern review: {r.Proposed} new, {r.Updated} updated", status: "ok");
            return Results.Ok(r);
        });
        // Research and learning from sources (both go through the tools, so permissions and the audit log apply).
        api.MapPost("/memory/research", async (ResearchDto dto, ToolExecutor executor, ISettingsStore settings, CancellationToken ct) =>
        {
            var ctx = new ToolContext { Settings = settings.Current, ConversationId = "dashboard", Via = "dashboard", CancellationToken = ct, Lang = settings.Current.General.Language == "ar" ? Jarvis.Core.Language.Lang.Ar : Jarvis.Core.Language.Lang.En };
            var (r, step) = string.IsNullOrWhiteSpace(dto.Source)
                ? await executor.ExecuteAsync("research_topic", ToolArgs.From(new { topic = dto.Topic ?? "", sources = dto.Sources ?? 3 }), ctx)
                : await executor.ExecuteAsync("learn_from_source", ToolArgs.From(new { source = dto.Source, topic = dto.Topic }), ctx);
            return Results.Ok(new { r.Success, r.Message, r.Data, status = r.Status.ToString() });
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

        // ---- Browser ----
        api.MapGet("/browser", (Jarvis.Core.Web.BrowserService browser, ISettingsStore settings) => Results.Ok(new
        {
            enabled = settings.Current.Web.BrowserEnabled, browserPath = browser.Locate(), running = browser.IsRunning, url = browser.CurrentUrl,
        }));
        api.MapPost("/browser/close", async (Jarvis.Core.Web.BrowserService browser) => { await browser.CloseAsync(); return Results.Ok(); });

        // ---- Executive inbox ----
        api.MapGet("/inbox/status", (Jarvis.Core.Inbox.InboxService inbox) => Results.Ok(new
        {
            accounts = inbox.Store.Accounts().Select(a => new { a.Id, a.Kind, a.Address, a.DisplayName, a.Enabled, a.Status, a.StatusMessage, a.LastSync, config = a.Config }),
            counts = inbox.Store.Counts(),
            drafts = inbox.Store.Drafts().Count,
            presets = Jarvis.Core.Inbox.MailCatalog.Presets,
            connectors = Jarvis.Core.Inbox.MailCatalog.Connectors,
        }));
        api.MapPost("/inbox/accounts", async (MailAccountDto dto, Jarvis.Core.Inbox.InboxService inbox, CancellationToken ct) =>
        {
            var preset = Jarvis.Core.Inbox.MailCatalog.Presets.FirstOrDefault(p => p.Id == dto.Preset);
            var cfg = dto.Config ?? preset?.Config ?? new Jarvis.Core.Inbox.MailAccountConfig();
            if (string.IsNullOrWhiteSpace(cfg.Username)) cfg = cfg with { Username = dto.Address ?? "" };
            try
            {
                var a = await inbox.AddAccountAsync(Jarvis.Core.Inbox.ImapSmtpConnector.KindName, dto.Address ?? "", dto.DisplayName, cfg, dto.Password ?? "", ct);
                _ = Task.Run(() => inbox.SyncAsync(CancellationToken.None, a.Id));
                return Results.Ok(new { a.Id, a.Address, a.Status });
            }
            catch (Exception ex) when (ex is ArgumentException or Jarvis.Core.Inbox.MailConnectorException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        api.MapDelete("/inbox/accounts/{id}", (string id, Jarvis.Core.Inbox.InboxService inbox) => inbox.RemoveAccount(id) ? Results.Ok() : Results.NotFound());
        api.MapPost("/inbox/sync", async (Jarvis.Core.Inbox.InboxService inbox, CancellationToken ct) => Results.Ok(await inbox.SyncAsync(ct)));
        api.MapGet("/inbox/messages", (Jarvis.Core.Inbox.InboxService inbox, string? category, string? q, bool? all, int? limit) =>
            Results.Ok((q is { Length: > 0 } ? inbox.Store.Search(q, limit ?? 100) : inbox.Store.List(category, all ?? false, limit ?? 100))
                .Select(m => m with { Body = "" })));
        api.MapGet("/inbox/messages/{id}", (string id, Jarvis.Core.Inbox.InboxService inbox, KnowledgeService knowledge) =>
        {
            var m = inbox.Store.Get(id);
            if (m is null) return Results.NotFound();
            return Results.Ok(new
            {
                message = m,
                entities = inbox.Store.EntityIdsOf(id).Select(knowledge.Entities.Get).OfType<Entity>(),
                drafts = inbox.Store.Drafts(null).Where(d => d.ReplyToId == id),
                fromSender = inbox.Store.FromSender(m.FromAddress, 6).Where(x => x.Id != id).Select(x => x with { Body = "" }),
            });
        });
        api.MapPost("/inbox/messages/{id}/category", (string id, CategoryDto dto, Jarvis.Core.Inbox.InboxService inbox) =>
            Guard(() => inbox.Recategorize(id, dto.Category ?? "") ? new { ok = true } : null));
        api.MapPost("/inbox/messages/{id}/handled", (string id, HandledDto dto, Jarvis.Core.Inbox.InboxService inbox) =>
            inbox.Store.SetHandled(id, dto.Handled) ? Results.Ok() : Results.NotFound());
        api.MapGet("/inbox/drafts", (Jarvis.Core.Inbox.InboxService inbox, string? status) => Results.Ok(inbox.Store.Drafts(status ?? Jarvis.Core.Inbox.DraftStatus.Draft)));
        api.MapPost("/inbox/drafts", (DraftDto dto, Jarvis.Core.Inbox.InboxService inbox) => Guard(() => dto.ReplyTo is { Length: > 0 }
            ? inbox.DraftReply(dto.ReplyTo, dto.Body ?? "", "you")
            : inbox.DraftNew(Addrs(dto.To), Addrs(dto.Cc), dto.Subject ?? "", dto.Body ?? "", "you", dto.AccountId)));
        api.MapPut("/inbox/drafts/{id}", (string id, DraftDto dto, Jarvis.Core.Inbox.InboxService inbox) =>
            Guard(() => inbox.Store.UpdateDraft(id, dto.To is null ? null : Addrs(dto.To), dto.Cc is null ? null : Addrs(dto.Cc), dto.Subject, dto.Body)));
        api.MapDelete("/inbox/drafts/{id}", (string id, Jarvis.Core.Inbox.InboxService inbox) =>
        {
            if (inbox.Store.GetDraft(id) is not { Status: Jarvis.Core.Inbox.DraftStatus.Draft or Jarvis.Core.Inbox.DraftStatus.Failed }) return Results.NotFound();
            inbox.Store.SetDraftStatus(id, Jarvis.Core.Inbox.DraftStatus.Discarded);
            return Results.Ok();
        });
        // Sending goes through the same tool, permission check and (always) approval as when the AI asks.
        api.MapPost("/inbox/drafts/{id}/send", async (string id, ToolExecutor executor, ISettingsStore settings) =>
        {
            var ctx = new ToolContext { Lang = settings.Current.General.Language == "ar" ? Lang.Ar : Lang.En, Settings = settings.Current, ConversationId = "dashboard", Via = "dashboard" };
            var (result, step) = await executor.ExecuteAsync("inbox_send", ToolArgs.From(new { draft = id }), ctx);
            return Results.Ok(new { result.Success, result.Message, status = step.Status });
        });

        // ---- Calendar ----
        api.MapGet("/calendar/calendars", (Jarvis.Core.Agenda.CalendarService cal) =>
            Results.Ok(cal.Store.Calendars().Select(c => new { c.Id, c.Name, c.Kind, c.Color, c.Enabled, c.Status, c.StatusMessage, c.LastSync })));
        api.MapPost("/calendar/calendars", async (CalendarSubDto dto, Jarvis.Core.Agenda.CalendarService cal, CancellationToken ct) =>
        {
            try
            {
                var c = await cal.SubscribeAsync(dto.Name ?? "", dto.Url ?? "", ct);
                return Results.Ok(new { c.Id, c.Name, c.Status });
            }
            catch (Exception ex) when (ex is ArgumentException or Jarvis.Core.Agenda.CalendarException or HttpRequestException or TaskCanceledException)
            {
                return Results.BadRequest(new { error = ex is HttpRequestException or TaskCanceledException ? "Couldn't reach that calendar address." : ex.Message });
            }
        });
        api.MapDelete("/calendar/calendars/{id}", (string id, Jarvis.Core.Agenda.CalendarService cal) => cal.Remove(id) ? Results.Ok() : Results.NotFound());
        api.MapPost("/calendar/sync", async (Jarvis.Core.Agenda.CalendarService cal, CancellationToken ct) => Results.Ok(new { events = await cal.SyncAsync(ct) }));
        api.MapGet("/calendar/events", (Jarvis.Core.Agenda.CalendarService cal, DateTimeOffset? from, DateTimeOffset? to) =>
        {
            var f = from ?? new DateTimeOffset(DateTime.Today);
            return Results.Ok(cal.Store.Between(f, to ?? f.AddDays(7), 1000));
        });
        api.MapPost("/calendar/events", (EventDto dto, Jarvis.Core.Agenda.CalendarService cal) => Guard(() =>
            cal.AddLocal(dto.Title ?? "", dto.Start, dto.End ?? dto.Start.AddHours(1), dto.Location,
                (dto.Attendees ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(a => a.Contains('@') ? new Jarvis.Core.Agenda.Attendee(null, a) : new Jarvis.Core.Agenda.Attendee(a, null)).ToList(), "you")));
        api.MapDelete("/calendar/events/{id}", (string id, Jarvis.Core.Agenda.CalendarService cal) =>
            cal.Store.Get(id) is { Source: not "ics" } ? (cal.Store.DeleteEvent(id) ? Results.Ok() : Results.NotFound()) : Results.BadRequest(new { error = "Events from subscribed calendars are read-only." }));
        api.MapGet("/calendar/events/{id}/prep", (string id, Jarvis.Core.Agenda.CalendarService cal, ISettingsStore settings) =>
        {
            var e = cal.Store.Get(id);
            if (e is null) return Results.NotFound();
            var ctx = new ToolContext { Lang = settings.Current.General.Language == "ar" ? Lang.Ar : Lang.En, Settings = settings.Current };
            var b = cal.Prepare(e);
            return Results.Ok(new
            {
                text = Jarvis.Core.Agenda.CalendarService.Render(b, ctx),
                people = b.People.Select(p => new { name = p.Person, known = p.Entity?.Name, entityId = p.Entity?.Id, facts = p.Facts }),
                workflows = b.Workflows.Select(w => new { w.Id, w.Title }),
                tasks = b.OpenTasks.Select(t => new { t.Id, t.Title }),
                mail = b.RecentMail.Select(m => new { m.Id, m.Sender, m.Subject, m.ReceivedAt }),
                files = b.Files.Select(f => new { f.Id, f.Name }),
            });
        });

        // ---- Meetings ----
        api.MapGet("/meetings", (Jarvis.Core.Meetings.MeetingStore store) =>
            Results.Ok(store.List().Select(m => new { m.Id, m.Title, m.EventId, m.StartedAt, m.EndedAt, m.Status, m.AudioSeconds, m.Error, actionItems = m.Notes?.ActionItems.Count ?? 0, decisions = m.Notes?.Decisions.Count ?? 0 })));
        api.MapGet("/meetings/{id}", (string id, Jarvis.Core.Meetings.MeetingStore store) => store.Get(id) is { } m ? Results.Ok(m) : Results.NotFound());
        api.MapDelete("/meetings/{id}", (string id, Jarvis.Core.Meetings.MeetingStore store, Jarvis.Core.Meetings.MeetingRecorder rec) =>
            rec.Current?.Id == id ? Results.BadRequest(new { error = "Stop the recording first." }) : store.Delete(id) ? Results.Ok() : Results.NotFound());
        // Starting goes through the tool, so the same always-ask approval applies to the dashboard button.
        api.MapPost("/meetings/start", async (MeetingStartDto dto, ToolExecutor executor, ISettingsStore settings) =>
        {
            var ctx = new ToolContext { Lang = settings.Current.General.Language == "ar" ? Lang.Ar : Lang.En, Settings = settings.Current, ConversationId = "dashboard", Via = "dashboard" };
            var (result, step) = await executor.ExecuteAsync("meeting_record_start", ToolArgs.From(new { title = dto.Title }), ctx);
            return Results.Ok(new { result.Success, result.Message, status = step.Status });
        });
        api.MapPost("/meetings/stop", async (Jarvis.Core.Meetings.MeetingRecorder rec) => Results.Ok(await rec.StopAsync() is { } m ? new { m.Id, m.Status } : null));
        // Action items become tasks only when you choose them; each goes through task_create.
        api.MapPost("/meetings/{id}/tasks", async (string id, MeetingTasksDto dto, Jarvis.Core.Meetings.MeetingStore store, ToolExecutor executor, ISettingsStore settings) =>
        {
            var m = store.Get(id);
            if (m?.Notes is null) return Results.NotFound();
            var ctx = new ToolContext { Lang = settings.Current.General.Language == "ar" ? Lang.Ar : Lang.En, Settings = settings.Current, ConversationId = "dashboard", Via = "dashboard" };
            var created = 0;
            foreach (var i in dto.Items ?? [])
            {
                if (i < 0 || i >= m.Notes.ActionItems.Count) continue;
                var a = m.Notes.ActionItems[i];
                var title = a.Text.Length > 140 ? a.Text[..137] + "…" : a.Text;
                var (r, _) = await executor.ExecuteAsync("task_create", ToolArgs.From(new { title, notes = $"From the meeting “{m.Title}” ({m.StartedAt:d MMM})", due = a.Due?.ToString("O") }), ctx);
                if (r.Success) created++;
            }
            return Results.Ok(new { created });
        });

        // ---- Plugins ----
        api.MapGet("/plugins", (Jarvis.Core.Plugins.PluginManager plugins) => Results.Ok(plugins.List().Select(PluginView)));
        api.MapGet("/plugins/{id}", (string id, Jarvis.Core.Plugins.PluginManager plugins) => plugins.Get(id) is { } p ? Results.Ok(PluginView(p)) : Results.NotFound());
        api.MapPost("/plugins/generate", async (PluginGenerateDto dto, ToolExecutor executor, ISettingsStore settings) =>
        {
            var ctx = new ToolContext { Lang = settings.Current.General.Language == "ar" ? Lang.Ar : Lang.En, Settings = settings.Current, ConversationId = "dashboard", Via = "dashboard" };
            var (result, _) = await executor.ExecuteAsync("plugin_create", ToolArgs.From(new { description = dto.Description ?? "" }), ctx);
            return Results.Ok(new { result.Success, result.Message, result.Data });
        });
        api.MapPost("/plugins/import", (PluginImportDto dto, Jarvis.Core.Plugins.PluginManager plugins, CancellationToken ct) =>
        {
            try
            {
                var draft = plugins.SaveDraft(dto.Manifest ?? "", dto.Code ?? "", "imported");
                return Results.Ok(PluginView(plugins.Check(draft.Id, ct)));
            }
            catch (Jarvis.Core.Plugins.PluginException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        // Running the tests with the network is your explicit choice, after seeing the hosts it reads from.
        api.MapPost("/plugins/{id}/check", (string id, PluginCheckDto dto, Jarvis.Core.Plugins.PluginManager plugins, CancellationToken ct) =>
        {
            try { return Results.Ok(PluginView(plugins.Check(id, ct, dto.Network))); }
            catch (Jarvis.Core.Plugins.PluginException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        api.MapPost("/plugins/{id}/install", async (string id, ToolExecutor executor, ISettingsStore settings) =>
        {
            var ctx = new ToolContext { Lang = settings.Current.General.Language == "ar" ? Lang.Ar : Lang.En, Settings = settings.Current, ConversationId = "dashboard", Via = "dashboard" };
            var (result, step) = await executor.ExecuteAsync("plugin_install", ToolArgs.From(new { plugin = id }), ctx);
            return Results.Ok(new { result.Success, result.Message, status = step.Status });
        });
        api.MapPost("/plugins/{id}/update", async (string id, ToolExecutor executor, ISettingsStore settings) =>
        {
            var ctx = new ToolContext { Lang = settings.Current.General.Language == "ar" ? Lang.Ar : Lang.En, Settings = settings.Current, ConversationId = "dashboard", Via = "dashboard" };
            var (result, step) = await executor.ExecuteAsync("plugin_update", ToolArgs.From(new { plugin = id }), ctx);
            return Results.Ok(new { result.Success, result.Message, status = step.Status });
        });
        api.MapDelete("/plugins/{id}/update", (string id, Jarvis.Core.Plugins.PluginManager plugins) => plugins.DiscardUpdate(id) ? Results.Ok() : Results.NotFound());
        api.MapPost("/plugins/{id}/enabled", (string id, PluginEnableDto dto, Jarvis.Core.Plugins.PluginManager plugins) =>
        {
            try { plugins.SetEnabled(id, dto.Enabled); return Results.Ok(); }
            catch (Jarvis.Core.Plugins.PluginException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        api.MapDelete("/plugins/{id}", (string id, Jarvis.Core.Plugins.PluginManager plugins) => plugins.Remove(id) ? Results.Ok() : Results.NotFound());

        // ---- Phone companion ----
        api.MapGet("/devices", (Jarvis.Core.Companion.DeviceStore devices, CompanionServer server, ISettingsStore settings) => Results.Ok(new
        {
            enabled = settings.Current.Companion.Enabled, running = server.Running, port = settings.Current.Companion.Port, error = server.Error,
            addresses = CompanionServer.LanAddresses().Select(a => a.ToString()), devices = devices.List(),
        }));
        // A one-time code (5 minutes) plus everything a phone needs to pair and pin JARVIS's certificate.
        api.MapPost("/devices/pairing", (Jarvis.Core.Companion.DeviceStore devices, CompanionServer server, CompanionCertificate cert, ISettingsStore settings) =>
        {
            if (!server.Running) return Results.BadRequest(new { error = server.Error ?? "Turn on the phone companion first." });
            var (code, expires) = devices.NewPairingCode();
            var ip = CompanionServer.LanAddresses().FirstOrDefault()?.ToString() ?? Environment.MachineName;
            var url = $"https://{ip}:{server.Port}";
            var fp = cert.Fingerprint();
            return Results.Ok(new
            {
                code, expires, url, fingerprint = fp,
                link = $"{url}/companion#pair={code}",
                appLink = $"jarvis://pair?u={Uri.EscapeDataString(url)}&c={code}&fp={Uri.EscapeDataString(fp)}",
            });
        });
        api.MapDelete("/devices/{id}", (string id, Jarvis.Core.Companion.DeviceStore devices, ActivityLog log) =>
        {
            if (!devices.Revoke(id)) return Results.NotFound();
            log.Record(ActivityKinds.System, "Phone removed", status: "ok");
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
        var recorder = sp.GetRequiredService<Jarvis.Core.Meetings.MeetingRecorder>();

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
            // Always reported so every screen can show that a meeting is being recorded.
            recording = recorder.Current is { } rec ? new { rec.Id, rec.Title, rec.StartedAt, source = recorder.SourceDescription } : null,
            recordingBlocker = recorder.Blocker,
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

    private static object PluginView(Jarvis.Core.Plugins.PluginInfo p) => new
    {
        id = p.Id, manifest = p.Manifest, p.Status, p.Source, p.Code, p.Report, p.Error, p.CreatedAt, p.InstalledAt,
        permissions = Jarvis.Core.Plugins.PluginManager.Describe(p.Manifest),
        tools = p.Manifest.Tools.Select(t => new { t.Name, toolName = p.Manifest.ToolName(t), risk = p.Manifest.EffectiveRisk(t).ToString().ToLowerInvariant(), t.Description }),
        update = p.Update is not { } u ? null : new
        {
            manifest = u.Manifest, u.Status, u.Source, u.Code, u.Report, u.Changes, u.MorePermissions,
            permissions = Jarvis.Core.Plugins.PluginManager.Describe(u.Manifest),
        },
    };

    private static IReadOnlyList<string> Addrs(string? s) =>
        (s ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

public sealed record MailAccountDto(string? Preset, string? Address, string? DisplayName, string? Password, Jarvis.Core.Inbox.MailAccountConfig? Config);
public sealed record CategoryDto(string? Category);
public sealed record HandledDto(bool Handled);
public sealed record DraftDto(string? ReplyTo, string? To, string? Cc, string? Subject, string? Body, string? AccountId);
public sealed record CalendarSubDto(string? Name, string? Url);
public sealed record EventDto(string? Title, DateTimeOffset Start, DateTimeOffset? End, string? Location, string? Attendees);
public sealed record MeetingStartDto(string? Title);
public sealed record MeetingTasksDto(List<int>? Items);
public sealed record PluginGenerateDto(string? Description);
public sealed record PluginImportDto(string? Manifest, string? Code);
public sealed record PluginCheckDto(bool Network);
public sealed record PluginEnableDto(bool Enabled);

public sealed record ResearchDto(string? Topic, string? Source, int? Sources);
