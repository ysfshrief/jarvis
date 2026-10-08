using System.Text.RegularExpressions;
using Jarvis.Core.Language;
using Jarvis.Core.Scheduling;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Agent;

/// <summary>What the deterministic layer understood.</summary>
public abstract record Intent;

/// <summary>Run one tool. If it fails and an AI model is available, the request may be handed to the AI instead.</summary>
/// <param name="PreferAi">Use the AI when one is available (the request needs reasoning); run the tool directly otherwise.</param>
public sealed record ToolIntent(string Tool, ToolArgs Args, bool FallBackToAiOnFailure = false, bool PreferAi = false) : Intent;

/// <summary>Answer directly without any tool (greetings, help, time).</summary>
public sealed record ReplyIntent(string Kind, string? Value = null) : Intent;

/// <summary>Switch the conversation language.</summary>
public sealed record LanguageIntent(Lang Lang) : Intent;

/// <summary>Answer to a pending approval ("yes" / "لأ").</summary>
public sealed record ApprovalAnswerIntent(bool Approve) : Intent;

/// <summary>
/// Fast, offline, AI-free understanding of common commands in English and Egyptian Arabic.
/// The principle: "Open Calculator" must never need a language model. Anything ambiguous or
/// compound ("open my project and tell me why the build fails") is left for the AI.
/// </summary>
public static partial class IntentEngine
{
    /// <summary>Removes the wake word, politeness and punctuation so patterns can stay simple.</summary>
    public static string Clean(string text)
    {
        var t = TextNormalizer.Normalize(text);
        t = t.Trim(' ', '.', '!', '?', ',', ';', ':', '-', '"', '\'');
        for (var i = 0; i < 3; i++)
        {
            var before = t;
            t = LeadingFiller().Replace(t, "");
            t = TrailingFiller().Replace(t, "");
            t = t.Trim(' ', '.', '!', '?', ',', ';', ':', '-');
            if (t == before) break;
        }
        return t;
    }

