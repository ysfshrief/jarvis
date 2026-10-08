using System.Globalization;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Scheduling;

public sealed record TimeExpression(DateTimeOffset When, int Index, int Length, string Text);

/// <summary>
/// Finds relative ("in 10 minutes", "بعد ربع ساعة") and clock ("at 5pm", "الساعة 7") time
/// expressions in English and Egyptian Arabic. Works on text that went through
/// <see cref="Language.TextNormalizer"/>.
/// </summary>
public static partial class TimeExpressionParser
{
    private static readonly Dictionary<string, double> Numbers = new()
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["fifteen"] = 15, ["twenty"] = 20,
        ["thirty"] = 30, ["forty"] = 40, ["forty five"] = 45, ["half an"] = 0.5, ["half a"] = 0.5,
        ["واحد"] = 1, ["واحده"] = 1, ["اتنين"] = 2, ["تلات"] = 3, ["تلاته"] = 3, ["اربع"] = 4, ["اربعه"] = 4,
        ["خمس"] = 5, ["خمسه"] = 5, ["ست"] = 6, ["سته"] = 6, ["سبع"] = 7, ["سبعه"] = 7, ["تمن"] = 8, ["تمانيه"] = 8,
        ["تسع"] = 9, ["تسعه"] = 9, ["عشر"] = 10, ["عشره"] = 10, ["ربع"] = 0.25, ["نص"] = 0.5, ["تلت"] = 1.0 / 3,
        ["عشرين"] = 20, ["تلاتين"] = 30, ["اربعين"] = 40,
    };

    public static TimeExpression? Find(string text, DateTimeOffset now)
    {
        var rel = RelativeRegex().Match(text);
        if (rel.Success && TryUnit(rel.Groups["unit"].Value, out var unitSeconds))
        {
            var numText = rel.Groups["num"].Value.Trim();
            double count = 1;
            if (numText.Length > 0 && !double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out count) &&
                !Numbers.TryGetValue(numText, out count))
                count = 1;
            // Arabic dual forms carry their own count ("دقيقتين" = two minutes).
            if (rel.Groups["unit"].Value is "دقيقتين" or "ساعتين" or "يومين" or "ثانيتين") count = 2;
            var when = now.AddSeconds(count * unitSeconds);
            return new TimeExpression(when, rel.Index, rel.Length, rel.Value);
        }

        var clock = ClockRegex().Match(text);
        if (clock.Success)
        {
            var hour = int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minute = clock.Groups["m"].Success ? int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            var suffix = clock.Groups["ampm"].Value;
            if (hour > 23 || minute > 59) return null;
            var explicitPm = suffix is "pm" or "p.m." or "م" or "بالليل" or "المسا" or "مساء" or "العصر" or "بعد الضهر";
            var explicitAm = suffix is "am" or "a.m." or "ص" or "الصبح" or "صباحا";
            if (explicitPm && hour < 12) hour += 12;
            if (explicitAm && hour == 12) hour = 0;

            var local = now.ToLocalTime();
            var candidate = new DateTimeOffset(local.Year, local.Month, local.Day, hour, minute, 0, local.Offset);
            if (!explicitAm && !explicitPm && hour <= 12)
            {
                // "at 5" with no am/pm: the next 5 o'clock that is still ahead.
                while (candidate <= now) candidate = candidate.AddHours(12);
            }
            else if (candidate <= now)
            {
                candidate = candidate.AddDays(1);
            }
            return new TimeExpression(candidate, clock.Index, clock.Length, clock.Value);
        }
        return null;
    }

    private static bool TryUnit(string unit, out double seconds)
    {
        seconds = unit switch
        {
            "second" or "seconds" or "sec" or "secs" or "ثانيه" or "ثواني" or "ثانيتين" => 1,
            "minute" or "minutes" or "min" or "mins" or "دقيقه" or "دقايق" or "دقائق" or "دقيقتين" => 60,
            "hour" or "hours" or "hr" or "hrs" or "ساعه" or "ساعات" or "ساعتين" => 3600,
            "day" or "days" or "يوم" or "ايام" or "يومين" => 86400,
            _ => -1,
        };
        return seconds > 0;
    }

    [GeneratedRegex(@"(?:\b(?:in|after)\s+|(?:بعد|كمان)\s+)(?<num>\d+(?:\.\d+)?|half an?|forty five|an?|one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty|forty|واحده?|اتنين|تلاته?|اربعه?|خمسه?|سته?|سبعه?|تمن|تمانيه|تسعه?|عشره?|ربع|نص|تلت|عشرين|تلاتين|اربعين)?\s*(?<unit>seconds?|secs?|minutes?|mins?|hours?|hrs?|days?|ثانيه|ثواني|ثانيتين|دقيقتين|دقيقه|دقايق|دقائق|ساعتين|ساعات|ساعه|يومين|ايام|يوم)\b")]
    private static partial Regex RelativeRegex();

    [GeneratedRegex(@"(?:\bat\s+|الساعه\s+)(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ampm>am|pm|a\.m\.|p\.m\.|الصبح|صباحا|بالليل|المسا|مساء|العصر|بعد الضهر|ص|م)?(?=\s|$)")]
    private static partial Regex ClockRegex();
}
