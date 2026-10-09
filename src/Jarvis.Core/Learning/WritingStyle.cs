using System.Text.RegularExpressions;
using Jarvis.Core.Language;
using Jarvis.Core.Memory;
using Jarvis.Core.Persistence;

namespace Jarvis.Core.Learning;

/// <summary>Text the user wrote (sent email), kept locally only while writing-style learning is on.</summary>
public sealed class WritingSamples(JarvisDatabase db)
{
    public const int Keep = 200;

    /// <summary>Stores the user's own words (quoted replies and forwarded text removed). False if too short or already stored.</summary>
    public bool Add(string id, string source, string text, DateTimeOffset writtenAt)
    {
        var own = WritingStyle.OwnText(text);
        if (own.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 4) return false;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO writing_samples(id, source, text, written_at) VALUES ($id, $s, $t, $w);
            DELETE FROM writing_samples WHERE id NOT IN (SELECT id FROM writing_samples ORDER BY written_at DESC LIMIT $keep);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", source);
        cmd.Parameters.AddWithValue("$t", own.Length > 4000 ? own[..4000] : own);
        cmd.Parameters.AddWithValue("$w", JarvisDatabase.Format(writtenAt));
        cmd.Parameters.AddWithValue("$keep", Keep);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<(string Text, DateTimeOffset WrittenAt)> Recent(int max = Keep)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT text, written_at FROM writing_samples ORDER BY written_at DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", max);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, DateTimeOffset)>();
        while (r.Read()) list.Add((r.GetString(0), DateTimeOffset.Parse(r.GetString(1))));
        return list;
    }

    public int Count()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM writing_samples;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Clear()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM writing_samples;";
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Describes how the user writes email — greeting, sign-off, length, sentence length, language, tone markers —
/// from their own sent messages. Deterministic and explainable: the evidence says how many emails it's based on.
/// </summary>
public static partial class WritingStyle
{
    public const string Key = "style:writing";
    public const int MinSamples = 5;

    public static LearnedPattern? Analyze(IReadOnlyList<(string Text, DateTimeOffset WrittenAt)> samples)
    {
        var texts = samples.Select(s => OwnText(s.Text)).Where(t => t.Length > 0).ToList();
        if (texts.Count < MinSamples) return null;

        var greetings = texts.Select(Greeting).Where(g => g is not null).GroupBy(g => g!).OrderByDescending(g => g.Count()).FirstOrDefault();
        var signoffs = texts.Select(SignOff).Where(g => g is not null).GroupBy(g => g!).OrderByDescending(g => g.Count()).FirstOrDefault();
        var words = texts.Select(t => Words().Matches(t).Count).OrderBy(n => n).ToList();
        var medianWords = words[words.Count / 2];
        var sentences = texts.SelectMany(t => Sentences().Split(Body(t))).Select(s => Words().Matches(s).Count).Where(n => n > 0).ToList();
        var perSentence = sentences.Count == 0 ? 0 : (int)Math.Round(sentences.Average());
        var arabic = texts.Count(t => LanguageDetector.Detect(t) == Lang.Ar);
        var exclaim = texts.Count(t => t.Contains('!'));
        var emoji = texts.Count(t => Emoji().IsMatch(t));

        var parts = new List<string>();
        if (greetings is not null && greetings.Count() * 3 >= texts.Count) parts.Add($"usually opens with “{greetings.Key}”");
        if (signoffs is not null && signoffs.Count() * 3 >= texts.Count) parts.Add($"signs off with “{signoffs.Key.Replace("\n", " / ")}”");
        parts.Add(medianWords <= 60 ? $"keeps emails short (about {medianWords} words)" : medianWords <= 150 ? $"writes medium-length emails (about {medianWords} words)" : $"writes detailed emails (about {medianWords} words)");
        if (perSentence > 0) parts.Add(perSentence <= 12 ? "uses short sentences" : perSentence <= 22 ? "uses moderate sentences" : "uses long sentences");
        parts.Add(arabic * 5 >= texts.Count * 4 ? "writes in Arabic" : arabic * 5 <= texts.Count ? "writes in English" : $"writes in both English and Arabic ({arabic} of {texts.Count} in Arabic)");
        parts.Add(exclaim * 3 >= texts.Count ? "often uses exclamation marks" : "rarely uses exclamation marks");
        if (emoji * 4 >= texts.Count) parts.Add("uses emoji");
        else if (emoji == 0) parts.Add("never uses emoji");

        var newest = samples.Max(s => s.WrittenAt);
        var content = "When writing emails, the user " + string.Join("; ", parts) + ".";
        var reason = $"Based on {texts.Count} emails you sent (latest {newest:d MMM yyyy})" +
                     (greetings is not null ? $"; greeting seen in {greetings.Count()}" : "") +
                     (signoffs is not null ? $"; sign-off seen in {signoffs.Count()}" : "") + ".";
        return new LearnedPattern(Key, MemoryKinds.Preference, content, reason, Math.Round(Math.Min(0.85, 0.4 + 0.03 * texts.Count), 2));
    }