    public static Intent? Match(string rawText, DateTimeOffset now, bool approvalPending = false)
    {
        var t = Clean(rawText);
        if (t.Length == 0) return null;

        if (approvalPending)
        {
            if (YesRegex().IsMatch(t)) return new ApprovalAnswerIntent(true);
            if (NoRegex().IsMatch(t)) return new ApprovalAnswerIntent(false);
        }

        Match m;
        if ((m = SpeakEnglish().Match(t)).Success) return new LanguageIntent(Lang.En);
        if ((m = SpeakArabic().Match(t)).Success) return new LanguageIntent(Lang.Ar);
        if (Greeting().IsMatch(t)) return new ReplyIntent("greeting");
        if (Help().IsMatch(t)) return new ReplyIntent("help");
        if (AreYouThere().IsMatch(t)) return new ReplyIntent("presence");
        if (TimeQuery().IsMatch(t)) return new ReplyIntent("time");
        if (DateQuery().IsMatch(t)) return new ReplyIntent("date");

        // Reminders before anything that starts with a verb like "remind".
        if (RemindPrefix().Match(t) is { Success: true } rp)
        {
            var rest = t[rp.Length..].Trim();
            var when = TimeExpressionParser.Find(rest, now);
            if (when is not null)
            {
                var what = (rest[..when.Index] + " " + rest[(when.Index + when.Length)..]).Trim();
                what = ReminderConnectors().Replace(what, "").Trim(' ', ',', '.');
                what = what.Length == 0 ? rawText.Trim() : Original(rawText, what);
                return new ToolIntent("reminder_create", ToolArgs.From(new { text = what, due = when.When.ToString("O") }));
            }
            return null; // "remind me later" without a time: let the AI ask a follow-up.
        }

        // Tasks.
        if ((m = AddTask().Match(t)).Success)
            return new ToolIntent("task_create", ToolArgs.From(new { title = Original(rawText, m.Groups["x"].Value) }));
        if (ListTasks().IsMatch(t)) return new ToolIntent("task_list", new ToolArgs());
        if ((m = CompleteTask().Match(t)).Success)
            return new ToolIntent("task_complete", ToolArgs.From(new { title = m.Groups["x"].Value.Trim() }));

        // Memory.
        if ((m = Remember().Match(t)).Success)
            return new ToolIntent("memory_remember", ToolArgs.From(new { content = Original(rawText, m.Groups["x"].Value) }));
        if ((m = Recall().Match(t)).Success)
            return new ToolIntent("memory_search", ToolArgs.From(new { query = m.Groups["x"].Value.Trim() }));
        if ((m = Forget().Match(t)).Success)
            return new ToolIntent("memory_forget", ToolArgs.From(new { query = m.Groups["x"].Value.Trim() }));

        // Audio & media (before open/close: "اقفل الصوت" is mute, not "close an app called الصوت").
        if (Mute().IsMatch(t)) return Volume("mute");
        if (Unmute().IsMatch(t)) return Volume("unmute");
        if ((m = SetVolume().Match(t)).Success) return Volume("set", int.Parse(m.Groups["n"].Value));
        if (VolumeUp().IsMatch(t)) return Volume("up");
        if (VolumeDown().IsMatch(t)) return Volume("down");
        if (MediaNext().IsMatch(t)) return new ToolIntent("media_control", ToolArgs.From(new { action = "next" }));
        if (MediaPrevious().IsMatch(t)) return new ToolIntent("media_control", ToolArgs.From(new { action = "previous" }));
        if (MediaPlayPause().IsMatch(t)) return new ToolIntent("media_control", ToolArgs.From(new { action = "play_pause" }));

        // System.
        if (Screenshot().IsMatch(t)) return new ToolIntent("screenshot", new ToolArgs());
        if (LockScreen().IsMatch(t)) return new ToolIntent("lock_screen", new ToolArgs());
        if (Shutdown().IsMatch(t)) return new ToolIntent("system_power", ToolArgs.From(new { action = "shutdown" }));
        if (Restart().IsMatch(t)) return new ToolIntent("system_power", ToolArgs.From(new { action = "restart" }));
        if (SleepPc().IsMatch(t)) return new ToolIntent("system_power", ToolArgs.From(new { action = "sleep" }));
        if (SystemStatus().IsMatch(t)) return new ToolIntent("system_info", new ToolArgs());

        // Projects ("open my project jarvis", "build jarvis", "why is the build failing in jarvis").
        if ((m = WhyBuildFails().Match(t)).Success && (!m.Groups["x"].Success || IsSimpleTarget(m.Groups["x"].Value)))
            return new ToolIntent("project_build", m.Groups["x"].Success ? ToolArgs.From(new { name = Original(rawText, m.Groups["x"].Value) }) : new ToolArgs(), PreferAi: true);
        if ((m = OpenProject().Match(t)).Success && IsSimpleTarget(m.Groups["x"].Value))
            return new ToolIntent("project_open", ToolArgs.From(new { name = Original(rawText, m.Groups["x"].Value) }), FallBackToAiOnFailure: true);
        if ((m = BuildProject().Match(t)).Success && IsSimpleTarget(m.Groups["x"].Value))
            return new ToolIntent("project_build", ToolArgs.From(new { name = Original(rawText, m.Groups["x"].Value), action = m.Groups["test"].Success ? "test" : "build" }));
        if (ListProjects().IsMatch(t)) return new ToolIntent("project_find", new ToolArgs());

        // Shell commands ("run git status", "شغل الأمر npm test").
        if ((m = RunCommand().Match(t)).Success)
            return new ToolIntent("run_command", ToolArgs.From(new { command = Original(rawText, m.Groups["x"].Value) }));

        // Files and web search.
        if ((m = FindFile().Match(t)).Success)
            return new ToolIntent("file_search", ToolArgs.From(new { query = m.Groups["x"].Value.Trim() }));
        if ((m = WebSearch().Match(t)).Success)
            return new ToolIntent("web_search", ToolArgs.From(new { query = m.Groups["x"].Value.Trim() }), FallBackToAiOnFailure: true);

        // Apps.
        if ((m = OpenApp().Match(t)).Success && IsSimpleTarget(m.Groups["x"].Value))
            return new ToolIntent("app_open", ToolArgs.From(new { name = Original(rawText, m.Groups["x"].Value) }), FallBackToAiOnFailure: true);
        if ((m = CloseApp().Match(t)).Success && IsSimpleTarget(m.Groups["x"].Value))
            return new ToolIntent("app_close", ToolArgs.From(new { name = Original(rawText, m.Groups["x"].Value) }), FallBackToAiOnFailure: true);

        return null;
    }

