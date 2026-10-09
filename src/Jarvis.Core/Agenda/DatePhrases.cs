using System.Globalization;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Agenda;

/// <summary>A date and/or time found in a sentence, and the sentence without it.</summary>
public sealed record WhenPhrase(DateOnly? Day, TimeOnly? Time, TimeSpan? Duration, string Rest)
{
    public DateTimeOffset? Start(DateTimeOffset now)
    {
        if (Day is null && Time is null) return null;
        var local = now.ToLocalTime();
        var day = Day ?? DateOnly.FromDateTime(local.DateTime);
        var time = Time ?? new TimeOnly(9, 0);
        var dt = day.ToDateTime(time);
        var start = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        // "at 3" with no day, already past today → tomorrow.
        if (Day is null && start <= now) start = start.AddDays(1);
        return start;
    }
}

/// <summary>
/// Days, times and durations in English and Egyptian Arabic: "tomorrow at 3pm for an hour",
/// "next Sunday 10:30", "on 14 Oct", "بكره الساعة ٣ العصر", "يوم الأحد الجاي الساعة 11".
/// Works on text that went through <see cref="Language.TextNormalizer"/>.
/// </summary>
public static partial class DatePhrases
{
    private static readonly Dictionary<string, DayOfWeek> Days = new()
    {
        ["sunday"] = DayOfWeek.Sunday, ["monday"] = DayOfWeek.Monday, ["tuesday"] = DayOfWeek.Tuesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["friday"] = DayOfWeek.Friday, ["saturday"] = DayOfWeek.Saturday,
        ["sun"] = DayOfWeek.Sunday, ["mon"] = DayOfWeek.Monday, ["tue"] = DayOfWeek.Tuesday, ["wed"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["fri"] = DayOfWeek.Friday, ["sat"] = DayOfWeek.Saturday,
        ["الاحد"] = DayOfWeek.Sunday, ["الاتنين"] = DayOfWeek.Monday, ["الاثنين"] = DayOfWeek.Monday, ["التلات"] = DayOfWeek.Tuesday, ["الثلاثاء"] = DayOfWeek.Tuesday,
        ["الاربع"] = DayOfWeek.Wednesday, ["الاربعاء"] = DayOfWeek.Wednesday, ["الخميس"] = DayOfWeek.Thursday, ["الجمعه"] = DayOfWeek.Friday, ["السبت"] = DayOfWeek.Saturday,
    };

    private static readonly Dictionary<string, int> Months = new()
    {
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6, ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12,
        ["يناير"] = 1, ["فبراير"] = 2, ["مارس"] = 3, ["ابريل"] = 4, ["مايو"] = 5, ["يونيو"] = 6, ["يوليو"] = 7, ["اغسطس"] = 8, ["سبتمبر"] = 9, ["اكتوبر"] = 10, ["نوفمبر"] = 11, ["ديسمبر"] = 12,
    };

    public static WhenPhrase Parse(string text, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        var rest = " " + ToWesternDigits(text) + " ";
        DateOnly? day = null;
        TimeOnly? time = null;
        TimeSpan? duration = null;

        var m = RelativeDay().Match(rest);
        if (m.Success)
        {
            day = m.Groups[1].Value switch
            {
                "today" or "tonight" or "this evening" or "this afternoon" or "this morning" or "النهارده" or "انهارده" or "النهاردا" => today,
                "tomorrow" or "بكره" or "بكرا" => today.AddDays(1),
                _ => today.AddDays(2), // day after tomorrow / بعد بكره
            };
            rest = Cut(rest, m);
        }
        else if ((m = WeekDay().Match(rest)).Success && Days.TryGetValue(m.Groups["d"].Value, out var dow))
        {
            var next = m.Groups["next"].Success && m.Groups["next"].Value.Length > 0;
            var delta = ((int)dow - (int)today.DayOfWeek + 7) % 7;
            // "next Sunday" said on a Sunday means a week from today; otherwise a day name is its coming occurrence.
            if (delta == 0 && next) delta = 7;
            day = today.AddDays(delta);
            rest = Cut(rest, m);
        }
        else if ((m = DayMonth().Match(rest)).Success)
        {
            var d = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
            var mon = Months[MonthKey(m.Groups["mon"].Value)];
            day = Roll(today, mon, d);
            rest = Cut(rest, m);
        }
        else if ((m = MonthDay().Match(rest)).Success)
        {
            var d = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
            var mon = Months[MonthKey(m.Groups["mon"].Value)];
            day = Roll(today, mon, d);
            rest = Cut(rest, m);
        }
        else if ((m = NumericDate().Match(rest)).Success)
        {
            var d = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            var mon = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            if (mon is >= 1 and <= 12 && d is >= 1 and <= 31) { day = Roll(today, mon, d); rest = Cut(rest, m); }
        }

        if ((m = Clock().Match(rest)).Success)
        {
            var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            var min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            var suffix = m.Groups["ampm"].Value.Trim();
            var pm = suffix is "pm" or "p.m." or "م" or "بالليل" or "المسا" or "مساء" or "العصر" or "بعد الضهر" or "الضهر" && h < 12;
            var am = suffix is "am" or "a.m." or "ص" or "الصبح" or "صباحا";
            if (pm) h += 12;
            if (am && h == 12) h = 0;
            // No am/pm: office hours are the likely meaning ("at 3" = 15:00, "at 10" = 10:00).
            if (!pm && !am && h is >= 1 and <= 7) h += 12;
            if (h <= 23 && min <= 59) { time = new TimeOnly(h, min); rest = Cut(rest, m); }
        }
        else if ((m = Noon().Match(rest)).Success)
        {
            time = new TimeOnly(12, 0);
            rest = Cut(rest, m);
        }

        if ((m = Duration().Match(rest)).Success)
        {
            var n = m.Groups["n"].Success && m.Groups["n"].Value.Length > 0
                ? double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)
                : m.Groups["half"].Success && m.Groups["half"].Value.Length > 0 ? 0.5 : 1;
            var unit = m.Groups["u"].Value;
            duration = unit.StartsWith("min") || unit.StartsWith("دقي") || unit.StartsWith("دقا") ? TimeSpan.FromMinutes(n) : TimeSpan.FromHours(unit == "ساعتين" ? 2 : n);
            rest = Cut(rest, m);
        }

        return new WhenPhrase(day, time, duration, Regex.Replace(rest, @"\s+", " ").Trim());
    }

