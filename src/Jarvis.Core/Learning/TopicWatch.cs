using System.Text.Json;
using Jarvis.Core.Agent;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Notifications;
using Jarvis.Core.Persistence;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Core.Learning;

public sealed record WatchedTopic(string Id, string Topic, int EveryDays, DateTimeOffset CreatedAt, DateTimeOffset? LastRun, DateTimeOffset NextRun, string? LastResult);

/// <summary>
/// "Keep me updated on X": re-researches a topic on a schedule. New facts land in Memory → To review like any
/// research (unconfirmed, with sources); a notification says what's new, and only when something is.
/// Runs only while online, through the same tools and checks as a request.
/// </summary>
public sealed class TopicWatch(JarvisDatabase db, IServiceProvider sp, NotificationCenter notifications, IConnectivity connectivity, ISettingsStore settings)
{
    public const int MaxTopics = 20;

    public WatchedTopic Add(string topic, int everyDays)
    {
        topic = topic.Trim();
        if (topic.Length is < 2 or > 120) throw new ArgumentException("The topic should be 2–120 characters.");
        everyDays = Math.Clamp(everyDays, 1, 30);
        var existing = List().FirstOrDefault(t => t.Topic.Equals(topic, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Exec("UPDATE watched_topics SET every_days = $d WHERE id = $id;", c => { c.Parameters.AddWithValue("$id", existing.Id); c.Parameters.AddWithValue("$d", everyDays); });
            return Get(existing.Id)!;
        }
        if (List().Count >= MaxTopics) throw new ArgumentException($"You're already following {MaxTopics} topics; stop one first.");
        var id = Guid.NewGuid().ToString("n");
        // The first look happens soon (the user just asked), then on the schedule.
        Exec("INSERT INTO watched_topics(id, topic, every_days, created_at, next_run) VALUES ($id, $t, $d, $now, $now);", c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$t", topic);
            c.Parameters.AddWithValue("$d", everyDays);
            c.Parameters.AddWithValue("$now", JarvisDatabase.Now());
        });
        return Get(id)!;
    }

