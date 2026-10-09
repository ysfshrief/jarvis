using Jarvis.Core.Language;
using Jarvis.Core.Tools;

namespace Jarvis.Core.Agent;

/// <summary>
/// Chooses which tool schemas to send to a model. Small local models have small context windows
/// (often 4K tokens) and get confused by long tool lists, so they receive a core set plus the
/// tools whose topic matches the request. Large cloud models receive everything.
/// </summary>
public static class ToolSelector
{
    private static readonly HashSet<string> Core =
    [
        "app_open", "app_close", "run_command", "file_search", "file_read", "memory_remember", "memory_search",
        "task_create", "task_list", "reminder_create", "system_info", "project_find", "project_build", "web_search",
    ];

    private static readonly (string[] Keywords, string[] Tools)[] Topics =
    [
        (["file", "folder", "document", "save", "write", "delete", "move", "copy", "rename", "ملف", "فولدر", "احفظ", "امسح"],
            ["file_list", "file_write", "file_move", "file_delete"]),
        (["file", "document", "doc", "pdf", "excel", "sheet", "slide", "deck", "contract", "proposal", "invoice", "report", "summar", "latest", "version",
          "ملف", "مستند", "عقد", "عرض", "فاتوره", "تقرير", "لخص", "اخر", "نسخه"],
            ["file_find", "file_latest", "file_extract", "file_summarize", "file_compare"]),
        (["web", "site", "page", "url", "link", "internet", "search", "research", "compare", "price", "news", "دور", "سعر", "النت", "موقع"],
            ["web_read", "open_url"]),
        (["browser", "site", "page", "website", "click", "form", "fill", "sign in", "login", "book", "order", "buy", "edge", "chrome",
          "المتصفح", "موقع", "صفحه", "اضغط", "دوس", "املا", "احجز", "اطلب"],
            ["browser_open", "browser_read", "browser_click", "browser_type", "browser_back", "browser_screenshot", "browser_close"]),
        (["track", "deal", "workflow", "follow up", "follow-up", "waiting", "step", "pipeline", "hiring", "تابع", "صفقه", "متابعه", "خطوه", "مستني"],
            ["workflow_create", "workflow_status", "workflow_update_step", "workflow_add_step", "workflow_cancel"]),
        (["who", "works at", "relationship", "manager", "client", "مين", "شغال", "مدير", "عميل"], ["memory_relate"]),
        (["window", "switch", "minimize", "maximize", "focus", "شباك"], ["window_list", "window_control"]),
        (["process", "kill", "memory usage", "cpu", "slow", "frozen", "hang", "تقيل", "واقف"], ["process_list", "process_kill"]),
        (["volume", "sound", "mute", "music", "song", "play", "pause", "صوت", "اغنيه", "مزيكا"], ["volume", "media_control"]),
        (["screenshot", "screen", "شاشه", "سكرين"], ["screenshot", "lock_screen"]),
        (["shutdown", "restart", "reboot", "sleep", "lock", "اطفي", "ريستارت"], ["system_power", "lock_screen"]),
        (["clipboard", "copy", "paste", "انسخ", "الصق"], ["clipboard_read", "clipboard_write"]),
        (["type", "press", "shortcut", "keyboard", "اكتب", "دوس"], ["keyboard_type", "keyboard_shortcut"]),
        (["task", "todo", "done", "complete", "مهمه", "تاسك", "خلصت"], ["task_complete", "task_update"]),
        (["remind", "reminder", "alarm", "فكرني", "تذكير"], ["reminder_list", "reminder_cancel"]),
        (["forget", "remember", "انسي", "افتكر"], ["memory_forget"]),
        (["today", "priorit", "brief", "my day", "focus", "النهارده", "اولويات", "يومي"], ["daily_briefing"]),
        (["project", "build", "code", "repo", "test", "مشروع", "بيلد", "كود"], ["project_open"]),
    ];

    public static IReadOnlyList<ToolDefinition> Select(string request, IReadOnlyList<ToolDefinition> available, bool compact)
    {
        if (!compact || available.Count <= 18) return available;
        var text = TextNormalizer.Normalize(request);
        var wanted = new HashSet<string>(Core);
        foreach (var (keywords, tools) in Topics)
            if (keywords.Any(k => text.Contains(k))) wanted.UnionWith(tools);
        return available.Where(t => wanted.Contains(t.Name)).ToList();
    }
}
