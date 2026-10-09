namespace Jarvis.Runtime;

public enum CapabilityStatus { Working, Partial, Foundation, Planned }

public sealed record Capability(string Area, string Name, CapabilityStatus Status, string Notes, int Phase);

/// <summary>
/// An honest map of what JARVIS can and cannot do in this build, shown in the dashboard.
/// "Working" means real and tested; "Partial" works with stated limits; "Foundation" means the
/// architecture exists but the user-facing feature isn't there yet; "Planned" is not started.
/// </summary>
public static class Capabilities
{
    public static IReadOnlyList<Capability> All(bool windows) =>
    [
        new("Core", "Always-on runtime, local API, event stream", CapabilityStatus.Working, "jarvis-core runs in the background; the UI can close without stopping it.", 1),
        new("Core", "Start with Windows", windows ? CapabilityStatus.Working : CapabilityStatus.Planned, "Per-user Run key, toggled in Settings.", 1),
        new("Core", "Agent loop with tools and approvals", CapabilityStatus.Working, "Deterministic commands, AI planning with tool calls, permission checks, audit log.", 1),
        new("Language", "English and Egyptian Arabic", CapabilityStatus.Working, "Auto-detection, bilingual commands and replies; AI replies in Egyptian Arabic when you speak Arabic.", 1),
        new("AI", "Local AI (Ollama / LM Studio / llama.cpp)", CapabilityStatus.Working, "Native Ollama API: model discovery with real capabilities (tools/vision/embeddings), context sizing, streaming, downloads from Settings. Any OpenAI-compatible server also works. Verified in CI against a real Ollama model.", 1),
        new("AI", "Fallback, streaming, context management", CapabilityStatus.Working, "A failing model hands over to the next one; replies stream as they're written; long conversations are condensed to fit small local models; models without tool support still converse.", 1),
        new("AI", "Optional cloud AI (Anthropic, OpenAI-compatible)", CapabilityStatus.Working, "Off by default. Needs your own API key and 'Allow cloud AI'.", 1),
        new("AI", "Router (no-AI / local / cloud by task)", CapabilityStatus.Partial, "Keyword task classes plus capability-aware model choice (prefers tool-capable ~7-8B models). No cost/latency model yet. Deterministic commands never use AI.", 1),
        new("Voice", "Push-to-talk with local Whisper", windows ? CapabilityStatus.Partial : CapabilityStatus.Foundation, "Requires downloading a speech model in Settings → Voice. Verified end to end on Windows in CI (speech → Whisper → command). Arabic accuracy depends on model size.", 1),
        new("Voice", "Wake word \"Jarvis\"", windows ? CapabilityStatus.Partial : CapabilityStatus.Foundation, "Off by default. Uses speech detection + Whisper keyword spotting; a dedicated low-power wake-word model is planned.", 1),
        new("Voice", "Spoken replies (Windows voices)", windows ? CapabilityStatus.Working : CapabilityStatus.Foundation, "Arabic speech needs an Arabic Windows voice installed.", 1),
        new("Computer", "Open/close apps, windows, processes", windows ? CapabilityStatus.Working : CapabilityStatus.Planned, "Start-menu apps (classic + Store), settings pages, folders, websites. English/Arabic names.", 2),
        new("Computer", "Volume, media keys, lock, power, screenshots", windows ? CapabilityStatus.Working : CapabilityStatus.Planned, "Shutdown/restart always need approval and wait 30 seconds.", 2),
        new("Computer", "Keyboard input and shortcuts, clipboard", windows ? CapabilityStatus.Working : CapabilityStatus.Planned, "Need approval because keystrokes go to the focused app.", 2),
        new("Computer", "Run PowerShell/cmd commands", CapabilityStatus.Working, "Read-only commands run freely; others need approval; destructive ones are critical.", 2),
        new("Computer", "UI Automation (click buttons in other apps)", CapabilityStatus.Planned, "Windows UI Automation layer.", 2),
        new("Developer", "Find, open and build code projects; extract build errors", CapabilityStatus.Working, "npm/pnpm/yarn, dotnet, cargo, go, python, gradle, maven, cmake, make. Builds need approval.", 2),
        new("Files", "Search, read, write, move, delete (Recycle Bin)", CapabilityStatus.Working, "Name search over your folders; protected locations require approval.", 2),
        new("Files", "Document knowledge: PDF, Word, Excel, PowerPoint, OpenDocument, RTF, text/code, ZIP listings, images (OCR)", CapabilityStatus.Working, "Opt-in local index of your chosen folders (Settings → Files), kept current as files change. Image text uses Windows OCR on Windows; scanned PDFs without a text layer are not OCR'd yet.", 6),
        new("Files", "Search documents by words or meaning; latest files; key points; version comparison", CapabilityStatus.Working, "\"find documents about the renewal fee\", \"what's my latest PDF\", \"summarize X\", \"compare X with the previous version\". Meaning-based search needs a local embedding model (bge-m3). Key points are extracted sentences; AI summaries are written from the real text when a model is available.", 6),
        new("Files", "Documents linked to people and organisations", CapabilityStatus.Working, "Files mentioning a known person/organisation appear on their profile.", 6),
        new("Memory", "Local memory: add, search, edit, delete, clear", CapabilityStatus.Working, "SQLite full-text search with Arabic normalization; kinds, sources and provenance (why each item is stored).", 3),
        new("Memory", "Confirmed vs inferred, review and confirm/reject", CapabilityStatus.Working, "Inferences never become facts without you.", 3),
        new("Memory", "People, organisations, projects and relationships", CapabilityStatus.Working, "\"Ahmed works at CityCrep\"; profiles gather memories, relationships and related tasks.", 3),
        new("Memory", "Semantic (meaning-based) search", CapabilityStatus.Working, "Needs a free local embedding model (bge-m3 via Ollama); keyword search otherwise.", 3),
        new("Memory", "Conversation history", CapabilityStatus.Working, "Stored locally with a retention period.", 3),
        new("Tasks", "Tasks and reminders", CapabilityStatus.Working, "States: pending, in progress, waiting, blocked, completed, cancelled.", 5),
        new("Tasks", "Daily briefing and priorities", CapabilityStatus.Working, "\"What's happening today?\" / \"Check my priorities\" summarise real reminders, tasks, approvals and queued work. No AI needed.", 5),
        new("Interface", "HUD dashboard, command console, 8-state orb, system monitor", CapabilityStatus.Working, "Streaming replies with live progress; English and Arabic (RTL) interface; appearance, sounds and shortcuts are configurable.", 1),
        new("Tasks", "Workflows (\"track the CityCrep deal\")", CapabilityStatus.Working, "Steps with dependencies, deadlines, waiting-for + follow-up nudges, approval-gated business steps, recurring cycles, history.", 5),
        new("Tasks", "Recurring tasks", CapabilityStatus.Working, "\"every monday\", \"كل شهر\", working days (Sun–Thu)…", 5),
        new("Notifications", "Priority, dedupe, meeting/fullscreen-aware holding, digest", windows ? CapabilityStatus.Working : CapabilityStatus.Partial, "Windows toasts and spoken alerts.", 5),
        new("Presence", "Active app, idle, fullscreen, meeting detection", windows ? CapabilityStatus.Working : CapabilityStatus.Planned, "Meeting = a call app using the microphone. No camera.", 1),
        new("Web", "Web search and page reading", CapabilityStatus.Partial, "Key-less DuckDuckGo search can be rate-limited; page text extraction; untrusted-content handling.", 4),
        new("Web", "Browser agent: open pages, read them, click, fill forms, search, go back, screenshot", CapabilityStatus.Working, "Drives Microsoft Edge/Chrome (its own visible window and profile) over the DevTools protocol — no extra download. Sending, buying, publishing, deleting, booking and form submission always ask; nothing suggested after reading a page runs without your OK. Verified in CI with a real browser.", 4),
        new("Web", "File downloads and multi-tab research in the browser", CapabilityStatus.Planned, "Comparisons currently combine search, page reading and the browser one page at a time.", 4),
        new("Offline", "Offline detection and queued online actions", CapabilityStatus.Working, "Queued actions wait for your OK when the connection returns.", 1),
        new("Privacy", "Screen-capture switch", CapabilityStatus.Working, "Settings → Privacy can block every screen-capture tool.", 1),
        new("Security", "Encrypted secrets, PIN lock, permission levels, audit", CapabilityStatus.Working, windows ? "Secrets use Windows DPAPI." : "Secrets use an AES key file (dev builds).", 1),
        new("Inbox", "Executive inbox over IMAP/SMTP (Gmail with an app password, Yahoo, iCloud, Zoho, company servers)", CapabilityStatus.Working, "Read-only sync, sorting into urgent / needs reply / important / FYI / noise with the reason shown, links to people and organisations, urgent alerts, drafts. Sending is a critical action that always asks. Verified in CI against a real mail server.", 6),
        new("Inbox", "Outlook.com / Microsoft 365 and Google sign-in (OAuth)", CapabilityStatus.Planned, "Microsoft turned off password sign-in for Outlook mail; needs an app registration and sign-in flow.", 6),
        new("Inbox", "WhatsApp, Instagram, Facebook, LinkedIn, X messages", CapabilityStatus.Planned, "Shown honestly in Settings → Accounts: official business APIs only (WhatsApp/Instagram/Messenger), paid (X), or not possible (LinkedIn).", 6),
        new("Inbox", "AI-written replies in your style", CapabilityStatus.Partial, "With a model, JARVIS reads the email and saves a draft for you to edit; learning your writing style from sent mail is planned.", 6),
        new("Calendar", "Calendar and meeting intelligence", CapabilityStatus.Planned, "", 7),
        new("Vision", "Screen understanding", CapabilityStatus.Foundation, "Screenshots work; analysis needs a vision model.", 7),
        new("Adaptation", "Opt-in pattern learning (routines, language, brevity)", CapabilityStatus.Working, "Off by default; proposals carry their evidence and wait for your confirmation.", 8),
        new("Plugins", "Plugin API and self-extension", CapabilityStatus.Foundation, "Tool registry is the extension point.", 9),
        new("Devices", "Android companion", CapabilityStatus.Foundation, "The local API + event stream is the future device protocol.", 11),
    ];
}
