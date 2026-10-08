using Jarvis.Core.Scheduling;
using Jarvis.Core.Settings;
using Jarvis.Core.Tasks;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Memory;

public sealed record RecallHit(MemoryItem Memory, double Score, bool Semantic);

public sealed record EntityProfile(
    Entity Entity,
    IReadOnlyList<MemoryItem> Memories,
    IReadOnlyList<Relation> Relations,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<Reminder> Reminders);

/// <summary>
/// The front door to JARVIS's long-term knowledge: storing memories (with provenance, entity links
/// and semantic indexing) and recalling them (keyword + meaning, ranked together).
/// </summary>
public sealed class KnowledgeService(
    MemoryStore memories,
    EntityStore entities,
    SemanticIndex semantic,
    TaskStore tasks,
    ReminderStore reminders,
    ISettingsStore settings,
    ILogger<KnowledgeService> logger)
{
    public const string MemoryOwner = "memory";

    public MemoryStore Memories => memories;
    public EntityStore Entities => entities;
    public SemanticIndex Semantic => semantic;

    /// <summary>Stores a memory, links the people/things it mentions, and queues it for semantic indexing.</summary>
    public MemoryItem Remember(NewMemory m)
    {
        var item = memories.Add(m);
        Index(item);
        return item;
    }

    public MemoryItem? Edit(string id, MemoryUpdate u)
    {
        var item = memories.Update(id, u);
        if (item is not null) Index(item);
        return item;
    }

    /// <summary>Links entities and queues embedding for one memory.</summary>
    public void Index(MemoryItem item)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.Subject))
            {
                var type = item.Kind switch
                {
                    MemoryKinds.Person => EntityTypes.Person,
                    MemoryKinds.Project => EntityTypes.Project,
                    _ => EntityTypes.Topic,
                };
                var e = entities.Upsert(type, item.Subject!, item.IsConfirmed ? MemorySources.UserExplicit : item.Source);
                entities.Link(item.Id, e.Id);
                LinkExisting(e);
            }
            foreach (var e in entities.Mentioned(item.Content)) entities.Link(item.Id, e.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(ex, "Couldn't link entities for memory {Id}", item.Id);
        }
        semantic.Enqueue(MemoryOwner, item.Id, Text(item));
    }

    /// <summary>When someone/something becomes known, connect the memories that already mention it.</summary>
    public int LinkExisting(Entity e)
    {
        var linked = 0;
        foreach (var name in new[] { e.Name }.Concat(e.Aliases))
        {
            foreach (var m in memories.Search(name, 200))
            {
                if (entities.Mentioned(Text(m)).Any(x => x.Id == e.Id))
                {
                    entities.Link(m.Id, e.Id);
                    linked++;
                }
            }
        }
        return linked;
    }

    public static string Text(MemoryItem m) => string.IsNullOrWhiteSpace(m.Subject) ? m.Content : $"{m.Subject}: {m.Content}";

    /// <summary>
    /// Keyword and meaning-based search ranked together. Keyword hits score by rank; semantic hits by
    /// similarity (only when an embedding model is installed). Confirmed memories win ties.
    /// </summary>
    public async Task<IReadOnlyList<RecallHit>> RecallAsync(string query, int limit, CancellationToken ct, string? kind = null)
    {
        var scores = new Dictionary<string, (double Score, bool Semantic)>();
        var keyword = memories.Search(query, limit * 2, kind);
        for (var i = 0; i < keyword.Count; i++) scores[keyword[i].Id] = (1.0 / (i + 2) + 0.35, false);

        // Everything linked to an entity named in the question is relevant too ("CityCrep" → its people and facts).
        foreach (var e in entities.Mentioned(query))
            foreach (var id in entities.MemoryIdsOf(e.Id))
                scores[id] = (Math.Max(scores.GetValueOrDefault(id).Score, 0.55), scores.GetValueOrDefault(id).Semantic);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            foreach (var (id, sim) in await semantic.SearchAsync(MemoryOwner, query, limit * 2, timeout.Token).ConfigureAwait(false))
            {
                if (sim < 0.45) continue;
                var cur = scores.GetValueOrDefault(id);
                scores[id] = (Math.Max(cur.Score, sim) + (cur.Score > 0 ? 0.1 : 0), true);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or AI.AiProviderException)
        {
            logger.LogDebug(ex, "Semantic recall unavailable; keyword results only");
        }

        var items = memories.GetMany(scores.Keys).Where(m => kind is null || m.Kind == kind);
        return items
            .Select(m => new RecallHit(m, scores[m.Id].Score + (m.IsConfirmed ? 0.05 : 0), scores[m.Id].Semantic))
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList();
    }

    /// <summary>Everything JARVIS knows around one person/organisation/project.</summary>
    public EntityProfile Profile(Entity e)
    {
        var mems = memories.GetMany(entities.MemoryIdsOf(e.Id)).OrderByDescending(m => m.IsConfirmed).ThenByDescending(m => m.UpdatedAt).ToList();
        var names = new[] { e.Name }.Concat(e.Aliases).Select(EntityStore.Normalize).Where(n => n.Length >= 2).ToList();
        bool Mentions(string text) { var t = EntityStore.Normalize(text); return names.Any(n => t.Contains(n, StringComparison.Ordinal)); }
        var relatedTasks = tasks.List(includeClosed: false).Where(t => Mentions(t.Title) || (t.Project is not null && Mentions(t.Project)) || (t.Notes is not null && Mentions(t.Notes))).ToList();
        var relatedReminders = reminders.List().Where(r => Mentions(r.Text)).ToList();
        return new EntityProfile(e, mems, entities.RelationsOf(e.Id), relatedTasks, relatedReminders);
    }

    /// <summary>Records "from relation to" (creating the entities if needed) and a readable memory of it.</summary>
    public Relation Relate(string fromName, string fromType, string relation, string toName, string toType, string source, MemoryProvenance? provenance)
    {
        var from = entities.Upsert(fromType, fromName, source);
        var to = entities.Upsert(toType, toName, source);
        LinkExisting(from);
        LinkExisting(to);
        var rel = entities.Relate(from.Id, relation, to.Id, source, null, provenance);
        var sentence = $"{from.Name} {rel.Type.Replace('_', ' ')} {to.Name}";
        var mem = Remember(new NewMemory(sentence, MemoryKinds.Fact, from.Name, source, Provenance: provenance));
        entities.Link(mem.Id, to.Id);
        return rel;
    }

    /// <summary>Re-index memories that have no vector for the current embedding model.</summary>
    public async Task<int> BackfillAsync(CancellationToken ct)
    {
        var model = await semantic.ResolveAsync(ct).ConfigureAwait(false);
        if (model is null) return 0;
        var all = memories.List(limit: 1000);
        var missing = semantic.Missing(MemoryOwner, all.Select(m => m.Id), model.Value.Model).ToHashSet();
        foreach (var m in all.Where(m => missing.Contains(m.Id))) semantic.Enqueue(MemoryOwner, m.Id, Text(m));
        var done = 0;
        while (true)
        {
            var n = await semantic.DrainAsync(ct).ConfigureAwait(false);
            if (n == 0) break;
            done += n;
        }
        return done;
    }
}
