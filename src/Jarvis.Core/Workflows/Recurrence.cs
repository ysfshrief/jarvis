using System.Globalization;
using System.Text.RegularExpressions;
using Jarvis.Core.Language;

namespace Jarvis.Core.Workflows;

/// <summary>
/// Simple, readable repeat rules: "daily", "weekdays", "weekly", "weekly:mon,thu", "monthly",
/// "every:3d". Parsed from English or Egyptian Arabic ("every Monday", "كل يوم", "كل شهر").
/// </summary>
public static partial class Recurrence
{
    private static readonly string[] Days = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    public static bool IsValid(string? rule) => rule is not null && Next(rule, DateTimeOffset.Now) is not null;

    /// <summary>The next occurrence strictly after <paramref name="after"/> (keeping its time of day).</summary>
    public static DateTimeOffset? Next(string rule, DateTimeOffset after)
    {
        var r = rule.Trim().ToLowerInvariant();
        switch (r)
        {
            case "daily": return after.AddDays(1);
            case "weekly": return after.AddDays(7);
            case "monthly": return after.AddMonths(1);
            case "yearly": return after.AddYears(1);
            case "weekdays":
            {
                var d = after.AddDays(1);
                while (d.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) d = d.AddDays(1); // Egyptian work week: Sun–Thu
                return d;
            }
        }
        if (r.StartsWith("weekly:", StringComparison.Ordinal))
        {
            var wanted = r[7..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Array.IndexOf(Days, x[..Math.Min(3, x.Length)])).Where(i => i >= 0).ToHashSet();
            if (wanted.Count == 0) return null;
            for (var i = 1; i <= 7; i++)
            {
                var d = after.AddDays(i);
                if (wanted.Contains((int)d.DayOfWeek)) return d;
            }
        }
        var m = EveryN().Match(r);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n is > 0 and <= 365)
            return m.Groups[2].Value == "w" ? after.AddDays(7 * n) : after.AddDays(n);
        return null;
    }

    /// <summary>Finds a repeat phrase in text and returns the rule plus the text without it.</summary>
    public static (string? Rule, string Remaining) Extract(string text)
    {
        var norm = TextNormalizer.Normalize(text).TrimEnd('.', '!', ' ');
        // A repeat phrase at the end wins ("send the weekly report every week" repeats weekly, and keeps "weekly" in the title).
        foreach (var (pattern, rule) in Phrases.Select(p => (Pattern: $"(?:{p.Pattern})$", p.Rule)).Concat(Phrases))
        {
            var m = Regex.Match(norm, pattern);
            if (!m.Success) continue;
            var r = rule;
            if (rule == "weekly:$day")
            {
                var day = m.Groups["d"].Value;
                var idx = DayIndex(day);
                if (idx < 0) continue;
                r = "weekly:" + Days[idx];
            }
            if (rule == "every:$n")
            {
                r = $"every:{m.Groups["n"].Value}{(m.Groups["u"].Value.StartsWith('w') || m.Groups["u"].Value.StartsWith("اسب") ? "w" : "d")}";
            }
            // Remove the phrase from the original text by position in the normalized string (same length mapping is
            // not guaranteed, so cut by searching the original for the matched words).
            var rest = RemovePhrase(text, m.Value);
            return (r, rest);
        }
        return (null, text);
    }

    public static string Describe(string rule, bool arabic = false)
    {
        var r = rule.ToLowerInvariant();
        if (arabic)
        {
            return r switch
            {
                "daily" => "كل يوم", "weekdays" => "كل أيام الشغل", "weekly" => "كل أسبوع", "monthly" => "كل شهر", "yearly" => "كل سنة",
                _ when r.StartsWith("weekly:") => "كل " + string.Join("، ", r[7..].Split(',').Select(d => ArabicDay(d))),
                _ when r.StartsWith("every:") => $"كل {r[6..^1]} {(r.EndsWith('w') ? "أسابيع" : "أيام")}",
                _ => r,
            };
        }
        return r switch
        {
            "daily" => "every day", "weekdays" => "every working day (Sun–Thu)", "weekly" => "every week", "monthly" => "every month", "yearly" => "every year",
            _ when r.StartsWith("weekly:") => "every " + string.Join(", ", r[7..].Split(',').Select(d => CultureInfo.InvariantCulture.DateTimeFormat.DayNames[Array.IndexOf(Days, d)])),
            _ when r.StartsWith("every:") => $"every {r[6..^1]} {(r.EndsWith('w') ? "weeks" : "days")}",
            _ => r,
        };
    }

    private static readonly (string Pattern, string Rule)[] Phrases =
    [
        (@"\bevery (?<n>\d{1,3}) (?<u>days?|weeks?)\b", "every:$n"),
        (@"\bevery (?:working day|weekday|work day)\b|كل يوم شغل|كل ايام الشغل", "weekdays"),
        (@"\b(?:every ?day|daily)\b|كل يوم\b|يوميا", "daily"),
        (@"\bevery (?<d>sunday|monday|tuesday|wednesday|thursday|friday|saturday)\b|كل (?<d>حد|احد|اتنين|اثنين|تلات|ثلاثاء|اربع|اربعاء|خميس|جمعه|سبت)\b", "weekly:$day"),
        (@"\b(?:every week|weekly)\b|كل اسبوع|اسبوعيا", "weekly"),
        (@"\b(?:every month|monthly)\b|كل شهر|شهريا", "monthly"),
        (@"\b(?:every year|yearly|annually)\b|كل سنه|سنويا", "yearly"),
        (@"كل (?<n>\d{1,3}) (?<u>ايام|يوم|اسابيع|اسبوع)", "every:$n"),
    ];

    private static int DayIndex(string d) => d switch
    {
        "sunday" or "حد" or "احد" => 0, "monday" or "اتنين" or "اثنين" => 1, "tuesday" or "تلات" or "ثلاثاء" => 2,
        "wednesday" or "اربع" or "اربعاء" => 3, "thursday" or "خميس" => 4, "friday" or "جمعه" => 5, "saturday" or "سبت" => 6, _ => -1,
    };

    private static string ArabicDay(string d) => d switch
    {
        "sun" => "حد", "mon" => "اتنين", "tue" => "تلات", "wed" => "أربع", "thu" => "خميس", "fri" => "جمعة", "sat" => "سبت", _ => d,
    };

    private static string RemovePhrase(string original, string normalizedMatch)
    {
        // Try a case-insensitive removal; Arabic normalization may differ, so fall back to word-by-word.
        var idx = original.IndexOf(normalizedMatch, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return Collapse(original.Remove(idx, normalizedMatch.Length));
        var words = normalizedMatch.Split(' ');
        var parts = original.Split(' ').ToList();
        for (var i = 0; i + words.Length <= parts.Count; i++)
        {
            if (Enumerable.Range(0, words.Length).All(j => TextNormalizer.Normalize(parts[i + j]).Trim('.', ',', '،') == words[j]))
            {
                parts.RemoveRange(i, words.Length);
                return Collapse(string.Join(' ', parts));
            }
        }
        return original;
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s{2,}", " ").Trim(' ', ',', '،', '.');

    [GeneratedRegex(@"^every:(\d{1,3})([dw])$")]
    private static partial Regex EveryN();
}
