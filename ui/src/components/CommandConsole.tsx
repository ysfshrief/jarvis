import { useEffect, useRef, useState } from "react";
import { CornerDownLeft, History, LayoutDashboard, Mic, Square, X } from "lucide-react";
import { get, post, type ActivityEntry, type TurnResult } from "../api";
import { hostMessage, useStatus } from "../App";
import { useOrbState } from "../lib/orbState";
import { useActiveTurn } from "../lib/turns";
import { ApprovalCard, useApprovals } from "./Approvals";
import { Orb } from "./Orb";
import { Timeline } from "./Timeline";
import { StepList } from "./Steps";
import { timeAgo } from "./ui";
import { tr, uiLang } from "../lib/i18n";

const SUGGESTIONS_EN = [
  "What's happening today?",
  "Check my priorities",
  "Show my projects",
  "How's the system?",
  "Remind me in 20 minutes to stretch",
  "إيه اللي ورايا النهارده؟",
];

const SUGGESTIONS_AR = [
  "إيه اللي ورايا النهارده؟",
  "إيه أولوياتي؟",
  "مشاريعي إيه؟",
  "الجهاز عامل إيه؟",
  "فكرني بعد ٢٠ دقيقة أقوم أتمشى",
  "What's happening today?",
];

/** Commands JARVIS handles directly (no AI needed) — offered as completions while typing. */
const COMMANDS = [
  "what's happening today?", "check my priorities", "what are my tasks", "add task ", "remind me in 10 minutes to ",
  "open calculator", "open notepad", "close notepad", "take a screenshot", "how's the system", "volume 40", "mute", "next song",
  "check my email", "what's on my calendar tomorrow", "what's my next meeting", "prepare me for my  meeting", "schedule a meeting with ",
  "record this meeting", "stop recording", "what did we decide?", "what am I tracking", "track the  deal",
  "find documents about ", "what's my latest PDF", "summarize ", "what's on my screen?", "make a plugin that ", "what plugins do I have?",
  "remember that ", "what do you know about ", "research ", "keep me updated on ", "run git status", "what can you do?",
  "إيه اللي ورايا النهارده؟", "إيه أولوياتي؟", "افتح الآلة الحاسبة", "اقفل النوتباد", "خد سكرين شوت", "فكرني بعد ربع ساعة ",
  "شوف الإيميل", "سجل الاجتماع", "وقف التسجيل", "دور على ملفات عن ", "اتعلم عن ", "تابعلي أخبار ", "افتكر إن ",
];

function completionsFor(text: string, history: string[]): string[] {
  const q = text.trim().toLowerCase();
  if (q.length < 2) return [];
  const seen = new Set<string>();
  const out: string[] = [];
  for (const c of [...history, ...COMMANDS]) {
    const k = c.trim().toLowerCase();
    if (k === q || seen.has(k) || !k.includes(q)) continue;
    seen.add(k);
    out.push(c);
    if (out.length >= 6) break;
  }
  // Prefix matches first.
  return out.sort((a, b) => Number(!a.toLowerCase().startsWith(q)) - Number(!b.toLowerCase().startsWith(q)));
}

/**
 * The command console (Ctrl+Alt+J on the desktop, Ctrl+K in the dashboard): one line to ask or
 * command anything, with live progress, approvals and recent requests.
 */