    /// <summary>The day range a question is about: "today", "tomorrow", "this week", "on Friday"…</summary>
    public static (DateTimeOffset From, DateTimeOffset To, string Label) Range(string text, DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var startOfToday = new DateTimeOffset(local.Date, local.Offset);
        var t = " " + text + " ";
        if (Regex.IsMatch(t, @"\b(?:this|the) week\b|الاسبوع ده|الاسبوع دا|الاسبوع"))
            return (now, startOfToday.AddDays(7), "week");
        var p = Parse(text, now);
        if (p.Day is { } d)
        {
            var from = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), local.Offset);
            return (from < now && d == DateOnly.FromDateTime(local.DateTime) ? now : from, from.AddDays(1), d == DateOnly.FromDateTime(local.DateTime) ? "today" : d.ToString("dddd d MMM", CultureInfo.InvariantCulture));
        }
        return (now, startOfToday.AddDays(1), "today");
    }

    private static DateOnly Roll(DateOnly today, int month, int day)
    {
        var year = today.Year;
        day = Math.Min(day, DateTime.DaysInMonth(year, month));
        var d = new DateOnly(year, month, day);
        return d < today.AddDays(-1) ? new DateOnly(year + 1, month, Math.Min(day, DateTime.DaysInMonth(year + 1, month))) : d;
    }

    private static string MonthKey(string s) => s.Length > 3 && !char.IsAsciiLetter(s[0]) ? s : s[..3].ToLowerInvariant();

    private static string Cut(string s, Match m) => s.Remove(m.Index, m.Length).Insert(m.Index, " ");

    private static string ToWesternDigits(string s) =>
        string.Concat(s.Select(c => c is >= '٠' and <= '٩' ? (char)('0' + (c - '٠')) : c));

    [GeneratedRegex(@"\s(today|tonight|this (?:morning|afternoon|evening)|tomorrow|(?:the )?day after tomorrow|النهارده|انهارده|النهاردا|بعد بكره|بعد بكرا|بكره|بكرا)(?=\s)")]
    private static partial Regex RelativeDay();

    [GeneratedRegex(@"\s(?:(?:on|this|يوم)\s+)?(?<next>next\s+)?(?<d>sunday|monday|tuesday|wednesday|thursday|friday|saturday|sun|mon|tue|wed|thu|fri|sat|الاحد|الاتنين|الاثنين|التلات|الثلاثاء|الاربعاء|الاربع|الخميس|الجمعه|السبت)(?<next>\s+(?:الجاي|اللي جاي))?(?=\s)")]
    private static partial Regex WeekDay();

    [GeneratedRegex(@"\s(?:on\s+|يوم\s+)?(?<day>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?(?<mon>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|يناير|فبراير|مارس|ابريل|مايو|يونيو|يوليو|اغسطس|سبتمبر|اكتوبر|نوفمبر|ديسمبر)(?=\s)")]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"\s(?:on\s+)?(?<mon>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+(?<day>\d{1,2})(?:st|nd|rd|th)?(?=\s)")]
    private static partial Regex MonthDay();

    [GeneratedRegex(@"\s(?:on\s+|يوم\s+)?(?<d>\d{1,2})/(?<m>\d{1,2})(?=\s)")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"\s(?:at\s+|@\s*|الساعه\s+|ساعه\s+)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ampm>am|pm|a\.m\.|p\.m\.|الصبح|صباحا|بالليل|المسا|مساء|العصر|بعد الضهر|الضهر|ص|م)(?=\s)|\s(?:at\s+|الساعه\s+)(?<h>\d{1,2})(?::(?<m>\d{2}))?(?=\s)|\s(?<h>\d{1,2}):(?<m>\d{2})(?=\s)")]
    private static partial Regex Clock();

    [GeneratedRegex(@"\s(?:at\s+)?(?:noon|midday)(?=\s)|\sالضهر(?=\s)")]
    private static partial Regex Noon();

    [GeneratedRegex(@"\s(?:for\s+(?:an?\s+|(?<n>\d+(?:\.\d+)?)\s*|(?<half>half an?\s+))?(?<u>hours?|hrs?|minutes?|mins?)|لمده\s+(?:(?<n>\d+)\s*)?(?<half>نص\s+)?(?<u>ساعتين|ساعه|ساعات|دقيقه|دقايق))(?=\s)")]
    private static partial Regex Duration();
}
