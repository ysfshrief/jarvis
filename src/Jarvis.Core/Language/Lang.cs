using System.Text;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Language;

/// <summary>Languages JARVIS converses in. Arabic means Egyptian colloquial Arabic.</summary>
public enum Lang { En, Ar }

public static partial class LanguageDetector
{
    /// <summary>
    /// Detects the language of a user utterance by script. Mixed text such as
    /// "Jarvis، افتح VS Code" is Arabic because Arabic letters dominate the instruction.
    /// </summary>
    public static Lang Detect(string? text, Lang fallback = Lang.En)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        int arabicWords = 0, latinWords = 0;
        foreach (var word in text.Split([' ', '\t', '\n', ',', '،', '.', '?', '؟', '!', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Any(IsArabicLetter)) arabicWords++;
            else if (word.Any(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') &&
                     !word.Equals("jarvis", StringComparison.OrdinalIgnoreCase)) latinWords++;
        }

        if (arabicWords == 0 && latinWords == 0) return fallback;
        // Egyptians code-switch: app names and tech terms stay English inside Arabic sentences
        // ("افتح VS Code"), while English sentences rarely contain Arabic words.
        return arabicWords > 0 && arabicWords * 3 >= latinWords ? Lang.Ar : Lang.En;
    }

    public static bool IsArabicLetter(char ch) =>
        ch is >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' or >= '\uFB50' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF';
}

/// <summary>
/// Text normalization shared by intent matching and memory search. Arabic is written
/// inconsistently in casual chat (أ/إ/آ/ا, ة/ه, ى/ي, optional diacritics), so both the
/// stored text and the query are folded to one form.
/// </summary>
public static partial class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.Normalize(NormalizationForm.FormC))
        {
            var ch = raw;
            switch (ch)
            {
                case 'أ' or 'إ' or 'آ' or 'ٱ': ch = 'ا'; break;
                case 'ة': ch = 'ه'; break;
                case 'ى': ch = 'ي'; break;
                case 'ؤ': ch = 'و'; break;
                case 'ئ': ch = 'ي'; break;
                case '،': ch = ','; break;
                case '؟': ch = '?'; break;
                case '؛': ch = ';'; break;
            }
            // Arabic diacritics (tashkeel) and tatweel.
            if (ch is >= '\u064B' and <= '\u0652' || ch == '\u0640' || ch == '\u0670') continue;
            // Arabic-Indic digits to ASCII.
            if (ch is >= '\u0660' and <= '\u0669') ch = (char)('0' + (ch - '\u0660'));
            if (ch is >= '\u06F0' and <= '\u06F9') ch = (char)('0' + (ch - '\u06F0'));
            sb.Append(char.ToLowerInvariant(ch));
        }
        return Whitespace().Replace(sb.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