export function CommandConsole({ onClose, standalone = false }: { onClose: () => void; standalone?: boolean }) {
  const { status, connected } = useStatus();
  const approvals = useApprovals();
  const orb = useOrbState(status, connected, approvals.length);
  const active = useActiveTurn();
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<TurnResult | null>(null);
  const [recent, setRecent] = useState<ActivityEntry[]>([]);
  const [listening, setListening] = useState(false);
  const [history, setHistory] = useState<string[]>([]);
  const [historyAt, setHistoryAt] = useState(-1); // -1: not browsing history
  const [picked, setPicked] = useState(-1); // highlighted completion
  const input = useRef<HTMLInputElement>(null);

  const loadRecent = () =>
    get<ActivityEntry[]>("/activity?kind=request&limit=50").then((all) => {
      setRecent(all.slice(0, 6));
      setHistory([...new Set(all.map((a) => a.summary.trim()).filter(Boolean))]);
    }).catch(() => {});

  const completions = historyAt < 0 ? completionsFor(text, history) : [];

  /** ↑/↓ move through completions while typing, otherwise through earlier requests (like a terminal); Tab completes. */
  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (completions.length > 0 && (e.key === "ArrowDown" || e.key === "ArrowUp")) {
      e.preventDefault();
      setPicked((p) => (e.key === "ArrowDown" ? Math.min(completions.length - 1, p + 1) : Math.max(-1, p - 1)));
      return;
    }
    if (completions.length > 0 && e.key === "Tab" && picked >= 0) {
      e.preventDefault();
      setText(completions[picked]);
      setPicked(-1);
      return;
    }
    if (e.key === "ArrowUp" && history.length > 0) {
      e.preventDefault();
      const next = Math.min(history.length - 1, historyAt + 1);
      setHistoryAt(next);
      setText(history[next]);
      return;
    }
    if (e.key === "ArrowDown" && historyAt >= 0) {
      e.preventDefault();
      const next = historyAt - 1;
      setHistoryAt(next);
      setText(next >= 0 ? history[next] : "");
    }
  };

  useEffect(() => {
    input.current?.focus();
    void loadRecent();
    // The desktop host shows/hides this page instead of reloading it; refocus when shown.
    const onFocus = () => { input.current?.focus(); void loadRecent(); };
    window.addEventListener("focus", onFocus);
    return () => window.removeEventListener("focus", onFocus);
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") { e.preventDefault(); onClose(); } };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const send = async (t = picked >= 0 && completions[picked] ? completions[picked] : text) => {
    const q = t.trim();
    setPicked(-1);
    setHistoryAt(-1);
    if (!q || busy) return;
    setBusy(true);
    setResult(null);
    setText("");
    try {
      setResult(await post<TurnResult>("/chat", { text: q, source: "text" }));
    } catch (e) {
      setResult({ reply: e instanceof Error ? e.message : String(e), success: false } as TurnResult);
    } finally {
      setBusy(false);
      void loadRecent();
      input.current?.focus();
    }
  };

  const listen = async () => {
    setListening(true);
    try { await post("/voice/listen"); } catch { /* the voice status explains */ } finally { setListening(false); }
  };

  const voice = status?.voice;
  const voiceUsable = !!voice?.audioAvailable && !!voice?.sttReady && !status?.paused;
  const live = busy || (active && !result) ? active : null;
  const openDashboard = () => {
    if (!hostMessage("dashboard")) location.hash = "#/assistant";
    else onClose();
  };

  return (
    <div className="console card card-glow" role="dialog" aria-label={tr("JARVIS command console")}>
      <div className="console-head">
        <Orb state={orb.state} size={standalone ? 44 : 40} label={orb.label} />
        <div className="grow">
          <div className="hud-label">{tr("Command console")}</div>
          <div className="small muted truncate">{orb.label}</div>
        </div>
        <button className="btn btn-ghost btn-sm" onClick={openDashboard} title={tr("Open the dashboard")}><LayoutDashboard size={14} /> {tr("Dashboard")}</button>
        <button className="btn btn-ghost btn-icon" onClick={onClose} aria-label={tr("Close (Esc)")} title={tr("Close (Esc)")}><X size={16} /></button>
      </div>

      <form className="cmd" onSubmit={(e) => { e.preventDefault(); void send(); }}>
        <span className="prompt" aria-hidden>›</span>
        <input
          ref={input}
          dir="auto"
          placeholder={tr("Ask or command — English or عربي")}
          value={text}
          onChange={(e) => { setText(e.target.value); setHistoryAt(-1); setPicked(-1); }}
          onKeyDown={onKeyDown}
          aria-label={tr("Command input")}
          aria-autocomplete="list"
          aria-controls="cmd-completions"
          aria-activedescendant={picked >= 0 ? `cmd-c${picked}` : undefined}
        />
        <button type="button" className={`btn btn-icon ${listening ? "btn-danger" : "btn-ghost"}`} onClick={listening ? () => post("/voice/stop") : listen} disabled={!voiceUsable && !listening}
          title={voiceUsable ? tr("Speak") : voice?.sttMessage ?? tr("Voice isn't set up")} aria-label={tr("Speak")}>
          {listening ? <Square size={16} /> : <Mic size={16} />}
        </button>
        <button type="submit" className="btn btn-primary btn-icon" disabled={busy || !text.trim()} aria-label={tr("Send")}><CornerDownLeft size={16} /></button>
      </form>

      {completions.length > 0 && (
        <ul className="completions" id="cmd-completions" role="listbox" aria-label={tr("Suggestions")}>
          {completions.map((c, i) => (
            <li key={c} id={`cmd-c${i}`} role="option" aria-selected={i === picked} className={i === picked ? "on" : ""}
              onMouseDown={(e) => { e.preventDefault(); setText(c); setPicked(-1); input.current?.focus(); }} dir="auto">
              {history.includes(c) ? <History size={13} /> : <CornerDownLeft size={13} />} {c}
            </li>
          ))}
        </ul>
      )}

      <div className="console-body">
        {approvals.map((a) => <ApprovalCard key={a.id} approval={a} />)}

        {live && (
          <div className="stack-sm">
            <Timeline turn={live} />
            {live.tools.length > 0 && <StepList steps={live.tools} />}
            {live.draft && <div className="bubble"><div className="bubble-text cursor" dir="auto">{live.draft}</div></div>}
          </div>
        )}

        {result && !busy && (
          <div className={`stack-sm ${result.success ? "" : "msg-error"}`}>
            {result.steps?.length > 0 && <StepList steps={result.steps} />}
            <div className="bubble"><div className="bubble-text" dir="auto">{result.reply}</div></div>
            <div className="row meta">
              {result.route === "ai" ? `${tr("AI")} · ${result.model ?? ""}` : result.route === "deterministic" ? tr("Direct command") : ""}
              {result.durationMs ? ` · ${tr("{n} s", { n: (result.durationMs / 1000).toFixed(1) })}` : ""}
              <button className="link small" onClick={openDashboard}>{tr("Continue in Assistant →")}</button>
            </div>
          </div>
        )}

        {!live && !result && (
          <>
            <div>
              <div className="hud-label" style={{ marginBottom: 6 }}>{tr("Suggestions")}</div>
              <div className="row wrap">
                {(uiLang() === "ar" ? SUGGESTIONS_AR : SUGGESTIONS_EN).map((s) => (
                  <button key={s} className="chip-btn" dir="auto" onClick={() => void send(s)}>{s}</button>
                ))}
              </div>
            </div>
            {recent.length > 0 && (
              <div>
                <div className="hud-label" style={{ marginBottom: 4 }}>{tr("Recent")}</div>
                <div className="recent">
                  {recent.map((r) => (
                    <button key={r.id} onClick={() => { setText(r.summary); input.current?.focus(); }} title={tr("Use again")}>
                      <History size={14} />
                      <span className="grow truncate" dir="auto">{r.summary}</span>
                      <span className="meta nowrap">{timeAgo(r.timestamp)}</span>
                    </button>
                  ))}
                </div>
              </div>
            )}
          </>
        )}
      </div>
      <div className="row meta" style={{ justifyContent: "space-between" }}>
        <span>{tr("Enter to run · ↑↓ history and suggestions · Tab to complete · Esc to close")}</span>
        <span>{tr(status?.ai?.anyAvailable ? "AI online" : "Direct commands only (no AI model)")}</span>
      </div>
    </div>
  );
}