    private static ToolIntent Volume(string action, int? level = null) =>
        new("volume", level is null ? ToolArgs.From(new { action }) : ToolArgs.From(new { action, level }));

    /// <summary>A target is "simple" when it is a short name rather than a sentence or a compound task.</summary>
    internal static bool IsSimpleTarget(string target)
    {
        var x = target.Trim();
        if (x.Length == 0 || x.Length > 40) return false;
        if (x.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 4) return false;
        return !CompoundMarkers().IsMatch(" " + x + " ");
    }

    /// <summary>
    /// Recover the original casing/spelling of a fragment that was matched in normalized text
    /// (so "remember that Ahmed's birthday is in May" keeps "Ahmed"). Falls back to the fragment.
    /// </summary>
    internal static string Original(string raw, string normalizedFragment)
    {
        var frag = normalizedFragment.Trim();
        if (frag.Length == 0) return frag;
        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var fragWords = frag.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        for (var start = 0; start + fragWords <= words.Length; start++)
        {
            var candidate = string.Join(' ', words.Skip(start).Take(fragWords)).Trim('.', '!', '?', ',', '،', '؟');
            if (TextNormalizer.Normalize(candidate).Trim('.', '!', '?', ',') == frag) return candidate;
        }
        return frag;
    }

    // ---- patterns (written against normalized text: lowercase, Arabic folded: ة→ه ى→ي أ/إ/آ→ا) ----

    [GeneratedRegex(@"^(?:(?:hey|ok|okay|hi)\s+)?(?:jarvis|جارفيس|يا جارفيس)[\s,]+|^(?:please|kindly|can you|could you|would you|will you|لو سمحت|من فضلك|ممكن|يا ريت|بالله)[\s,]+|^يا\s+(?=جارفيس)")]
    private static partial Regex LeadingFiller();

    [GeneratedRegex(@"[\s,]+(?:please|for me|jarvis|لو سمحت|من فضلك|يا جارفيس|جارفيس)$")]
    private static partial Regex TrailingFiller();

    [GeneratedRegex(@"\s(?:and|then|after that|also|ثم|بعدين|وبعدين|وكمان)\s|\sو(?:قول|قلي|قولي|شوف|اعمل|ابعت|دور|ابدا|افتح|اقفل|شغل)|\b(?:my|project|repo|repository|why|how)\b|مشروع|ملف|فولدر|ليه|ازاي")]
    private static partial Regex CompoundMarkers();

    [GeneratedRegex(@"^(?:yes|yeah|yep|yup|sure|ok|okay|approve|approved|confirm|confirmed|do it|go ahead|proceed|ايوه|ايوا|اه|ايوه اعمل|تمام|ماشي|اوك|اوكي|اعمل|اعملها|نفذ|موافق|اكيد|يلا)$")]
    private static partial Regex YesRegex();

    [GeneratedRegex(@"^(?:no|nope|nah|cancel|deny|denied|don'?t|do not|stop|abort|لا|لاء|لأ|بلاش|الغي|الغيها|متعملش|ارفض|لا متعملش|خلاص لا)$")]
    private static partial Regex NoRegex();

    [GeneratedRegex(@"^(?:(?:please\s+)?(?:speak|talk|reply|answer|respond)(?:\s+to me)?\s+(?:in\s+)?english|english(?: please)?|(?:كلمني|اتكلم|رد|ردي)\s+(?:ب?ال)?(?:انجليزي|انجلش|انجليزيه))$")]
    private static partial Regex SpeakEnglish();

