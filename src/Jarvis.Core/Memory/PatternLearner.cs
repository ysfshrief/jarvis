using System.Globalization;
using System.Text.RegularExpressions;
using Jarvis.Core.Activity;
using Jarvis.Core.Language;
using Jarvis.Core.Persistence;
using Jarvis.Core.Settings;

namespace Jarvis.Core.Memory;

/// <summary>A behaviour JARVIS noticed, with the evidence for it.</summary>
public sealed record LearnedPattern(string Key, string Kind, string Content, string Reason, double Confidence);

public sealed record LearnResult(int Proposed, int Updated, int SkippedRejected, IReadOnlyList<LearnedPattern> Patterns);

/// <summary>
/// Opt-in personal adaptation (Settings → Memory → "Learn from my activity"). It reads only JARVIS's
/// own activity log, proposes patterns as *learned* memories with the evidence attached, and never
/// turns a guess into a fact: the user confirms (it becomes a fact) or rejects (it is never proposed
/// again). Learned preferences reach the AI only as labelled hints.
/// </summary>
public sealed partial class PatternLearner(ActivityLog activity, KnowledgeService knowledge, JarvisDatabase db, ISettingsStore settings)
{
    public const string TagPrefix = "learn:";
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    public LearnResult Run(DateTimeOffset now, bool force = false)
    {
        if (!force && !settings.Current.Memory.LearnPatterns) return new LearnResult(0, 0, 0, []);
        if (!settings.Current.Memory.AllowedKinds.Contains(MemoryKinds.Pattern) && !settings.Current.Memory.AllowedKinds.Contains(MemoryKinds.Preference))
            return new LearnResult(0, 0, 0, []);

        var entries = activity.Since(now - Window);
        var patterns = Analyze(entries, now);
        int proposed = 0, updated = 0, rejected = 0;
        foreach (var p in patterns)
        {
            var kindAllowed = settings.Current.Memory.AllowedKinds.Contains(p.Kind);
            if (!kindAllowed) continue;
            var state = State(p.Key);
            if (state?.Status == "rejected") { rejected++; continue; }
            var existing = state?.MemoryId is { } mid ? knowledge.Memories.Get(mid) : null;
            var provenance = new MemoryProvenance("learner", Reason: p.Reason);
            if (existing is null)
            {
                var m = knowledge.Remember(new NewMemory(p.Content, p.Kind, null, MemorySources.Learned, p.Confidence, TagPrefix + p.Key, Provenance: provenance));
                SetState(p.Key, "proposed", m.Id, p.Reason);
                proposed++;
            }
            else if (!existing.IsConfirmed && (existing.Content != p.Content || Math.Abs(existing.Confidence - p.Confidence) > 0.05))
            {
                knowledge.Edit(existing.Id, new MemoryUpdate(Content: p.Content, Confidence: p.Confidence));
                SetState(p.Key, "proposed", existing.Id, p.Reason);
                updated++;
            }
        }
        return new LearnResult(proposed, updated, rejected, patterns);
    }

    /// <summary>The user said a learned item is wrong: delete it and never propose it again.</summary>
    public bool Reject(string memoryId)
    {
        var m = knowledge.Memories.Get(memoryId);
        if (m is null) return false;
        var key = m.Tags?.Split(' ', ',').FirstOrDefault(t => t.StartsWith(TagPrefix))?[TagPrefix.Length..];
        if (key is not null) SetState(key, "rejected", null, m.Provenance?.Reason);
        return knowledge.Memories.Delete(memoryId);
    }

    public void MarkConfirmed(string memoryId)
    {
        var m = knowledge.Memories.Get(memoryId);
        var key = m?.Tags?.Split(' ', ',').FirstOrDefault(t => t.StartsWith(TagPrefix))?[TagPrefix.Length..];
        if (key is not null) SetState(key, "confirmed", memoryId, m!.Provenance?.Reason);
    }

