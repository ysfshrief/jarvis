import { settingsStore } from "./settings";

/**
 * Interface language. Arabic switches the whole layout to right-to-left and translates the
 * interface chrome; anything without a translation stays in English rather than being guessed.
 */
const AR: Record<string, string> = {
  // navigation
  Command: "القيادة", Work: "الشغل", Knowledge: "المعرفة", System: "النظام",
  Overview: "الرئيسية", Assistant: "المساعد", Tasks: "المهام", Workflows: "المتابعات", Memory: "الذاكرة", Files: "الملفات", Inbox: "الإنبوكس", Calendar: "الأجندة", Meetings: "الاجتماعات", Plugins: "الإضافات", REC: "تسجيل", Stop: "وقف", "Executive Inbox": "الإنبوكس التنفيذي", Urgent: "مستعجل", "Needs reply": "محتاج رد", Important: "مهم", FYI: "للعلم", Noise: "مش مهم", Drafts: "المسودات", Accounts: "الحسابات", Web: "الويب", Activity: "النشاط", Settings: "الإعدادات",
  Console: "الكونسول",
  // top bar
  "Core link": "متصل بالنواة", Reconnecting: "بيعيد الاتصال", Online: "أونلاين", Offline: "أوفلاين", "AI ready": "الذكاء جاهز",
  "No AI model": "مفيش موديل", Voice: "الصوت", "Voice paused": "الصوت متوقف", "No microphone": "مفيش مايك", "Speech model needed": "محتاج موديل كلام",
  "Mic on · wake word": "المايك شغال · كلمة التنبيه", "Mic on · listening": "المايك شغال · بسمع", "Voice ready": "الصوت جاهز",
  Pause: "إيقاف مؤقت", Resume: "استكمال", queued: "مستني", "In a meeting": "في اجتماع", Fullscreen: "شاشة كاملة",
  // orb
  "Standing by": "في الانتظار", Listening: "بسمع", Thinking: "بفكر", Speaking: "بتكلم", Executing: "بنفذ",
  "Needs your approval": "محتاج موافقتك", "Something failed": "حصلت مشكلة", "Runtime unreachable": "النواة مش متاحة", Paused: "متوقف مؤقتاً",
  "Listening for “Jarvis”": "مستني «جارفيس»",
  // timeline
  Understanding: "فهم", Analyzing: "تحليل", "Selecting tool": "اختيار أداة", Completed: "تم", Failed: "فشل",
  // home
  "Good morning": "صباح الخير", "Good afternoon": "مساء الخير", "Good evening": "مساء الخير", "Working late": "سهران",
  Systems: "الأنظمة", Details: "التفاصيل", Now: "دلوقتي", Priorities: "الأولويات", Upcoming: "الجاي", Recent: "الأخيرة", Notifications: "الإشعارات",
  All: "الكل", Log: "السجل", "No open tasks.": "مفيش مهام مفتوحة.", "Nothing scheduled.": "مفيش حاجة متجدولة.", "Nothing yet.": "لسه مفيش حاجة.",
  "All clear. Ask me anything, or say “Jarvis”.": "كله تمام. اسألني أي حاجة، أو قول «جارفيس».",
  "Ask JARVIS… “what's happening today?”, “افتح VS Code”": "اسأل جارفيس… «إيه اللي ورايا النهارده؟»، «افتح VS Code»",
  "Continue in Assistant →": "كمّل في المساعد ←",
  "Enable conversation (free, local)": "شغّل المحادثة (ببلاش ومحلي)",
  // context
  Context: "السياق", "Next up": "اللي جاي", "No reminders scheduled.": "مفيش تذكيرات.", Core: "النواة", Network: "الشبكة", AI: "الذكاء", Uptime: "مدة التشغيل",
  // console & assistant
  "Command console": "كونسول الأوامر", Dashboard: "لوحة التحكم", Suggestions: "اقتراحات", "Enter to run · Esc to close": "Enter للتنفيذ · Esc للإغلاق",
  "Ask or command — English or عربي": "اسأل أو اأمر — عربي أو English", "Message JARVIS — English or عربي": "اكتب لجارفيس — عربي أو English",
  "New conversation": "محادثة جديدة", "How can I help": "أقدر أساعدك إزاي", "Direct command": "أمر مباشر",
  "AI online": "الذكاء شغال", "Direct commands only (no AI model)": "أوامر مباشرة بس (مفيش موديل)",
  "Approval needed": "محتاج موافقة", Approve: "موافق", Deny: "رفض",
  // pages
  "Every switch here changes how JARVIS behaves right away once saved.": "كل اختيار هنا بيغير تصرف جارفيس أول ما تحفظ.",
  "Save changes": "احفظ التغييرات", Discard: "تجاهل", "Unsaved changes": "تغييرات مش محفوظة", Saved: "اتحفظ",
  General: "عام", Security: "الأمان", Appearance: "الشكل", Shortcuts: "الاختصارات", "Tools & plugins": "الأدوات والإضافات", Privacy: "الخصوصية",
};

export function uiLang(): "en" | "ar" {
  // <html lang> is set from the saved setting (and by the Appearance preview).
  if (typeof document !== "undefined" && document.documentElement.lang) return document.documentElement.lang === "ar" ? "ar" : "en";
  return settingsStore.getSnapshot()?.appearance?.language === "ar" ? "ar" : "en";
}

/** Translate an interface string (English is the key). */
export function tr(en: string): string {
  return uiLang() === "ar" ? AR[en] ?? en : en;
}