    [GeneratedRegex(@"^(?:(?:please\s+)?(?:speak|talk|reply|answer|respond)(?:\s+to me)?\s+(?:in\s+)?arabic|arabic(?: please)?|(?:كلمني|اتكلم|رد|ردي)\s+(?:ب?ال)?(?:عربي|مصري))$")]
    private static partial Regex SpeakArabic();

    [GeneratedRegex(@"^(?:hi|hello|hey|hey there|good (?:morning|afternoon|evening)|morning|ازيك|ازيك يا جارفيس|اهلا|اهلا يا جارفيس|السلام عليكم|صباح الخير|مساء الخير|هاي|صباحو)$")]
    private static partial Regex Greeting();

    [GeneratedRegex(@"^(?:help|what can you do|what are you able to do|what do you do|(?:انت )?(?:بتعرف|تقدر|بتقدر) تعمل ايه|مساعده|بتعمل ايه)$")]
    private static partial Regex Help();

    [GeneratedRegex(@"^(?:are you there|you there|are you awake|انت معايا|انت هنا|صاحي)$")]
    private static partial Regex AreYouThere();

    [GeneratedRegex(@"^(?:what(?:'s| is) the time|what time is it|time|tell me the time|current time|الساعه (?:كام|كم)(?: دلوقتي)?|كام الساعه|الساعه كام دلوقتي)$")]
    private static partial Regex TimeQuery();

    [GeneratedRegex(@"^(?:what(?:'s| is) (?:the |today'?s )?date(?: today)?|what day is (?:it|today)|today'?s date|date|(?:النهارده|انهارده|النهاردة) (?:كام|ايه|يوم ايه|كام في الشهر)|التاريخ(?: النهارده)?|احنا (?:في )?(?:يوم )?(?:كام|ايه))$")]
    private static partial Regex DateQuery();

    [GeneratedRegex(@"^(?:remind me|set (?:a )?reminder|فكرني|ذكرني|فكرني ب)\b")]
    private static partial Regex RemindPrefix();

    [GeneratedRegex(@"^(?:to|that|about|of|for|ب|اني|ان|عشان)\s+|^ب(?=ال)|\s+(?:to|that)$")]
    private static partial Regex ReminderConnectors();

    [GeneratedRegex(@"^(?:add (?:a )?task(?: to)?|add to (?:my )?(?:tasks|todo|to-do)(?: list)?|new task|create (?:a )?task(?: to)?|(?:ضيف|اضف|سجل|اعمل) (?:مهمه|تاسك)(?: جديده)?)[\s:]+(?<x>.+)$")]
    private static partial Regex AddTask();

    [GeneratedRegex(@"^(?:(?:what are|show|list|show me|read) (?:my|the) (?:tasks|todos?|to-dos?)(?: list)?|my tasks|tasks|(?:ايه )?مهامي(?: ايه)?|ايه المهام|عندي (?:مهام|ايه) (?:ايه|النهارده)|المهام)$")]
    private static partial Regex ListTasks();

    [GeneratedRegex(@"^(?:mark (?:the )?(?:task )?(?<x>.+?) (?:as )?(?:done|complete|completed|finished)|complete (?:the )?task (?<x>.+)|(?:خلص|خلصت|علم علي) (?:مهمه|تاسك) (?<x>.+))$")]
    private static partial Regex CompleteTask();

    [GeneratedRegex(@"^(?:remember|note|keep in mind|don'?t forget)(?: that)?\s+(?<x>.+)$|^(?:افتكر|خلي في بالك|خليك فاكر|احفظ)(?: ان| اني)?\s+(?<x>.+)$")]
    private static partial Regex Remember();

    [GeneratedRegex(@"^(?:what do you (?:know|remember) about|what did i tell you about|do you remember)\s+(?<x>.+)$|^(?:تعرف|فاكر|تفتكر) (?:ايه|اي حاجه) عن\s+(?<x>.+)$")]
    private static partial Regex Recall();

    [GeneratedRegex(@"^(?:forget(?: about| that)?)\s+(?<x>.+)$|^(?:انسي|امسح من ذاكرتك)\s+(?<x>.+)$")]
    private static partial Regex Forget();

