using Jarvis.Core.Agent;
using Jarvis.Core.Language;
using Jarvis.Core.Scheduling;

namespace Jarvis.Core.Tests;

public class IntentEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 10, 10, 0, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData("open calculator", "app_open", "name", "calculator")]
    [InlineData("Jarvis, open VS Code", "app_open", "name", "VS Code")]
    [InlineData("Jarvis، افتح VS Code", "app_open", "name", "VS Code")]
    [InlineData("افتح الآلة الحاسبة", "app_open", "name", "الآلة الحاسبة")]
    [InlineData("please launch spotify", "app_open", "name", "spotify")]
    [InlineData("close notepad", "app_close", "name", "notepad")]
    [InlineData("اقفل الكروم", "app_close", "name", "الكروم")]
    [InlineData("volume 40", "volume", "action", "set")]
    [InlineData("علي الصوت", "volume", "action", "up")]
    [InlineData("وطي الصوت شوية", "volume", "action", "down")]
    [InlineData("اقفل الصوت", "volume", "action", "mute")]
    [InlineData("mute", "volume", "action", "mute")]
    [InlineData("next song", "media_control", "action", "next")]
    [InlineData("take a screenshot", "screenshot", null, null)]
    [InlineData("خد سكرين شوت", "screenshot", null, null)]
    [InlineData("lock the screen", "lock_screen", null, null)]
    [InlineData("اقفل الشاشة", "lock_screen", null, null)]
    [InlineData("shut down the computer", "system_power", "action", "shutdown")]
    [InlineData("اطفي الجهاز", "system_power", "action", "shutdown")]
    [InlineData("restart", "system_power", "action", "restart")]
    [InlineData("how's the system", "system_info", null, null)]
    [InlineData("الجهاز عامل ايه", "system_info", null, null)]
    [InlineData("add task send the proposal", "task_create", "title", "send the proposal")]
    [InlineData("ضيف مهمة أراجع العقد", "task_create", "title", "أراجع العقد")]
    [InlineData("what are my tasks", "task_list", null, null)]
    [InlineData("مهامي ايه", "task_list", null, null)]
    [InlineData("add task sign the CityCrep contract with high priority", "task_create", "priority", "high")]
    [InlineData("add task sign the CityCrep contract with high priority", "task_create", "title", "sign the CityCrep contract")]
    [InlineData("new task: call the bank, urgent", "task_create", "priority", "urgent")]
    [InlineData("ضيف مهمة أراجع العقد بأولوية عالية", "task_create", "title", "أراجع العقد")]
    [InlineData("ضيف مهمة أراجع العقد مستعجل", "task_create", "priority", "urgent")]
    [InlineData("What's happening today?", "daily_briefing", "focus", "today")]
    [InlineData("brief me", "daily_briefing", "focus", "today")]
    [InlineData("how's my day looking", "daily_briefing", "focus", "today")]
    [InlineData("إيه اللي ورايا النهارده؟", "daily_briefing", "focus", "today")]
    [InlineData("Check my priorities", "daily_briefing", "focus", "priorities")]
    [InlineData("أولوياتي إيه", "daily_briefing", "focus", "priorities")]
    [InlineData("remember that Ahmed's birthday is in May", "memory_remember", "content", "Ahmed's birthday is in May")]
    [InlineData("افتكر إن أحمد بيحب القهوة سادة", "memory_remember", "content", "أحمد بيحب القهوة سادة")]
    [InlineData("what do you know about CityCrep", "memory_search", "query", "citycrep")]
    [InlineData("forget my old address", "memory_forget", "query", "my old address")]
    [InlineData("find file proposal", "file_search", "query", "proposal")]
    [InlineData("دور على ملف العرض", "file_search", "query", "العرض")]
    [InlineData("what's my latest pdf", "file_latest", "kind", "pdf")]
    [InlineData("latest file", "file_latest", null, null)]
    [InlineData("اخر ملفات", "file_latest", null, null)]
    [InlineData("find documents about the renewal fee", "file_find", "query", "the renewal fee")]
    [InlineData("what files mention CityCrep", "file_find", "query", "CityCrep")]
    [InlineData("find the CityCrep proposal", "file_find", "query", "CityCrep proposal")]
    [InlineData("دور على ملفات عن سيتي كريب", "file_find", "query", "سيتي كريب")]
    [InlineData("summarize Proposal v2.docx", "file_summarize", "file", "Proposal v2.docx")]
    [InlineData("لخص العقد بتاع سيتي كريب", "file_summarize", null, null)]
    [InlineData("compare Proposal v2.docx with Proposal v1.docx", "file_compare", "other", "Proposal v1.docx")]
    [InlineData("compare Proposal v2.docx with the previous version", "file_compare", "other", null)]
    [InlineData("what changed in Proposal v2.docx", "file_compare", "file", "Proposal v2.docx")]
    [InlineData("قارن العرض بالنسخة القديمة", "file_compare", "other", null)]
    [InlineData("Jarvis, prepare me for my CityCrep meeting", "meeting_prep", "meeting", "CityCrep")]
    [InlineData("brief me on the board call", "meeting_prep", "meeting", "board")]
    [InlineData("جهزني لاجتماع سيتي كريب", "meeting_prep", "meeting", "سيتي كريب")]
    [InlineData("what's on my calendar today", "calendar_agenda", "when", "today")]
    [InlineData("do I have meetings tomorrow?", "calendar_agenda", "when", "tomorrow")]
    [InlineData("my schedule this week", "calendar_agenda", "when", "this week")]
    [InlineData("عندي اجتماعات بكرة؟", "calendar_agenda", "when", "بكره")]
    [InlineData("ايه مواعيدي النهارده", "calendar_agenda", "when", "النهارده")]
    [InlineData("what's my next meeting", "calendar_next", null, null)]
    [InlineData("الاجتماع الجاي امتى", "calendar_next", null, null)]
    [InlineData("schedule a meeting with Ahmed tomorrow at 3pm", "calendar_add", "title", "Meeting with Ahmed tomorrow at 3pm")]
    [InlineData("add an appointment dentist on Sunday at 5pm", "calendar_add", "title", "dentist on Sunday at 5pm")]
    [InlineData("حط اجتماع مع سارة بكرة الساعة 11", "calendar_add", "title", "اجتماع مع سارة بكرة الساعة 11")]
    [InlineData("make a plugin that converts currencies", "plugin_create", "description", "converts currencies")]
    [InlineData("اعمل إضافة تحول العملات", "plugin_create", null, null)]
    [InlineData("what plugins do I have?", "plugin_list", null, null)]
    [InlineData("research CityCrep's competitors", "research_topic", "topic", "CityCrep's competitors")]
    [InlineData("learn about solar panel prices in Egypt", "research_topic", "topic", "solar panel prices in Egypt")]
    [InlineData("اتعلم عن أسعار الطاقة الشمسية", "research_topic", "topic", "أسعار الطاقة الشمسية")]
    [InlineData("learn from https://example.com/Pricing-2026", "learn_from_source", "source", "https://example.com/Pricing-2026")]
    [InlineData("learn from this file C:\\Docs\\Atlas Brief.pdf", "learn_from_source", "source", "C:\\Docs\\Atlas Brief.pdf")]
    [InlineData("اتعلم من https://example.com/a", "learn_from_source", "source", "https://example.com/a")]
    [InlineData("keep me updated on solar panel prices", "research_watch", "topic", "solar panel prices")]
    [InlineData("follow the news about CityCrep", "research_watch", "topic", "CityCrep")]
    [InlineData("تابعلي أخبار الطاقة الشمسية", "research_watch", "topic", "الطاقة الشمسية")]
    [InlineData("stop following solar panel prices", "research_unwatch", "topic", "solar panel prices")]
    [InlineData("what's on my screen?", "screen_describe", null, null)]
    [InlineData("ايه اللي على الشاشة؟", "screen_describe", null, null)]
    [InlineData("record this meeting", "meeting_record_start", null, null)]
    [InlineData("سجل الاجتماع", "meeting_record_start", null, null)]
    [InlineData("stop recording", "meeting_record_stop", null, null)]
    [InlineData("وقف التسجيل", "meeting_record_stop", null, null)]
    [InlineData("what did we decide?", "meeting_notes", null, null)]
    [InlineData("meeting notes", "meeting_notes", null, null)]
    [InlineData("ايه اللي اتفقنا عليه؟", "meeting_notes", null, null)]
    [InlineData("check my email", "inbox_check", null, null)]
    [InlineData("any urgent emails?", "inbox_check", null, null)]
    [InlineData("what's important in my inbox", "inbox_check", null, null)]
    [InlineData("شوف الإيميل", "inbox_check", null, null)]
    [InlineData("فيه إيميلات مستعجلة؟", "inbox_check", null, null)]
    [InlineData("search for best laptops under 30000 EGP", "web_search", "query", "best laptops under 30000 egp")]
    [InlineData("run git status", "run_command", "command", "git status")]
    [InlineData("run command npm test", "run_command", "command", "npm test")]
    public void Recognizes_commands(string text, string tool, string? arg, string? value)
    {
        var intent = Assert.IsType<ToolIntent>(IntentEngine.Match(text, Now));
        Assert.Equal(tool, intent.Tool);
        if (arg is not null) Assert.Equal(value, intent.Args.GetString(arg));
    }

    [Theory]
    [InlineData("open my project and tell me why the build is failing")]
    [InlineData("افتح المشروع وقولي ليه البيلد بيفشل")]
    [InlineData("why is the sky blue")]
    [InlineData("draft a reply to Ahmed about pricing")]
    [InlineData("remind me later")]
    public void Leaves_complex_requests_to_the_ai(string text)
    {
        Assert.Null(IntentEngine.Match(text, Now));
    }

    [Theory]
    [InlineData("what time is it", "time")]
    [InlineData("الساعة كام؟", "time")]
    [InlineData("what's the date", "date")]
    [InlineData("النهارده كام", "date")]
    [InlineData("hello", "greeting")]
    [InlineData("صباح الخير", "greeting")]
    [InlineData("what can you do", "help")]
    [InlineData("بتعرف تعمل ايه", "help")]
    public void Answers_simple_questions_without_tools(string text, string kind)
    {
        var intent = Assert.IsType<ReplyIntent>(IntentEngine.Match(text, Now));
        Assert.Equal(kind, intent.Kind);
    }

    [Theory]
    [InlineData("speak english", Lang.En)]
    [InlineData("كلمني انجليزي", Lang.En)]
    [InlineData("talk to me in arabic", Lang.Ar)]
    [InlineData("كلمني عربي", Lang.Ar)]
    public void Switches_language(string text, Lang lang)
    {
        Assert.Equal(lang, Assert.IsType<LanguageIntent>(IntentEngine.Match(text, Now)).Lang);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("go ahead", true)]
    [InlineData("أيوه", true)]
    [InlineData("تمام", true)]
    [InlineData("no", false)]
    [InlineData("لأ", false)]
    [InlineData("بلاش", false)]
    public void Approval_answers_only_when_something_is_pending(string text, bool approve)
    {
        Assert.Equal(approve, Assert.IsType<ApprovalAnswerIntent>(IntentEngine.Match(text, Now, approvalPending: true)).Approve);
        Assert.IsNotType<ApprovalAnswerIntent>(IntentEngine.Match(text, Now, approvalPending: false));
    }

    [Theory]
    [InlineData("remind me in 10 minutes to call Ahmed", "call Ahmed", 10)]
    [InlineData("remind me to stretch in 2 hours", "stretch", 120)]
    [InlineData("فكرني بعد ربع ساعة اكلم احمد", "اكلم احمد", 15)]
    [InlineData("فكرني بعد 5 دقايق بالاجتماع", "الاجتماع", 5)]
    [InlineData("فكرني بعد دقيقتين اشرب مية", "اشرب مية", 2)]
    public void Parses_relative_reminders(string text, string what, int minutes)
    {
        var intent = Assert.IsType<ToolIntent>(IntentEngine.Match(text, Now));
        Assert.Equal("reminder_create", intent.Tool);
        Assert.Equal(what, intent.Args.GetString("text"));
        var due = DateTimeOffset.Parse(intent.Args.GetString("due")!);
        Assert.Equal(Now.AddMinutes(minutes), due);
    }

    [Fact]
    public void Parses_clock_reminders()
    {
        var expr = TimeExpressionParser.Find("call mom at 5pm", Now);
        Assert.NotNull(expr);
        Assert.Equal(17, expr.When.ToLocalTime().Hour);
        Assert.True(expr.When > Now);
    }

    [Fact]
    public void Clock_time_without_ampm_picks_the_next_occurrence()
    {
        var local = new DateTimeOffset(2026, 5, 10, 14, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 5, 10, 14, 0, 0)));
        var expr = TimeExpressionParser.Find("الساعه 5", local);
        Assert.NotNull(expr);
        Assert.True(expr.When > local);
        Assert.True(expr.When - local <= TimeSpan.FromHours(12));
    }
}

public class LanguageTests
{
    [Theory]
    [InlineData("open calculator", Lang.En)]
    [InlineData("افتح الحاسبة", Lang.Ar)]
    [InlineData("Jarvis، افتح VS Code", Lang.Ar)]
    [InlineData("12345", Lang.En)]
    public void Detects_language(string text, Lang expected) => Assert.Equal(expected, LanguageDetector.Detect(text));

    [Fact]
    public void Normalizes_arabic_spelling_variants()
    {
        Assert.Equal(TextNormalizer.Normalize("الآلة الحاسبة"), TextNormalizer.Normalize("الاله الحاسبه"));
        Assert.Equal(TextNormalizer.Normalize("إحنا"), TextNormalizer.Normalize("احنا"));
        Assert.Equal("10", TextNormalizer.Normalize("١٠"));
        Assert.Equal("مرحبا", TextNormalizer.Normalize("مَرْحَبًا"));
    }
}
