using System.Text.RegularExpressions;
using Jarvis.Core.Language;

namespace Jarvis.Core.Files;

public sealed record TextDiff(int Added, int Removed, int Unchanged, IReadOnlyList<string> AddedLines, IReadOnlyList<string> RemovedLines)
{
    public bool Identical => Added == 0 && Removed == 0;
}

/// <summary>
/// No-AI text analysis for documents: extractive key sentences (labelled as extracted, not written
/// by a model) and line-level comparison between two versions.
/// </summary>
public static partial class TextAnalysis
{
    /// <summary>The most representative sentences, in document order.</summary>
    public static IReadOnlyList<string> KeySentences(string text, int count = 5)
    {
        var sentences = SentenceSplit().Split(text.ReplaceLineEndings(" "))
            .Select(s => s.Trim())
            .Where(s => s.Length is >= 30 and <= 400 && s.Count(char.IsLetter) > s.Length / 2)
            .Distinct()
            .ToList();
        if (sentences.Count <= count) return sentences;

        var freq = new Dictionary<string, int>();
        foreach (var s in sentences)
            foreach (var w in Words(s))
                freq[w] = freq.GetValueOrDefault(w) + 1;
        var top = freq.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value).Take(40).Select(kv => kv.Key).ToHashSet();

        return sentences
            .Select((s, i) => (s, i, Score: Words(s).Count(top.Contains) / Math.Sqrt(Math.Max(4, Words(s).Count())) + (i < 3 ? 0.4 : 0)))
            .OrderByDescending(x => x.Score)
            .Take(count)
            .OrderBy(x => x.i)
            .Select(x => x.s)
            .ToList();
    }

    /// <summary>Line diff via longest common subsequence (bounded for very large documents).</summary>
    public static TextDiff Compare(string oldText, string newText)
    {
        var a = Lines(oldText);
        var b = Lines(newText);
        if (a.Count * (long)b.Count > 4_000_000)
        {
            // Too big for LCS: compare as sets of lines (good enough to report what changed).
            var setA = a.ToHashSet();
            var setB = b.ToHashSet();
            var added = b.Where(l => !setA.Contains(l)).ToList();
            var removed = a.Where(l => !setB.Contains(l)).ToList();
            return new TextDiff(added.Count, removed.Count, b.Count - added.Count, added, removed);
        }
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var addedLines = new List<string>();
        var removedLines = new List<string>();
        int x = 0, y = 0, same = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y]) { same++; x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) removedLines.Add(a[x++]);
            else addedLines.Add(b[y++]);
        }
        while (x < a.Count) removedLines.Add(a[x++]);
        while (y < b.Count) addedLines.Add(b[y++]);
        return new TextDiff(addedLines.Count, removedLines.Count, same, addedLines, removedLines);
    }

    /// <summary>"Proposal v2 (final).docx" → "proposal": the stem shared by versions of a document.</summary>
    public static string VersionStem(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Replace('_', ' ');
        stem = VersionTokens().Replace(stem, " ");
        return Regex.Replace(stem, @"[\s_\-.()\[\]]+", " ").Trim();
    }

    private static List<string> Lines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Select(l => Regex.Replace(l.Trim(), @"\s+", " ")).Where(l => l.Length > 0).ToList();

    private static IEnumerable<string> Words(string s) =>
        WordRegex().Matches(TextNormalizer.Normalize(s)).Select(m => m.Value).Where(w => w.Length > 3 && !Stop.Contains(w));

    private static readonly HashSet<string> Stop = ["this", "that", "with", "from", "have", "will", "your", "they", "their", "which", "would", "there", "been", "were", "also", "into", "about", "when", "what",
        "هذا", "هذه", "التي", "الذي", "على", "الى", "إلى", "كان", "كانت", "عشان", "علشان", "اللي"];

    [GeneratedRegex(@"(?<=[.!?؟。])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\b(?:v|ver|version|rev)\s*\d+(?:\.\d+)*\b|\(\d+\)|\b(?:final|draft|copy|new|old|updated|latest|edited|signed)\b|-\s*copy|\b\d{4}[-_.]\d{2}[-_.]\d{2}\b|\b\d{8}\b|نسخه|نهائي|معدل")]
    private static partial Regex VersionTokens();
}