    [GeneratedRegex(@"^(?:mute(?: the)?(?: sound| volume| audio)?|(?:اكتم|اسكت|اقفل|اقفللي|كتم) (?:ال)?صوت)$")]
    private static partial Regex Mute();

    [GeneratedRegex(@"^(?:unmute(?: the)?(?: sound| volume| audio)?|(?:افتح|رجع|شغل) (?:ال)?صوت)$")]
    private static partial Regex Unmute();

    [GeneratedRegex(@"^(?:set (?:the )?volume to|volume(?: to)?|(?:خلي )?(?:ال)?صوت(?: علي)?)\s+(?<n>\d{1,3})(?:\s?%| percent| في الميه)?$")]
    private static partial Regex SetVolume();

    [GeneratedRegex(@"^(?:(?:turn )?(?:the )?volume up|turn (?:it|the volume|the sound) up|louder|increase (?:the )?volume|(?:علي|ارفع|زود|علا) (?:ال)?صوت(?: شويه)?)$")]
    private static partial Regex VolumeUp();

    [GeneratedRegex(@"^(?:(?:turn )?(?:the )?volume down|turn (?:it|the volume|the sound) down|quieter|lower (?:the )?volume|decrease (?:the )?volume|(?:وطي|قلل|نزل|واطي|اخفض) (?:ال)?صوت(?: شويه)?)$")]
    private static partial Regex VolumeDown();

    [GeneratedRegex(@"^(?:next(?: song| track)?|skip(?: (?:this )?(?:song|track))?|(?:ال)?اغنيه اللي بعدها|اللي بعده|اللي بعدها|التالي|الجاي)$")]
    private static partial Regex MediaNext();

    [GeneratedRegex(@"^(?:previous(?: song| track)?|go back a (?:song|track)|(?:ال)?اغنيه اللي قبلها|اللي قبله|اللي قبلها|السابق)$")]
    private static partial Regex MediaPrevious();

    [GeneratedRegex(@"^(?:pause|play|resume|play pause|(?:pause|play|resume|stop) (?:the )?(?:music|song|video|media|track)|(?:وقف|كمل|شغل) (?:ال)?(?:اغنيه|مزيكا|موسيقي|فيديو|المزيكا))$")]
    private static partial Regex MediaPlayPause();

    [GeneratedRegex(@"^(?:(?:take )?(?:a )?screen ?shot(?: of (?:the|my) screen)?|capture (?:the |my )?screen|(?:خد|اعمل|هات) (?:ل?ي )?(?:سكرين ?شوت|سكرينه|صوره للشاشه|لقطه شاشه)|سكرين ?شوت|صور (?:ال)?شاشه)$")]
    private static partial Regex Screenshot();

    [GeneratedRegex(@"^(?:lock(?: the| my)?(?: screen| computer| pc| laptop| workstation)?|(?:اقفل|قفل) (?:ال)?شاشه)$")]
    private static partial Regex LockScreen();

    [GeneratedRegex(@"^(?:shut ?down|turn off|power off)(?: the| my)? (?:computer|pc|laptop|machine)$|^shut ?down$|^(?:اطفي|اقفل|قفل) (?:ال)?(?:جهاز|كمبيوتر|لابتوب|بي سي)$")]
    private static partial Regex Shutdown();

    [GeneratedRegex(@"^(?:restart|reboot)(?: the| my)?(?: computer| pc| laptop| machine)?$|^(?:اعمل )?(?:ريستارت|رستارت)(?: لل?(?:جهاز|كمبيوتر|لابتوب))?$")]
    private static partial Regex Restart();

    [GeneratedRegex(@"^(?:put (?:the |my )?(?:computer|pc|laptop) to sleep|sleep (?:the )?(?:computer|pc|laptop))$|^(?:نيم|خلي) (?:ال)?(?:جهاز|كمبيوتر|لابتوب)(?: ينام)?$")]
    private static partial Regex SleepPc();