    /// <summary>The user's own words: quoted replies, forwarded messages and long signatures removed.</summary>
    public static string OwnText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (QuoteStart().IsMatch(line)) break;
            if (line.StartsWith('>')) continue;
            kept.Add(line);
        }
        return string.Join('\n', kept).Trim();
    }

    /// <summary>"Hi Ahmed," → "Hi &lt;name&gt;,"</summary>
    internal static string? Greeting(string text)
    {
        var first = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first is null || Words().Matches(first).Count > 5) return null;
        var m = GreetingRx().Match(first);
        if (!m.Success) return null;
        var rest = first[m.Length..].Trim();
        var punct = rest.Length > 0 && rest[^1] is ',' or '!' or '،' or ':' ? rest[^1].ToString() : first[^1] is ',' or '!' or '،' or ':' ? first[^1].ToString() : "";
        var name = rest.Trim(',', '!', '،', ':', ' ').Length > 0 ? " <name>" : "";
        return $"{Capital(m.Value.Trim())}{name}{punct}";
    }

    internal static string? SignOff(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        for (var i = Math.Max(0, lines.Count - 4); i < lines.Count; i++)
        {
            if (!SignOffRx().IsMatch(lines[i]) || Words().Matches(lines[i]).Count > 5) continue;
            var close = lines[i];
            var name = i + 1 < lines.Count && Words().Matches(lines[i + 1]).Count <= 3 ? "\n" + lines[i + 1] : "";
            return close + name;
        }
        return null;
    }

    private static string Body(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count > 0 && Greeting(text) is not null) lines.RemoveAt(0);
        return string.Join(' ', lines);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex Words();
    [GeneratedRegex(@"(?<=[.!?؟])\s+")]
    private static partial Regex Sentences();
    [GeneratedRegex(@"[☀-➿]|[\uD83C-\uD83E][\uDC00-\uDFFF]")]
    private static partial Regex Emoji();
    [GeneratedRegex(@"^(On .+ wrote:|-{2,}\s*Original Message\s*-{2,}|-{5,}\s*Forwarded message|From: .+|في .+ كتب.*:|من: .+)$", RegexOptions.IgnoreCase)]
    private static partial Regex QuoteStart();
    [GeneratedRegex(@"^(good (morning|afternoon|evening)|hi there|hello|hi|hey|dear|greetings|صباح الخير|مساء الخير|السلام عليكم|أهلا|اهلا|أهلاً|اهلاً|مرحبا|عزيزي|أستاذ|استاذ|يا)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GreetingRx();
    [GeneratedRegex(@"^(best( regards| wishes)?|kind regards|warm regards|regards|many thanks|thanks( again| so much)?|thank you|cheers|sincerely|yours( sincerely| truly)?|talk soon|شكرا|شكراً|تحياتي|مع خالص|مع تحياتي|وشكرا|وشكراً)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SignOffRx();
}
