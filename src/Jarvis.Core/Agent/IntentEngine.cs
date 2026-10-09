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
        {
            var (title, priority) = SplitPriority(Original(rawText, m.Groups["x"].Value));
            return new ToolIntent("task_create", priority is null ? ToolArgs.From(new { title }) : ToolArgs.From(new { title, priority }));
        }
        if (ListTasks().IsMatch(t)) return new ToolIntent("task_list", new ToolArgs());
        if (Priorities().IsMatch(t)) return new ToolIntent("daily_briefing", ToolArgs.From(new { focus = "priorities" }));
        if (Briefing().IsMatch(t)) return new ToolIntent("daily_briefing", ToolArgs.From(new { focus = "today" }));
        if (InboxCheck().IsMatch(t)) return new ToolIntent("inbox_check", new ToolArgs());
        if (NextMeeting().IsMatch(t)) return new ToolIntent("calendar_next", new ToolArgs());
        if (RecordMeeting().IsMatch(t)) return new ToolIntent("meeting_record_start", new ToolArgs());
        if (ScreenLook().IsMatch(t)) return new ToolIntent("screen_describe", new ToolArgs());
        if (ListPlugins().IsMatch(t)) return new ToolIntent("plugin_list", new ToolArgs());
        if ((m = MakePlugin().Match(t)).Success)
            return new ToolIntent("plugin_create", ToolArgs.From(new { description = Original(rawText, m.Groups["x"].Value) }));
        if ((m = WatchTopic().Match(t)).Success)
            return new ToolIntent("research_watch", ToolArgs.From(new { topic = Original(rawText, m.Groups["x"].Value).TrimEnd('.', '?', '!') }));
        if ((m = UnwatchTopic().Match(t)).Success)
            return new ToolIntent("research_unwatch", ToolArgs.From(new { topic = Original(rawText, m.Groups["x"].Value).TrimEnd('.', '?', '!') }));
        if ((m = LearnFrom().Match(t)).Success)
            return new ToolIntent("learn_from_source", ToolArgs.From(new { source = Original(rawText, m.Groups["x"].Value).Trim('"', '\'') }));
        if ((m = Research().Match(t)).Success)
            return new ToolIntent("research_topic", ToolArgs.From(new { topic = Original(rawText, m.Groups["x"].Value).TrimEnd('.', '?', '!') }));
        if (StopRecording().IsMatch(t)) return new ToolIntent("meeting_record_stop", new ToolArgs());
        if (MeetingNotesQ().IsMatch(t)) return new ToolIntent("meeting_notes", new ToolArgs(), PreferAi: true);
        if ((m = Agenda().Match(t)).Success)
            return new ToolIntent("calendar_agenda", m.Groups["w"].Success && m.Groups["w"].Value.Length > 0 ? ToolArgs.From(new { when = m.Groups["w"].Value.Trim() }) : new ToolArgs());
        if ((m = MeetingPrep().Match(t)).Success)
            return new ToolIntent("meeting_prep", m.Groups["x"].Success && m.Groups["x"].Value.Trim().Length > 0 ? ToolArgs.From(new { meeting = Original(rawText, m.Groups["x"].Value) }) : new ToolArgs(), PreferAi: true);
        if ((m = AddEvent().Match(t)).Success)
        {
            var what = Original(rawText, m.Groups["x"].Value).Trim();
            var kind = m.Groups["k"].Value;
            var title = Regex.IsMatch(what, @"^(?:with|مع)\b", RegexOptions.IgnoreCase) ? $"{(kind is "call" ? "Call" : kind is "اجتماع" or "ميتنج" ? "اجتماع" : "Meeting")} {what}" : what;
            return new ToolIntent("calendar_add", ToolArgs.From(new { title }), FallBackToAiOnFailure: true);
        }
        if ((m = CompleteTask().Match(t)).Success)
            return new ToolIntent("task_complete", ToolArgs.From(new { title = m.Groups["x"].Value.Trim() }));

        // Workflows ("track the CityCrep deal", "how's the CityCrep deal going", "proposal is done for CityCrep").
        if ((m = TrackDeal().Match(t)).Success)
        {
            var who = Original(rawText, m.Groups["x"].Value);
            return new ToolIntent("workflow_create", ToolArgs.From(new { title = $"{who} deal", template = "deal", about = who }));
        }
        if ((m = TrackProject().Match(t)).Success)
        {
            var what = Original(rawText, m.Groups["x"].Value);
            return new ToolIntent("workflow_create", ToolArgs.From(new { title = $"{what} project", template = "project", about = what }));
        }
        if (ListWorkflows().IsMatch(t)) return new ToolIntent("workflow_status", new ToolArgs());
        if ((m = WorkflowStatusQ().Match(t)).Success)
            return new ToolIntent("workflow_status", ToolArgs.From(new { name = Original(rawText, m.Groups["x"].Value) }), FallBackToAiOnFailure: true);
        if ((m = StepDone().Match(t)).Success)
            return new ToolIntent("workflow_update_step", ToolArgs.From(new
            {
                workflow = Original(rawText, m.Groups["w"].Value), step = Original(rawText, m.Groups["s"].Value), status = "done",
            }), FallBackToAiOnFailure: true);

        // Memory.
        if ((m = WorksAt().Match(t)).Success && !IsSelf(m.Groups["a"].Value))
            return new ToolIntent("memory_relate", ToolArgs.From(new
            {
                from = Original(rawText, m.Groups["a"].Value), from_type = "person", relation = "works_at",
                to = Original(rawText, m.Groups["b"].Value), to_type = "organization",
            }));
        if ((m = WhoIs().Match(t)).Success)
            return new ToolIntent("memory_search", ToolArgs.From(new { query = Original(rawText, m.Groups["x"].Value), require = true }), FallBackToAiOnFailure: true);
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
        if ((m = LatestFile().Match(t)).Success)
        {
            var folder = m.Groups["f"].Success ? m.Groups["f"].Value.Trim() : null;
            var kind = m.Groups["k"].Success ? KindWord(m.Groups["k"].Value) : null;
            return new ToolIntent("file_latest", folder is null && kind is null ? new ToolArgs()
                : folder is null ? ToolArgs.From(new { kind }) : kind is null ? ToolArgs.From(new { folder }) : ToolArgs.From(new { folder, kind }));
        }
        if ((m = FindDocsAbout().Match(t)).Success)
            return new ToolIntent("file_find", ToolArgs.From(new { query = Original(rawText, m.Groups["x"].Value) }));
        if ((m = FindFile().Match(t)).Success)
            return new ToolIntent("file_search", ToolArgs.From(new { query = m.Groups["x"].Value.Trim() }));
        if ((m = Summarize().Match(t)).Success)
            return new ToolIntent("file_summarize", ToolArgs.From(new { file = Original(rawText, m.Groups["x"].Value) }), PreferAi: true);
        if ((m = CompareFiles().Match(t)).Success)
            return new ToolIntent("file_compare", m.Groups["y"].Success && !PreviousVersionWords().IsMatch(m.Groups["y"].Value)
                ? ToolArgs.From(new { file = Original(rawText, m.Groups["x"].Value), other = Original(rawText, m.Groups["y"].Value) })
                : ToolArgs.From(new { file = Original(rawText, m.Groups["x"].Value) }), FallBackToAiOnFailure: true);
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

    /// <summary>"…with high priority", "…, urgent", "… (أولوية عالية)" → the priority, and the title without it.</summary>
    internal static (string Title, string? Priority) SplitPriority(string title)
    {
        var m = TaskPriority().Match(title);
        if (!m.Success || m.Index == 0) return (title, null);
        var word = m.Groups["p"].Value.ToLowerInvariant();
        var priority = word switch
        {
            "urgent" or "critical" or "asap" or "مستعجل" or "مستعجله" or "مستعجلة" or "ضروري" => "urgent",
            "high" or "important" or "عالية" or "عاليه" or "مهم" or "مهمة" or "مهمه" => "high",
            "low" or "منخفضة" or "منخفضه" or "قليلة" => "low",
            _ => "normal",
        };
        return (title[..m.Index].TrimEnd(' ', ',', '-', '(', '،'), priority);
    }

    [GeneratedRegex(@"[\s,،\-(]+(?:with |at |as )?(?:a )?(?:(?<p>high|urgent|low|normal|critical|important) priority|priority[: ]+(?<p>high|urgent|low|normal)|(?<p>urgent|asap|important)|بأولوية (?<p>عالية|عاليه|منخفضة|منخفضه)|أولوية (?<p>عالية|عاليه|منخفضة|منخفضه)|اولويه (?<p>عاليه|منخفضه)|(?<p>مستعجل|مستعجله|مستعجلة|ضروري))\)?[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex TaskPriority();

    [GeneratedRegex(@"^(?:add (?:a )?task(?: to)?|add to (?:my )?(?:tasks|todo|to-do)(?: list)?|new task|create (?:a )?task(?: to)?|(?:ضيف|اضف|سجل|اعمل) (?:مهمه|تاسك)(?: جديده)?)[\s:]+(?<x>.+)$")]
    private static partial Regex AddTask();

    [GeneratedRegex(@"^(?:(?:what are|show|list|show me|read) (?:my|the) (?:tasks|todos?|to-dos?)(?: list)?|my tasks|tasks|(?:ايه )?مهامي(?: ايه)?|ايه المهام|عندي (?:مهام|ايه) (?:ايه|النهارده)|المهام)$")]
    private static partial Regex ListTasks();

    [GeneratedRegex(@"^(?:(?:check|show|what are|whats|what's) (?:my|the) (?:priorities|top priorities)|my priorities|priorities|(?:ايه )?اولوياتي(?: ايه)?|ايه الاولويات)$")]
    private static partial Regex Priorities();

    [GeneratedRegex(@"^(?:(?:what's|whats|what is) (?:happening|going on|on|up)(?: for me)? today|what(?:'s| is) my day(?: look(?:ing)? like)?|how(?:'s| is) my day(?: looking)?|brief me|(?:give me )?(?:my |a |the )?(?:daily |morning )?briefing|my day|today|(?:ايه|إيه) (?:اللي ورايا|ورايا|اخبار يومي|اللي عندي) (?:النهارده|انهارده)|يومي عامل ايه|لخصلي يومي|(?:اديني |عايز )?(?:ال)?ملخص (?:ال)?يوم)$")]
    private static partial Regex Briefing();

    [GeneratedRegex(@"^(?:check|show|open|read)(?: me)? (?:my )?(?:e-?mails?|inbox|mail)(?: for me)?$|^(?:any|do i have any|did i get any) (?:new |urgent |important )?(?:e-?mails?|mail)(?: today)?\??$|^what(?:'s| is) (?:important|urgent|new) in my (?:inbox|e-?mails?|mail)\??$|^(?:my )?(?:inbox|e-?mails?)\??$|^(?:شوف|شوفلي|افتح|اقرا|اقرالي)(?: لي)? (?:ال)?(?:ايميل|ايميلات|ميل|انبوكس|بريد)(?:ي)?$|^(?:في|فيه|جالي) (?:اي )?(?:ايميلات|ايميل|رسايل) (?:جديده|مهمه|مستعجله)\??$|^ايه (?:الجديد|المهم) في (?:الايميل|الانبوكس|الميل)\??$")]
    private static partial Regex InboxCheck();

    [GeneratedRegex(@"^(?:what(?:'s| is)|when(?:'s| is)) my next (?:meeting|appointment|event|call)\??$|^(?:my )?next (?:meeting|appointment|event)\??$|^(?:الاجتماع|الميتنج|الميعاد) (?:الجاي|اللي جاي)(?: امتي| ايه)?\??$|^امتي (?:الاجتماع|الميتنج|الميعاد) (?:الجاي|اللي جاي)\??$")]
    private static partial Regex NextMeeting();

    [GeneratedRegex(@"^(?:record|start recording|transcribe)(?: (?:this|the|my))? (?:meeting|call)$|^start (?:a )?meeting recording$|^(?:سجل|ابدا تسجيل|ابدأ تسجيل) (?:ال)?(?:اجتماع|ميتنج|مكالمه)(?: ده| دي)?$")]
    private static partial Regex RecordMeeting();

    [GeneratedRegex(@"^(?:what(?:'s| is) on (?:my|the) screen|what am i looking at|(?:look at|describe|read) (?:my|the) screen|what do you see on (?:my|the) screen)\??$|^(?:شوف|اقرا|اوصف)(?: لي)? (?:ال)?شاشه\??$|^ايه اللي (?:على|علي) (?:ال)?شاشه\??$")]
    private static partial Regex ScreenLook();

    [GeneratedRegex(@"^(?:what|which) plugins (?:do i have|are installed)\??$|^(?:list|show)(?: me)?(?: my)? plugins$|^(?:my )?plugins$|^(?:عندي )?(?:ايه )?(?:ال)?اضافات(?: اللي عندي)?\??$")]
    private static partial Regex ListPlugins();

    [GeneratedRegex(@"^(?:make|create|write|build) (?:me )?(?:a |an )?(?:plugin|extension|add-on|new tool)(?: that| to| which| for)? (?<x>.+)$|^(?:اعمل|اكتب|اعملي)(?: لي)? (?:اضافه|بلجن|اداه)(?: بت| ت| عشان| علشان)? ?(?<x>.+)$")]
    private static partial Regex MakePlugin();

    [GeneratedRegex(@"^(?:research|learn about|read up on|find out everything about)(?: the)? (?<x>.{2,120})$|^(?:اتعلم|اقرا) عن (?<x>.{2,120})$|^اعمل(?: لي)? بحث عن (?<x>.{2,120})$")]
    private static partial Regex Research();

    [GeneratedRegex(@"^(?:learn|study|read and remember)(?: from)?(?: this| the)?(?: page| article| file| document| pdf)? (?<x>https?://\S+|""?[^""]+\.(?:pdf|docx?|pptx?|xlsx?|odt|txt|md|html?)""?)$|^اتعلم من (?<x>https?://\S+|.+\.(?:pdf|docx?|pptx?|xlsx?|odt|txt|md|html?))$")]
    private static partial Regex LearnFrom();

    [GeneratedRegex(@"^(?:keep me (?:updated|posted) (?:on|about)|follow (?:the )?news (?:on|about)|watch (?:for )?news (?:on|about)|keep an eye on news about)(?: the)? (?<x>.{2,120})$|^(?:تابع(?:لي)?|تابعلي) (?:اخبار |أخبار )?(?<x>.{2,120})$|^(?:عرفني|قولي) (?:لو |كل ما )?(?:فيه|في) جديد (?:عن|في) (?<x>.{2,120})$")]
    private static partial Regex WatchTopic();

    [GeneratedRegex(@"^(?:stop following|stop watching|unfollow|stop updating me (?:on|about))(?: the)? (?<x>.{2,120})$|^(?:بطل|وقف) (?:تتابع|متابعة) (?<x>.{2,120})$")]
    private static partial Regex UnwatchTopic();

    [GeneratedRegex(@"^(?:stop|end|finish) (?:the )?(?:meeting )?recording$|^stop recording(?: the meeting)?$|^(?:وقف|اقفل|خلص) (?:ال)?تسجيل$")]
    private static partial Regex StopRecording();

    [GeneratedRegex(@"^(?:(?:show|give) me )?(?:the )?(?:meeting )?notes(?: from| of)?(?: the| my)?(?: last)?(?: meeting)?$|^what did we (?:decide|agree)(?: on)?(?: in the (?:last )?meeting)?\??$|^(?:the )?action items(?: from (?:the|my) (?:last )?meeting)?$|^(?:ايه )?(?:اللي )?(?:اتفقنا|قررنا) (?:عليه)?(?: في الاجتماع)?\??$|^(?:ملخص|نوت) (?:ال)?اجتماع$")]
    private static partial Regex MeetingNotesQ();

    [GeneratedRegex(@"^(?:what(?:'s| is) on )?my (?:calendar|schedule|agenda)(?: for)?(?<w> today| tomorrow| this week| (?:on )?\w+day| on .+)?\??$|^what(?:'s| is) on my (?:calendar|schedule)(?<w> today| tomorrow| this week| (?:on )?\w+day| on .+)?\??$|^(?:do i have|have i got|any) (?:meetings?|events?|appointments?|calls?)(?<w> today| tomorrow| this week| (?:on )?\w+day| on .+)?\??$|^what (?:meetings|events) do i have(?<w> today| tomorrow| this week| (?:on )?\w+day)?\??$|^(?:عندي|فيه|في) (?:اجتماعات|اجتماع|مواعيد|ميعاد|ميتنجات|ميتنج)(?<w> النهارده| بكره| بعد بكره| الاسبوع ده| يوم \S+)?\??$|^(?:ايه )?(?:مواعيدي|جدولي|اجندتي)(?<w> النهارده| بكره| بعد بكره| الاسبوع ده| يوم \S+)?\??$")]
    private static partial Regex Agenda();

    [GeneratedRegex(@"^(?:prepare|prep|brief) me (?:for|on|about) (?:my |the |our )?(?:next )?(?<x>.*?)(?: ?(?:meeting|call|appointment))?$|^(?:جهزني|حضرني) (?:ل|لـ)?(?:ال)?(?:اجتماع|ميتنج)?(?: مع)? ?(?<x>.*)$")]
    private static partial Regex MeetingPrep();

    [GeneratedRegex(@"^(?:schedule|add|book|put|create|set up)(?: me)?(?: a| an)? (?<k>meeting|event|appointment|call)(?: in my calendar| on my calendar| to my calendar)?(?<x> .+)$|^(?:حط|ضيف|سجل|احجز)(?: لي| لى)? (?<k>اجتماع|ميعاد|ميتنج|مكالمه)(?<x> .+)$")]
    private static partial Regex AddEvent();

    [GeneratedRegex(@"^(?:mark (?:the )?(?:task )?(?<x>.+?) (?:as )?(?:done|complete|completed|finished)|complete (?:the )?task (?<x>.+)|(?:خلص|خلصت|علم علي) (?:مهمه|تاسك) (?<x>.+))$")]
    private static partial Regex CompleteTask();

    [GeneratedRegex(@"^(?:remember|note|keep in mind|don'?t forget)(?: that)?\s+(?<x>.+)$|^(?:افتكر|خلي في بالك|خليك فاكر|احفظ)(?: ان| اني)?\s+(?<x>.+)$")]
    private static partial Regex Remember();

    [GeneratedRegex(@"^(?:what do you (?:know|remember) about|what did i tell you about|do you remember)\s+(?<x>.+)$|^(?:تعرف|فاكر|تفتكر) (?:ايه|اي حاجه) عن\s+(?<x>.+)$")]
    private static partial Regex Recall();

    [GeneratedRegex(@"^(?:(?:start )?track(?:ing)?|follow|keep track of) (?:the |my |our )?(?:deal with |deal for )?(?<x>[\p{L}\p{N}][\p{L}\p{N} &'.\-]{0,40}?) (?:deal|opportunity|sale)$|^(?:(?:start )?track(?:ing)?|follow) (?:the |my |our )?(?:deal|opportunity) (?:with|for) (?<x>[\p{L}\p{N}][\p{L}\p{N} &'.\-]{0,40})$|^(?:تابع|تابعلي|خليك متابع) (?:صفقه|صفقة|ديل) (?<x>.{2,40})$")]
    private static partial Regex TrackDeal();

    [GeneratedRegex(@"^(?:(?:start )?track(?:ing)?|follow) (?:the |my |our )?(?:project (?<x>[\p{L}\p{N}][\p{L}\p{N} &'.\-]{0,40})|(?<x>[\p{L}\p{N}][\p{L}\p{N} &'.\-]{0,40}?) project)$|^(?:تابع|تابعلي) (?:مشروع) (?<x>.{2,40})$")]
    private static partial Regex TrackProject();

    [GeneratedRegex(@"^(?:what am i tracking|(?:show|list)(?: me)? (?:my )?(?:workflows|deals|what you'?re tracking)|my workflows|workflows|انت متابع ايه|متابع ايه|ايه اللي انت متابعه)$")]
    private static partial Regex ListWorkflows();

    [GeneratedRegex(@"^(?:what(?:'s| is) the status of|status of|where are we (?:with|on)|update (?:me )?on) (?:the |my |our )?(?<x>.+?)(?: deal| project)?$|^how(?:'s| is) (?:the |my |our )?(?!it\b|everything\b|things\b|life\b|your\b)(?<x>.+?) (?:deal|project)(?: going)?$|^how(?:'s| is) (?:the |my |our )?(?!it\b|everything\b|things\b|life\b|your\b)(?<x>.+?) going$|^(?:وصلنا لفين في|ايه الاخبار في|ايه اخبار) (?<x>.+)$")]
    private static partial Regex WorkflowStatusQ();

    [GeneratedRegex(@"^(?:mark |i'?ve |i |we'?ve |we )?(?:finished |done with |completed? )?(?<s>.+?) (?:is |as )?(?:done|finished|complete|completed) (?:for|in|on) (?:the )?(?<w>.+)$|^(?:خلصت|خلصنا) (?<s>.+?) (?:في|بتاع|بتاعه) (?<w>.+)$")]
    private static partial Regex StepDone();

    // "Ahmed works at CityCrep", "remember that Sara works for Atlas", "أحمد شغال في سيتي كريب".
    [GeneratedRegex(@"^(?:(?:remember|note) (?:that )?)?(?<a>[\p{L}][\p{L}'\-]*(?: [\p{L}][\p{L}'\-]*){0,2}) (?:works|is working) (?:at|for) (?<b>[\p{L}\p{N}][\p{L}\p{N} &'.\-]{1,50})$|^(?:افتكر (?:ان |إن )?)?(?<a>[\p{L}]+(?: [\p{L}]+)?) (?:شغال|بيشتغل|يشتغل|بتشتغل|شغاله) (?:في|ف|عند) (?<b>[\p{L}\p{N}][\p{L}\p{N} &'.\-]{1,50})$")]
    private static partial Regex WorksAt();

    [GeneratedRegex(@"^(?:who(?:'s| is) |مين )(?<x>[\p{L}][\p{L}\p{N} '.\-]{1,40})$")]
    private static partial Regex WhoIs();

    private static bool IsSelf(string who) => who.Trim() is "i" or "me" or "انا" or "احنا" or "we" or "he" or "she" or "هو" or "هي";

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

    // "latest file in downloads", "what's the last pdf I downloaded", "آخر ملف في التنزيلات"
    [GeneratedRegex(@"^(?:what(?:'s| is| are) )?(?:show (?:me )?)?(?:the |my )?(?:latest|newest|most recent|last)(?: \d+)? (?<k>files?|documents?|docs?|pdfs?|spreadsheets?|excel(?: files?)?|presentations?|slides?|images?|photos?|pictures?|screenshots?|downloads?)(?: i (?:downloaded|saved|worked on|got))?(?: (?:in|on|from) (?:the |my )?(?<f>[\p{L}\p{N} :\\/._\-]+))?$|^(?:اخر|احدث) (?<k>ملف|ملفات|بي دي اف|اكسيل|صوره|صور|سكرين شوت)(?: (?:في|ف|على|علي) (?<f>.+))?$")]
    private static partial Regex LatestFile();

    [GeneratedRegex(@"^(?:find|search|look for|show me)(?: for)?(?: the| my| any)? (?:documents?|docs|files|papers|contracts?|proposals?|invoices?|reports?) (?:about|on|regarding|mentioning|for|with) (?<x>.+)$|^(?:which|what|any) (?:files|documents|docs|papers) (?:mention|talk about|are about|contain|have|say anything about) (?<x>.+)$|^(?:find|search for|look for) (?:the |my )?(?!files? |folder )(?<x>.+? (?:contract|proposal|invoice|report|presentation|deck|spreadsheet|budget|offer|agreement|nda|cv|resume))$|^(?:دور|دورلي|ابحث|شوفلي)(?: لي)? (?:علي|عن) (?:ملفات|مستندات|ورق|عقود|عقد|عروض|عرض|فواتير|فاتوره) (?:عن|بتاع|بتاعه|بتاعت|ل|لـ)? ?(?<x>.+)$")]
    private static partial Regex FindDocsAbout();

    [GeneratedRegex(@"^(?:summari[sz]e|give me (?:a |the )?summary of|what(?:'s| is) in) (?:the |my |this )?(?:file |document |pdf |doc )?(?<x>.+\.[a-z0-9]{2,5}|.+? (?:file|document|pdf|doc|contract|proposal|report|presentation|deck|spreadsheet))$|^(?:لخص|لخصلي|لخص لي) (?:ال)?(?:ملف|مستند|عقد|عرض|تقرير)? ?(?<x>.+)$")]
    private static partial Regex Summarize();

    [GeneratedRegex(@"^(?:compare|diff) (?<x>.+?)(?: (?:with|to|and|against) (?<y>.+))?$|^what changed in (?<x>.+?)(?: since (?<y>.+))?$|^(?:قارن|قارنلي) (?<x>.+?)(?: (?:ب|مع|و) ?(?<y>.+))?$")]
    private static partial Regex CompareFiles();

    [GeneratedRegex(@"^(?:the |its |my )?(?:previous|earlier|older|last|old|prior)(?: one| version| draft| copy)?$|^(?:the )?(?:one|version) before$|^(?:ال)?(?:نسخه|نسخة) (?:ال)?(?:قديمه|قديمة|اللي قبلها|السابقه|السابقة)$|^اللي قبله(?:ا)?$")]
    private static partial Regex PreviousVersionWords();

    private static string? KindWord(string w)
    {
        w = w.Trim();
        if (w.StartsWith("pdf") || w == "بي دي اف") return "pdf";
        if (w.StartsWith("spreadsheet") || w.StartsWith("excel") || w == "اكسيل") return "spreadsheet";
        if (w.StartsWith("presentation") || w.StartsWith("slide")) return "presentation";
        if (w.StartsWith("image") || w.StartsWith("photo") || w.StartsWith("picture") || w.StartsWith("screenshot") || w.StartsWith("صور") || w == "سكرين شوت") return "image";
        if (w.StartsWith("doc")) return "document";
        return null;
    }

    [GeneratedRegex(@"^(?:find|search for|locate|where is|look for)(?: the| a| my)? files? (?:named |called )?(?<x>.+)$|^(?:دور|ابحث|دورلي|شوفلي)(?: لي)? (?:علي|عن) (?:ال)?ملف(?: اسمه)? (?<x>.+)$")]
    private static partial Regex FindFile();

    [GeneratedRegex(@"^(?:search|google|look up|search the web|search online|search google)(?: for)? (?<x>.+)$|^(?:دور|ابحث|دورلي|سيرش|جوجل)(?: لي)?(?: في جوجل| علي النت)? (?:علي|عن) (?<x>.+)$")]
    private static partial Regex WebSearch();

    [GeneratedRegex(@"^(?:open|launch|start|run|bring up|fire up)\s+(?:the |up )?(?<x>.+?)(?: app| application| program)?$|^(?:افتح|افتحلي|افتحي|افتح لي|شغل|شغلي|شغللي|شغل لي)\s+(?:(?:ال)?برنامج\s+)?(?<x>.+)$")]
    private static partial Regex OpenApp();

    [GeneratedRegex(@"^(?:close|quit|exit|kill|shut)\s+(?:the )?(?<x>.+?)(?: app| application| program)?$|^(?:اقفل|اقفلي|اقفللي|قفل|سكر|اقفل لي)\s+(?:برنامج\s+)?(?<x>.+)$")]
    private static partial Regex CloseApp();
}
