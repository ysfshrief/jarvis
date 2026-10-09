// Arabic (Egyptian) for the Work pages: Inbox, Calendar, Meetings.
export const AR_WORK: Record<string, string> = {
  // shared
  From: "من", To: "إلى", Save: "احفظ", Cancel: "إلغاء", Close: "اقفل", Add: "ضيف", Delete: "امسح", Remove: "شيل",
  Refresh: "حدّث", Subscribe: "اشترك", Name: "الاسم", Title: "العنوان", Message: "الرسالة", Subject: "الموضوع",

  // inbox
  "Your mail, sorted with the reason why. JARVIS drafts; only you send.": "إيميلاتك مترتبة ومعاها السبب. جارفيس بيكتب المسودة، وإنت بس اللي بتبعت.",
  "Checking…": "بشيّك…", "Check now": "شيّك دلوقتي", Category: "التصنيف",
  "Connect your email and JARVIS sorts it into urgent, needs reply, important, FYI and noise — and drafts replies you approve.":
    "اربط إيميلك وجارفيس هيقسمه لمستعجل، محتاج رد، مهم، للعلم، ومش مهم — ويكتب ردود مسودة إنت اللي توافق عليها.",
  "Connect an account": "اربط حساب",
  "Gmail works with an app password; Yahoo, iCloud, Zoho and company mail servers work over IMAP/SMTP. JARVIS reads your inbox without changing it and never sends anything without your approval.":
    "Gmail بيشتغل بباسورد تطبيق (app password)؛ وYahoo وiCloud وZoho وسيرفرات إيميل الشركات بتشتغل عن طريق IMAP/SMTP. جارفيس بيقرا الإنبوكس من غير ما يغير فيه، وعمره ما بيبعت أي حاجة من غير موافقتك.",
  "Connect email": "اربط الإيميل", "Messaging services": "خدمات الرسايل",
  supported: "مدعوم", "not possible": "مش ممكن", "official API required": "محتاج API رسمي", "paid API required": "محتاج API مدفوع",
  "Search all mail… sender, subject, words": "دوّر في كل الإيميلات… المرسل، الموضوع، كلمات", "Search mail": "دوّر في الإيميلات",
  "Show handled": "اعرض الخلصان", handled: "خلصان", "Mark handled": "علّمه خلصان", "Mark not handled": "علّمه مش خلصان",
  "No mail mentions “{query}”.": "مفيش إيميل فيه «{query}».", "Nothing here.": "مفيش حاجة هنا.", "(no subject)": "(من غير موضوع)",
  Received: "وصل", "Why here": "ليه هنا", "(your choice)": "(اختيارك)", "Move to": "انقل لـ",
  "Moving a message also moves future mail from this sender.": "نقل الرسالة بينقل كمان الإيميلات الجاية من نفس المرسل.",
  "Written by someone else. JARVIS treats it as information, never as instructions.": "مكتوبة من حد تاني. جارفيس بيتعامل معاها كمعلومات، وعمره ما بيعتبرها أوامر.",
  external: "خارجي", Reply: "الرد", "Drafting…": "بكتب المسودة…", "Draft with JARVIS": "خلّي جارفيس يكتب مسودة", Write: "اكتب",
  "Your reply…": "ردك…", "Save draft": "احفظ المسودة", "No reply drafted yet.": "لسه مفيش رد متكتب.", "Earlier from {name}": "رسايل قبل كده من {name}",
  "New email": "إيميل جديد", Cc: "نسخة (Cc)", "No drafts. Ask JARVIS to draft a reply, or write one.": "مفيش مسودات. اطلب من جارفيس يكتب رد، أو اكتب واحد بنفسك.",
  draft: "مسودة", sent: "اتبعت", failed: "فشل", discarded: "اتلغت",
  "Drafted by JARVIS": "كتبها جارفيس", "Your draft": "مسودتك", "Waiting for your approval…": "مستني موافقتك…", "Send…": "ابعت…",

  // calendar
  "Your week, and what you need to know before each meeting.": "أسبوعك، واللي محتاج تعرفه قبل كل اجتماع.",
  "Add event": "ضيف ميعاد", Calendars: "الأجندات", "Previous week": "الأسبوع اللي فات", "Next week": "الأسبوع الجاي",
  "No calendars yet. Add an event, say “schedule a meeting with Ahmed tomorrow at 3pm”, or subscribe to your Google/Outlook calendar in Settings → Accounts.":
    "لسه مفيش أجندات. ضيف ميعاد، أو قول «حط اجتماع مع أحمد بكرة الساعة 3»، أو اشترك في أجندة Google/Outlook بتاعتك من الإعدادات ← الحسابات.",
  "all day": "اليوم كله", When: "امتى", Where: "فين", Who: "مين",
  "Subscribed calendar (read-only)": "أجندة مشترك فيها (قراءة بس)", "Added by JARVIS": "ضافه جارفيس", "Added by you": "ضفته إنت",
  Prepare: "التحضير", "Invite notes": "ملاحظات الدعوة", "Remove “{title}”?": "تشيل «{title}»؟",
  "Add to JARVIS's calendar": "ضيف لأجندة جارفيس", "e.g. Call with Ahmed": "مثلاً: مكالمة مع أحمد", Starts: "يبدأ",
  "Length (minutes)": "المدة (بالدقايق)", With: "مع", "Names or emails, for your reference — nobody is invited.": "أسامي أو إيميلات، للتذكير بس — مفيش حد هيتبعتله دعوة.",
  "Subscribe with your calendar's private iCal address — Google Calendar: Settings → your calendar → “Secret address in iCal format”; Outlook: Settings → Calendar → Shared calendars → Publish → ICS link; iCloud: share as public calendar. JARVIS reads it (no changes to your calendar); the address is stored encrypted.":
    "اشترك بعنوان iCal الخاص بأجندتك — Google Calendar: الإعدادات ← أجندتك ← “Secret address in iCal format”؛ Outlook: الإعدادات ← Calendar ← Shared calendars ← Publish ← ICS link؛ iCloud: شاركها كأجندة عامة. جارفيس بيقراها بس (من غير أي تغيير في أجندتك)، والعنوان بيتحفظ متشفر.",
  "this PC": "الكمبيوتر ده", "Refreshed {when}": "اتحدّثت {when}", "Events you or JARVIS add": "المواعيد اللي بتضيفها إنت أو جارفيس",
  "Remove {name}? Its events disappear from JARVIS (your calendar isn't changed).": "تشيل {name}؟ مواعيدها هتختفي من جارفيس (أجندتك نفسها مش هتتغير).",
  Work: "الشغل", "iCal address (https:// or webcal://)": "عنوان iCal (https:// أو webcal://)", "Reading the calendar…": "بقرا الأجندة…",
  ok: "تمام", error: "فيه مشكلة", new: "جديدة",

  // meetings
  "Record only when you choose, always visibly. Transcribed on this PC; the audio is never kept.": "التسجيل بس لما إنت تختار، وباين دايماً. التفريغ بيتم على الكمبيوتر ده، والصوت عمره ما بيتحفظ.",
  Recording: "بيسجل", "Record a meeting": "سجّل اجتماع", "since {time}": "من {time}", "Stop and write notes": "وقف واكتب الملاحظات",
  "Live transcript": "التفريغ المباشر",
  "Everyone in the meeting should know it's being recorded. JARVIS captures your microphone and what you hear, transcribes it locally with Whisper, then pulls out decisions and action items.":
    "كل اللي في الاجتماع لازم يعرفوا إنه بيتسجل. جارفيس بياخد صوت المايك بتاعك واللي بتسمعه، ويفرّغه على الجهاز بـ Whisper، وبعدين يطلّع القرارات والمهام المطلوبة.",
  "Name (default: the calendar event happening now)": "الاسم (لو سبته: الميعاد اللي شغال دلوقتي في الأجندة)",
  "Waiting for your OK…": "مستني موافقتك…", "Start recording": "ابدأ التسجيل",
  "No meetings recorded yet. Say “record this meeting” when one starts.": "لسه مفيش اجتماعات متسجلة. قول «سجل الاجتماع» أول ما واحد يبدأ.",
  "{n} min": "{n} دقيقة", "{n} decision(s)": "{n} قرار", "{n} action item(s)": "{n} مهمة مطلوبة",
  recording: "بيسجل", transcribing: "بيفرّغ", done: "خلص",
  Decisions: "القرارات", "None said explicitly.": "محدش قال قرار صريح.", "Action items": "المهام المطلوبة", "Add {n} as tasks": "ضيف {n} كمهام",
  "No action items found.": "ملقتش مهام مطلوبة.", "Added {n} task(s).": "اتضاف {n} مهمة.", "Open questions": "أسئلة مفتوحة", "Key points": "أهم النقط",
  "Sentences from the transcript itself": "جمل من التفريغ نفسه", extracted: "مستخرجة",
  "Notes are written when the recording stops.": "الملاحظات بتتكتب لما التسجيل يقف.", "Finishing the transcript…": "بخلّص التفريغ…", "No notes.": "مفيش ملاحظات.",
  Transcript: "التفريغ", "local · Whisper": "محلي · Whisper", "Nothing transcribed yet.": "لسه مفيش حاجة اتفرغت.",
  "Delete this meeting's transcript and notes?": "تمسح تفريغ الاجتماع ده وملاحظاته؟",
};