    public bool Remove(string idOrTopic)
    {
        var t = Get(idOrTopic) ?? List().FirstOrDefault(x => x.Topic.Equals(idOrTopic.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? List().FirstOrDefault(x => x.Topic.Contains(idOrTopic.Trim(), StringComparison.OrdinalIgnoreCase));
        if (t is null) return false;
        return Exec("DELETE FROM watched_topics WHERE id = $id;", c => c.Parameters.AddWithValue("$id", t.Id)) > 0;
    }

    public WatchedTopic? Get(string id) => List().FirstOrDefault(t => t.Id == id);

    public IReadOnlyList<WatchedTopic> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, topic, every_days, created_at, last_run, next_run, last_result FROM watched_topics ORDER BY created_at;";
        using var r = cmd.ExecuteReader();
        var list = new List<WatchedTopic>();
        while (r.Read())
            list.Add(new WatchedTopic(r.GetString(0), r.GetString(1), r.GetInt32(2), DateTimeOffset.Parse(r.GetString(3)),
                r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4)), DateTimeOffset.Parse(r.GetString(5)), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    /// <summary>Researches every topic that is due. Returns how many were run.</summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!connectivity.IsOnline) return 0; // try again when back online, nothing is queued
        var due = List().Where(t => t.NextRun <= now).ToList();
        var executor = sp.GetRequiredService<ToolExecutor>();
        foreach (var t in due)
        {
            var known = ResearchNoteIds();
            var ctx = new ToolContext
            {
                Settings = settings.Current, ConversationId = $"watch-{t.Id}", Via = "scheduler", CancellationToken = ct,
                Lang = settings.Current.General.Language == "ar" ? Language.Lang.Ar : Language.Lang.En,
            };
            string result;
            try
            {
                var (r, _) = await executor.ExecuteAsync("research_topic", ToolArgs.From(new { topic = t.Topic, sources = 3 }), ctx, allowQueue: false).ConfigureAwait(false);
                result = r.Success ? await ReportAsync(t, r, known, ct).ConfigureAwait(false) : r.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { result = ex.Message; }
            Exec("UPDATE watched_topics SET last_run = $now, next_run = $next, last_result = $r WHERE id = $id;", c =>
            {
                c.Parameters.AddWithValue("$id", t.Id);
                c.Parameters.AddWithValue("$now", JarvisDatabase.Format(now));
                c.Parameters.AddWithValue("$next", JarvisDatabase.Format(now.AddDays(t.EveryDays)));
                c.Parameters.AddWithValue("$r", result.Length > 500 ? result[..500] : result);
            });
        }
        return due.Count;
    }

    /// <summary>Counts what's genuinely new (facts already known are just refreshed) and tells the user if anything is.</summary>
    private async Task<string> ReportAsync(WatchedTopic t, ToolResult r, IReadOnlySet<string> known, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToElement(r.Data);
        int fresh = 0, conflicts = 0;
        if (data.TryGetProperty("facts", out var facts))
            foreach (var f in facts.EnumerateArray())
            {
                var id = f.TryGetProperty("memoryId", out var m) ? m.GetString() : null;
                if (id is null || known.Contains(id)) continue; // already known: research only refreshed it
                fresh++;
                if (f.TryGetProperty("conflict", out var c) && c.ValueKind == JsonValueKind.String) conflicts++;
            }
        if (fresh == 0) return "Nothing new.";
        await notifications.PostAsync(new Notification
        {
            Title = $"New about {t.Topic}",
            Body = $"{fresh} new fact(s){(conflicts > 0 ? $", {conflicts} contradicting what I had" : "")} — review them in Memory → To review.",
            Source = "watch",
            GroupKey = "watch:" + t.Id,
        }, ct).ConfigureAwait(false);
        return $"{fresh} new fact(s), {conflicts} conflict(s).";
    }

    private HashSet<string> ResearchNoteIds()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM memories WHERE tags LIKE '%' || $t || '%';";
        cmd.Parameters.AddWithValue("$t", KnowledgeIngestion.Tag);
        using var r = cmd.ExecuteReader();
        var ids = new HashSet<string>();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    private int Exec(string sql, Action<Microsoft.Data.Sqlite.SqliteCommand> bind)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        return cmd.ExecuteNonQuery();
    }
}

public sealed class TopicWatchTool(TopicWatch watch) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "research_watch",
        Category = "learning",
        Description = "Follow a topic: JARVIS researches it again every few days and tells the user when there are new facts (saved unconfirmed, with sources). Use for 'keep me updated on…'. To stop, use research_unwatch.",
        Parameters =
        [
            new("topic", "string", "What to follow.", true),
            new("every_days", "integer", "How often to look (1-30 days, default 7)."),
        ],
    };

    protected override string Describe(ToolArgs args) => $"Follow: {args.GetString("topic")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        try
        {
            var t = watch.Add(args.RequireString("topic"), args.GetInt("every_days") ?? 7);
            return Task.FromResult(ToolResult.Ok(ctx.T($"I'll look into “{t.Topic}” every {t.EveryDays} day(s), starting shortly, and tell you when there's something new.",
                                                     $"هبحث عن «{t.Topic}» كل {t.EveryDays} يوم، وهبدأ قريب، وهقولك لما يكون فيه جديد."), new { t.Id, t.Topic, t.EveryDays }));
        }
        catch (ArgumentException ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
    }
}

public sealed class TopicUnwatchTool(TopicWatch watch) : ToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "research_unwatch",
        Category = "learning",
        Description = "Stop following a topic.",
        Parameters = [new("topic", "string", "The topic (or part of it).", true)],
    };

    protected override string Describe(ToolArgs args) => $"Stop following: {args.GetString("topic")}";

    public override Task<ToolResult> ExecuteAsync(ToolArgs args, ToolContext ctx)
    {
        var topic = args.RequireString("topic");
        return Task.FromResult(watch.Remove(topic)
            ? ToolResult.Ok(ctx.T($"Stopped following “{topic}”.", $"وقفت متابعة «{topic}»."))
            : ToolResult.Fail(ctx.T($"I'm not following anything called “{topic}”.", $"مش متابع حاجة اسمها «{topic}»."), status: ToolStatus.NotFound));
    }
}
