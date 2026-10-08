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
import { tr } from "../lib/i18n";

const SUGGESTIONS = [
  "What's happening today?",
  "Check my priorities",
  "Show my projects",
  "How's the system?",
  "Remind me in 20 minutes to call Ahmed",
  "إيه اللي ورايا النهارده؟",
];

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
  const input = useRef<HTMLInputElement>(null);

  const loadRecent = () =>
    get<ActivityEntry[]>("/activity?kind=request&limit=6").then(setRecent).catch(() => {});

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

  const send = async (t = text) => {
    const q = t.trim();
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
    <div className="console card card-glow" role="dialog" aria-label="JARVIS command console">
      <div className="console-head">
        <Orb state={orb.state} size={standalone ? 44 : 40} label={orb.label} />
        <div className="grow">
          <div className="hud-label">{tr("Command console")}</div>
          <div className="small muted truncate">{orb.label}</div>
        </div>
        <button className="btn btn-ghost btn-sm" onClick={openDashboard} title="Open the dashboard"><LayoutDashboard size={14} /> {tr("Dashboard")}</button>
        <button className="btn btn-ghost btn-icon" onClick={onClose} aria-label="Close (Esc)" title="Close (Esc)"><X size={16} /></button>
      </div>

      <form className="cmd" onSubmit={(e) => { e.preventDefault(); void send(); }}>
        <span className="prompt" aria-hidden>›</span>
        <input
          ref={input}
          dir="auto"
          placeholder={tr("Ask or command — English or عربي")}
          value={text}
          onChange={(e) => setText(e.target.value)}
          aria-label="Command"
        />
        <button type="button" className={`btn btn-icon ${listening ? "btn-danger" : "btn-ghost"}`} onClick={listening ? () => post("/voice/stop") : listen} disabled={!voiceUsable && !listening}
          title={voiceUsable ? "Speak" : voice?.sttMessage ?? "Voice isn't set up"} aria-label="Speak">
          {listening ? <Square size={16} /> : <Mic size={16} />}
        </button>
        <button type="submit" className="btn btn-primary btn-icon" disabled={busy || !text.trim()} aria-label="Send"><CornerDownLeft size={16} /></button>
      </form>

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
              {result.route === "ai" ? `AI · ${result.model ?? ""}` : result.route === "deterministic" ? tr("Direct command") : ""}
              {result.durationMs ? ` · ${(result.durationMs / 1000).toFixed(1)} s` : ""}
              <button className="link small" onClick={openDashboard}>{tr("Continue in Assistant →")}</button>
            </div>
          </div>
        )}

        {!live && !result && (
          <>
            <div>
              <div className="hud-label" style={{ marginBottom: 6 }}>{tr("Suggestions")}</div>
              <div className="row wrap">
                {SUGGESTIONS.map((s) => (
                  <button key={s} className="chip-btn" dir="auto" onClick={() => void send(s)}>{s}</button>
                ))}
              </div>
            </div>
            {recent.length > 0 && (
              <div>
                <div className="hud-label" style={{ marginBottom: 4 }}>{tr("Recent")}</div>
                <div className="recent">
                  {recent.map((r) => (
                    <button key={r.id} onClick={() => { setText(r.summary); input.current?.focus(); }} title="Use again">
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
        <span>{tr("Enter to run · Esc to close")}</span>
        <span>{tr(status?.ai?.anyAvailable ? "AI online" : "Direct commands only (no AI model)")}</span>
      </div>
    </div>
  );
}