    /// <summary>Pure analysis of activity entries (newest first) into candidate patterns.</summary>
    public static IReadOnlyList<LearnedPattern> Analyze(IReadOnlyList<ActivityEntry> entries, DateTimeOffset now)
    {
        var list = new List<LearnedPattern>();
        var requests = entries.Where(e => e.Kind == ActivityKinds.Request).ToList();

        // 1. Apps opened at a regular time of day.
        var opens = entries.Where(e => e.Kind == ActivityKinds.Tool && e.Tool == "app_open" && e.Status == "ok" && e.Summary.StartsWith("Open ", StringComparison.Ordinal))
            .Select(e => (App: e.Summary[5..].Trim(), e.Timestamp)).Where(x => x.App.Length > 0).ToList();
        foreach (var g in opens.GroupBy(x => (App: x.App.ToLowerInvariant(), Hour: x.Timestamp.Hour)))
        {
            var days = g.Select(x => x.Timestamp.Date).Distinct().Count();
            if (g.Count() < 4 || days < 3) continue;
            var app = g.First().App;
            list.Add(new LearnedPattern($"app_time:{g.Key.App}:{g.Key.Hour}", MemoryKinds.Pattern,
                $"Usually opens {app} around {g.Key.Hour:00}:00.",
                $"Opened {app} {g.Count()} times between {g.Key.Hour:00}:00 and {g.Key.Hour + 1:00}:00 on {days} different days in the last 14 days.",
                Math.Min(0.85, 0.4 + 0.08 * days)));
        }

        // 2. Preferred language.
        if (requests.Count >= 20)
        {
            var arabic = requests.Count(r => LanguageDetector.Detect(r.Summary) == Lang.Ar);
            var share = (double)arabic / requests.Count;
            if (share >= 0.8)
                list.Add(new LearnedPattern("language:ar", MemoryKinds.Preference, "Usually talks to JARVIS in Egyptian Arabic.",
                    $"{arabic} of the last {requests.Count} requests were in Arabic.", Math.Round(Math.Min(0.9, share), 2)));
            else if (share <= 0.2)
                list.Add(new LearnedPattern("language:en", MemoryKinds.Preference, "Usually talks to JARVIS in English.",
                    $"{requests.Count - arabic} of the last {requests.Count} requests were in English.", Math.Round(Math.Min(0.9, 1 - share), 2)));
        }

        // 3. Asks for shorter answers.
        var brevity = requests.Where(r => BrevityRegex().IsMatch(TextNormalizer.Normalize(r.Summary))).ToList();
        if (brevity.Count >= 3)
            list.Add(new LearnedPattern("style:brief", MemoryKinds.Preference, "Prefers short, to-the-point answers.",
                $"Asked for shorter or briefer answers {brevity.Count} times in the last 14 days (e.g. “{Trim(brevity[0].Summary)}”).", Math.Min(0.85, 0.45 + 0.1 * brevity.Count)));

        // 4. When the working day usually starts.
        var firstByDay = requests.GroupBy(r => r.Timestamp.Date).Select(g => g.Min(r => r.Timestamp)).OrderBy(t => t.TimeOfDay).ToList();
        if (firstByDay.Count >= 5)
        {
            var median = firstByDay[firstByDay.Count / 2].TimeOfDay;
            var spread = firstByDay.Count(t => Math.Abs((t.TimeOfDay - median).TotalMinutes) <= 60);
            if (spread >= (int)Math.Ceiling(firstByDay.Count * 0.7))
            {
                var rounded = TimeSpan.FromMinutes(Math.Round(median.TotalMinutes / 15) * 15);
                list.Add(new LearnedPattern("routine:start", MemoryKinds.Pattern, $"Usually starts the day with JARVIS around {rounded:hh\\:mm}.",
                    $"First request of the day was within an hour of {rounded:hh\\:mm} on {spread} of {firstByDay.Count} days.", Math.Min(0.8, 0.35 + 0.06 * spread)));
            }
        }

        // 5. Requests repeated often (candidates for a shortcut or workflow).
        foreach (var g in requests.GroupBy(r => TextNormalizer.Normalize(r.Summary).Trim('.', '!', '?', ' ')))
        {
            if (g.Key.Length < 6) continue;
            var days = g.Select(r => r.Timestamp.Date).Distinct().Count();
            if (g.Count() < 5 || days < 3) continue;
            var sample = g.First().Summary;
            list.Add(new LearnedPattern($"repeat:{Short(g.Key)}", MemoryKinds.Pattern, $"Often asks: “{Trim(sample)}”.",
                $"Asked this {g.Count()} times on {days} different days in the last 14 days.", Math.Min(0.8, 0.4 + 0.05 * g.Count())));
        }
        return list;
    }

    private (string Status, string? MemoryId)? State(string key)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, memory_id FROM learner_state WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)) : null;
    }

    private void SetState(string key, string status, string? memoryId, string? evidence)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO learner_state (key, status, memory_id, evidence, updated_at) VALUES ($k, $s, $m, $e, $now)
            ON CONFLICT(key) DO UPDATE SET status = excluded.status, memory_id = excluded.memory_id, evidence = excluded.evidence, updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$m", (object?)memoryId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$e", (object?)evidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
        cmd.ExecuteNonQuery();
    }

    private static string Trim(string s) => s.Length <= 60 ? s : s[..57] + "...";
    private static string Short(string s) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12];

    [GeneratedRegex(@"\b(shorter|briefly|brief|in short|too long|be concise|tl;?dr|keep it short)\b|اختصر|باختصار|قصر|بالمختصر|من غير رغي")]
    private static partial Regex BrevityRegex();
}
