using System.Text.RegularExpressions;
using Jarvis.Core.Agenda;
using Jarvis.Core.Files;
using Jarvis.Core.Language;

namespace Jarvis.Core.Meetings;

public sealed record ActionItem(string Text, string? Owner, DateTimeOffset? Due);

/// <summary>What came out of a meeting. Everything is quoted or extracted from the transcript — nothing invented.</summary>
public sealed record MeetingNotesResult(
    IReadOnlyList<string> KeyPoints,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<ActionItem> ActionItems,
    IReadOnlyList<string> OpenQuestions);

/// <summary>
/// Pulls decisions, action items (with owner and due date when said) and open questions out of a
/// transcript with EN/AR phrase rules. A model can write a nicer summary on top; this is the honest base.
/// </summary>
public static partial class MeetingNotes
{
    public static MeetingNotesResult Extract(string transcript, DateTimeOffset now, IReadOnlyCollection<string>? knownPeople = null)
    {
        var sentences = Sentences(transcript);
        var decisions = new List<string>();
        var actions = new List<ActionItem>();
        var questions = new List<string>();
        foreach (var s in sentences)
        {
            var n = TextNormalizer.Normalize(s);
            if (Decision().IsMatch(n)) { decisions.Add(s); continue; }
            var a = Action().Match(n);
            if (a.Success)
            {
                var when = DatePhrases.Parse(n, now);
                actions.Add(new ActionItem(s, Owner(s, a, knownPeople), when.Day is null && when.Time is null ? null : when.Start(now)));
                continue;
            }
            if ((s.EndsWith('?') || s.EndsWith('؟')) && s.Length > 12) questions.Add(s);
        }
        var key = TextAnalysis.KeySentences(string.Join(" ", sentences), 5);
        return new MeetingNotesResult(key, decisions.Distinct().Take(15).ToList(), actions.DistinctBy(x => x.Text).Take(25).ToList(), questions.Distinct().Take(10).ToList());
    }

    private static string? Owner(string original, Match m, IReadOnlyCollection<string>? known)
    {
        if (m.Groups["self"].Success && m.Groups["self"].Value.Length > 0) return "you";
        if (m.Groups["who"].Success && m.Groups["who"].Value.Length > 0)
        {
            var who = m.Groups["who"].Value.Trim();
            var match = known?.FirstOrDefault(k => TextNormalizer.Normalize(k).StartsWith(who, StringComparison.OrdinalIgnoreCase));
            return match ?? (who.Length > 1 ? char.ToUpper(who[0]) + who[1..] : who);
        }
        return null;
    }

    /// <summary>Splits a transcript ("[02:15] text" lines or plain text) into sentences without timestamps.</summary>
    internal static IReadOnlyList<string> Sentences(string transcript)
    {
        var text = Regex.Replace(transcript, @"^\[\d{1,2}:\d{2}(?::\d{2})?\]\s*", "", RegexOptions.Multiline).ReplaceLineEndings(" ");
        return Regex.Split(text, @"(?<=[.!?؟])\s+")
            .Select(x => Regex.Replace(x, @"\s+", " ").Trim())
            .Where(x => x.Length >= 8)
            .ToList();
    }

    [GeneratedRegex(@"\b(?:we (?:decided|agreed|will go with|are going with|chose)|(?:it's|it is) decided|decision is|let's go with|final answer is|agreed to|we'll go with)\b|اتفقنا|قررنا|القرار|هنمشي ب|استقرينا", RegexOptions.IgnoreCase)]
    private static partial Regex Decision();

    [GeneratedRegex(@"^(?:(?<self>i(?:'ll| will| am going to|'m going to)|i need to|i have to|let me)\b|(?!(?:it|this|that|there|which|what|who|nobody|everything|nothing|he|she|they|you)\b)(?<who>[a-z؀-ۿ]{2,20}) (?:will|is going to|needs to|has to|should|to)\b|(?:we(?:'ll| will| need to| have to| should)|action item|todo|to do|follow up|next step)\b|(?:can|could) you (?:please )?(?:send|share|prepare|review|check|call|update|book|finish)\b|(?<self>هبعت|هعمل|هكلم|هجهز|هراجع|لازم ابعت|لازم اعمل)|(?:لازم|مطلوب|محتاجين|هنعمل|هنبعت|هنجهز)|(?<who>[؀-ۿ]{2,20}) (?:هيبعت|هيعمل|هيكلم|هيجهز|هيراجع|هتبعت|هتعمل|هتجهز))", RegexOptions.IgnoreCase)]
    private static partial Regex Action();
}