    [GeneratedRegex(@"^(?:(?:system|computer|pc|laptop) (?:status|info|information|health)|how(?:'s| is) (?:the|my) (?:system|computer|pc|laptop)(?: doing)?|battery(?: level| status)?|how much battery(?: is left)?|(?:حاله|حالة) (?:ال)?جهاز|(?:ال)?جهاز عامل (?:ايه|اي)|(?:ال)?بطاريه(?: كام| فيها كام)?)$")]
    private static partial Regex SystemStatus();

    [GeneratedRegex(@"^(?:why (?:is|does) (?:the |my )?build (?:failing|fail|broken)|why (?:is|does) (?<x>.+?) (?:fail(?:ing)? to build|not build)|what(?:'s| is) wrong with the build)(?: (?:in|for|of) (?:the |my )?(?:project )?(?<x>[\w .\-]+?))?$|^(?:ليه|ليش) (?:ال)?(?:بيلد|build) (?:بيفشل|فاشل|واقع)(?: في| ف)?(?: مشروع)? (?<x>.+)$")]
    private static partial Regex WhyBuildFails();

    [GeneratedRegex(@"^(?:open|launch)(?: up)?(?: the| my)? project (?<x>.+)$|^open my (?<x>[\w.\-]+) project$|^(?:افتح|افتحلي|افتح لي) (?:ال)?مشروع (?<x>.+)$")]
    private static partial Regex OpenProject();

    [GeneratedRegex(@"^(?:build|compile)(?: the| my)?(?: project)? (?<x>[\w.\-]+(?: [\w.\-]+)?)(?: project)?$|^(?<test>run(?: the)? tests|test)(?: (?:in|for|of))?(?: the| my)?(?: project)? (?<x>[\w.\-]+)$|^(?:اعمل|شغل) (?:بيلد|build) (?:ل|لل)?(?:مشروع )?(?<x>.+)$")]
    private static partial Regex BuildProject();

    [GeneratedRegex(@"^(?:list|show)(?: me)? my projects$|^(?:what|which) projects do i have$|^مشاريعي(?: ايه)?$|^(?:ايه|اي) المشاريع(?: اللي عندي)?$")]
    private static partial Regex ListProjects();

    [GeneratedRegex(@"^(?:(?:run|execute)(?: the)? command|(?:شغل|نفذ)(?: ال)?(?:امر|كوماند))\s+(?<x>.+)$|^(?:run|execute)\s+(?<x>(?:git|npm|npx|pnpm|yarn|dotnet|python|py|pip|node|ipconfig|ping|winget|choco|docker|kubectl|cargo|go|java|mvn|gradle)\b.*)$")]
    private static partial Regex RunCommand();

    [GeneratedRegex(@"^(?:find|search for|locate|where is|look for)(?: the| a| my)? files? (?:named |called )?(?<x>.+)$|^(?:دور|ابحث|دورلي|شوفلي)(?: لي)? (?:علي|عن) (?:ال)?ملف(?: اسمه)? (?<x>.+)$")]
    private static partial Regex FindFile();

    [GeneratedRegex(@"^(?:search|google|look up|search the web|search online|search google)(?: for)? (?<x>.+)$|^(?:دور|ابحث|دورلي|سيرش|جوجل)(?: لي)?(?: في جوجل| علي النت)? (?:علي|عن) (?<x>.+)$")]
    private static partial Regex WebSearch();

    [GeneratedRegex(@"^(?:open|launch|start|run|bring up|fire up)\s+(?:the |up )?(?<x>.+?)(?: app| application| program)?$|^(?:افتح|افتحلي|افتحي|افتح لي|شغل|شغلي|شغللي|شغل لي)\s+(?:(?:ال)?برنامج\s+)?(?<x>.+)$")]
    private static partial Regex OpenApp();

    [GeneratedRegex(@"^(?:close|quit|exit|kill|shut)\s+(?:the )?(?<x>.+?)(?: app| application| program)?$|^(?:اقفل|اقفلي|اقفللي|قفل|سكر|اقفل لي)\s+(?:برنامج\s+)?(?<x>.+)$")]
    private static partial Regex CloseApp();
}
