import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import {
  Activity as ActivityIcon, Brain, CheckSquare, Cpu, LayoutDashboard, Lock, MessageSquare, PanelRight, Pause, Play,
  Settings as SettingsIcon, Terminal, Wifi, WifiOff, Workflow, FolderSearch, Inbox as InboxIcon,
} from "lucide-react";
import { get, getToken, onAuthProblem, post, setToken, type Status } from "./api";
import { events, useEvents } from "./events";
import { Orb } from "./components/Orb";
import { Overview } from "./pages/Overview";
import { Assistant } from "./pages/Assistant";
import { Tasks } from "./pages/Tasks";
import { WorkflowsPage } from "./pages/Workflows";
import { FilesPage } from "./pages/Files";
import { InboxPage } from "./pages/Inbox";
import { MemoryPage } from "./pages/Memory";
import { ActivityPage } from "./pages/Activity";
import { SystemPage } from "./pages/System";
import { SettingsPage } from "./pages/Settings";
import { useApprovals } from "./components/Approvals";
import { ContextPanel } from "./components/ContextPanel";
import { CommandConsole } from "./components/CommandConsole";
import { useOrbState } from "./lib/orbState";
import { settingsStore, useSettings } from "./lib/settings";
import "./lib/sounds";
import { tr } from "./lib/i18n";

interface StatusCtx {
  status: Status | null;
  refresh: () => Promise<void>;
  connected: boolean;
  openConsole: () => void;
}

const Ctx = createContext<StatusCtx>({ status: null, refresh: async () => {}, connected: false, openConsole: () => {} });
export const useStatus = () => useContext(Ctx);

const PAGES = [
  { id: "overview", label: "Overview", icon: LayoutDashboard, section: "Command" },
  { id: "assistant", label: "Assistant", icon: MessageSquare, section: "Command" },
  { id: "inbox", label: "Inbox", icon: InboxIcon, section: "Work" },
  { id: "tasks", label: "Tasks", icon: CheckSquare, section: "Work" },
  { id: "workflows", label: "Workflows", icon: Workflow, section: "Work" },
  { id: "memory", label: "Memory", icon: Brain, section: "Knowledge" },
  { id: "files", label: "Files", icon: FolderSearch, section: "Knowledge" },
  { id: "system", label: "System", icon: Cpu, section: "System" },
  { id: "activity", label: "Activity", icon: ActivityIcon, section: "System" },
  { id: "settings", label: "Settings", icon: SettingsIcon, section: "System" },
] as const;

type PageId = (typeof PAGES)[number]["id"] | "console";

function currentPage(): PageId {
  const id = location.hash.replace(/^#\/?/, "").split(/[?&/]/)[0];
  if (id === "console") return "console";
  return (PAGES.find((p) => p.id === id)?.id ?? "overview") as PageId;
}

export function navigate(page: PageId, sub?: string) {
  location.hash = `#/${page}${sub ? `/${sub}` : ""}`;
}

/** The desktop shell hosts some pages in WebView2; this lets them talk back (e.g. "hide me"). */
export function hostMessage(message: string) {
  const w = window as unknown as { chrome?: { webview?: { postMessage: (m: string) => void } } };
  w.chrome?.webview?.postMessage(message);
  return !!w.chrome?.webview;
}

export function App() {
  const [page, setPage] = useState<PageId>(currentPage);
  const [status, setStatus] = useState<Status | null>(null);
  const [auth, setAuth] = useState<"ok" | "token" | "locked">(getToken() ? "ok" : "token");
  const [connected, setConnected] = useState(false);
  const [consoleOpen, setConsoleOpen] = useState(false);
  const [ctxOpen, setCtxOpen] = useState(false);
  const settings = useSettings();

  const refresh = useCallback(async () => {
    try {
      const s = await get<Status>("/status");
      setStatus(s);
      setAuth(s.locked ? "locked" : "ok");
    } catch {
      /* auth listener handles 401 */
    }
  }, []);

  useEffect(() => {
    const onHash = () => setPage(currentPage());
    window.addEventListener("hashchange", onHash);
    const off = onAuthProblem((code) => setAuth(code === 401 ? "token" : "locked"));
    const offConn = events.onConnection((c) => {
      setConnected(c);
      if (c) void refresh();
    });
    events.start();
    void refresh();
    void settingsStore.load();
    // A slow safety refresh; live changes arrive as events.
    const t = window.setInterval(() => { if (document.visibilityState === "visible") void refresh(); }, 30000);
    return () => {
      window.removeEventListener("hashchange", onHash);
      off();
      offConn();
      window.clearInterval(t);
    };
  }, [refresh]);

  // The command console: Ctrl+K anywhere, or "/" when not typing.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const typing = e.target instanceof HTMLElement && (e.target.closest("input, textarea, select, [contenteditable]") !== null);
      if ((e.ctrlKey && e.key.toLowerCase() === "k") || (e.ctrlKey && e.altKey && e.key.toLowerCase() === "j") || (!typing && e.key === "/")) {
        e.preventDefault();
        setConsoleOpen(true);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  // Keep the header live without polling.
  useEvents(["voice", "presence", "connectivity", "runtime", "approval", "tasks", "memory", "reminders", "queue", "ai.status"], (e) => {
    switch (e.type) {
      case "voice.state":
        setStatus((s) => (s?.voice ? { ...s, voice: { ...s.voice, state: e.data.state, microphoneActive: e.data.microphoneActive } } : s));
        return;
      case "presence.changed":
        setStatus((s) => (s?.presence ? { ...s, presence: { ...s.presence, snapshot: e.data } } : s));
        return;
      case "connectivity.changed":
        setStatus((s) => (s ? { ...s, online: e.data.online } : s));
        return;
      case "runtime.state":
        setStatus((s) => (s ? { ...s, paused: e.data.paused ?? s.paused } : s));
        return;
      default:
        void refresh();
    }
  });

  if (auth === "token") return <TokenScreen onDone={() => { setAuth("ok"); events.restart(); void refresh(); void settingsStore.load(); }} />;
  if (auth === "locked") return <LockScreen onUnlocked={() => { setAuth("ok"); void refresh(); void settingsStore.load(); }} />;

  const value = { status, refresh, connected, openConsole: () => setConsoleOpen(true) };

  if (page === "console") {
    return (
      <Ctx.Provider value={value}>
        <Ambient />
        <div className="console-standalone">
          <CommandConsole standalone onClose={() => hostMessage("hide") || history.back()} />
        </div>
      </Ctx.Provider>
    );
  }

  const showCtx = settings?.appearance?.contextPanel ?? true;
  return (
    <Ctx.Provider value={value}>
      <Ambient />
      <div className="layout">
        <Rail page={page} />
        <main className="main">
          <TopBar onToggleCtx={showCtx ? () => setCtxOpen((o) => !o) : undefined} />
          <div className="page">
            {page === "overview" && <Overview />}
            {page === "assistant" && <Assistant />}
            {page === "inbox" && <InboxPage />}
            {page === "tasks" && <Tasks />}
            {page === "workflows" && <WorkflowsPage />}
            {page === "memory" && <MemoryPage />}
            {page === "files" && <FilesPage />}
            {page === "activity" && <ActivityPage />}
            {page === "system" && <SystemPage />}
            {page === "settings" && <SettingsPage />}
          </div>
        </main>
        {showCtx && <ContextPanel open={ctxOpen} onClose={() => setCtxOpen(false)} />}
      </div>
      {consoleOpen && (
        <div className="console-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) setConsoleOpen(false); }}>
          <CommandConsole onClose={() => setConsoleOpen(false)} />
        </div>
      )}
    </Ctx.Provider>
  );
}

function Ambient() {
  return (
    <>
      <div className="hud-grid" aria-hidden />
      <div className="hud-scan" aria-hidden />
      <div className="hud-sweep" aria-hidden />
    </>
  );
}

function Rail({ page }: { page: PageId }) {
  const approvals = useApprovals();
  const { status, connected, openConsole } = useStatus();
  const orb = useOrbState(status, connected, approvals.length);
  let section = "";
  return (
    <nav className="rail" aria-label="Main">
      <div className="brand">
        <Orb state={orb.state} size={38} label={orb.label} />
        <div>
          <div className="brand-name">JARVIS</div>
          <div className="brand-sub">v{status?.version ?? "…"}</div>
        </div>
      </div>
      {PAGES.map((p) => {
        const head = p.section !== section ? <div className="rail-section">{tr(p.section)}</div> : null;
        section = p.section;
        return (
          <div key={p.id} style={{ display: "contents" }}>
            {head}
            <a href={`#/${p.id}`} className={`nav-item ${page === p.id ? "active" : ""}`} aria-current={page === p.id ? "page" : undefined} title={tr(p.label)}>
              <p.icon size={18} />
              <span>{tr(p.label)}</span>
              {p.id === "assistant" && approvals.length > 0 && <span className="nav-count">{approvals.length}</span>}
            </a>
          </div>
        );
      })}
      <div className="rail-foot">
        <button className="btn btn-ghost" onClick={openConsole} title="Command console (Ctrl+K)">
          <Terminal size={16} /> <span className="nav-label-console">{tr("Console")}</span> <span className="kbd">Ctrl K</span>
        </button>
        <div className="meta">{status?.platformDescription}</div>
      </div>
    </nav>
  );
}

function useClock() {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    // Tick on the minute boundary, not every second: the clock shows minutes.
    let t = 0;
    const schedule = () => {
      t = window.setTimeout(() => { setNow(new Date()); schedule(); }, 60000 - (Date.now() % 60000) + 50);
    };
    schedule();
    return () => window.clearTimeout(t);
  }, []);
  return now;
}

export { useClock };

function TopBar({ onToggleCtx }: { onToggleCtx?: () => void }) {
  const { status, connected, refresh } = useStatus();
  const now = useClock();
  const locale = document.documentElement.lang === "ar" ? "ar-EG" : undefined;
  const voice = status?.voice;
  const presence = status?.presence?.snapshot;
  const togglePause = async () => {
    await post(status?.paused ? "/runtime/resume" : "/runtime/pause");
    await refresh();
  };
  return (
    <header className="topbar">
      <div className="clock" dir="ltr">
        {now.toLocaleTimeString(locale ?? [], { hour: "2-digit", minute: "2-digit" })}
        <small>{now.toLocaleDateString(locale ?? [], { weekday: "short", day: "numeric", month: "short" }).toUpperCase()}</small>
      </div>
      <div className="chips">
        <Chip ok={connected} label={tr(connected ? "Core link" : "Reconnecting")} />
        <Chip ok={status?.online} label={tr(status?.online ? "Online" : "Offline")} icon={status?.online ? <Wifi size={13} /> : <WifiOff size={13} />} />
        <Chip ok={status?.ai?.anyAvailable} label={tr(status?.ai?.anyAvailable ? "AI ready" : "No AI model")} />
        <Chip ok={voice?.sttReady && voice?.audioAvailable ? true : voice ? false : null} label={tr(voiceLabel(status))} warn={!!voice?.microphoneActive} />
        {presence && presence.state !== "Unknown" && <Chip ok={null} label={tr(presenceLabel(presence.state, presence.activeProcess))} />}
        {(status?.queuedActions ?? 0) > 0 && <Chip ok={false} label={`${status?.queuedActions} ${tr("queued")}`} />}
      </div>
      <button className="btn btn-ghost btn-sm" onClick={togglePause} title={status?.paused ? "Resume JARVIS" : "Pause listening and voice"}>
        {status?.paused ? <Play size={14} /> : <Pause size={14} />} {tr(status?.paused ? "Resume" : "Pause")}
      </button>
      {status?.pinSet && (
        <button className="btn btn-ghost btn-icon" onClick={async () => { await post("/auth/lock"); location.reload(); }} title="Lock the dashboard" aria-label="Lock">
          <Lock size={15} />
        </button>
      )}
      {onToggleCtx && (
        <button className="btn btn-ghost btn-icon ctx-toggle" onClick={onToggleCtx} title="Context panel" aria-label="Context panel">
          <PanelRight size={16} />
        </button>
      )}
    </header>
  );
}

function voiceLabel(s: Status | null) {
  const v = s?.voice;
  if (!v) return "Voice";
  if (s?.paused) return "Voice paused";
  if (!v.audioAvailable) return "No microphone";
  if (!v.sttReady) return "Speech model needed";
  if (v.microphoneActive && v.state === "WakeListening") return "Mic on · wake word";
  if (v.state === "Listening") return "Mic on · listening";
  return "Voice ready";
}

function presenceLabel(state: string, app?: string | null) {
  const s = state === "InMeeting" ? "In a meeting" : state === "Fullscreen" ? "Fullscreen" : state;
  return app && state === "Active" ? `${app}` : s;
}

function Chip({ ok, label, icon, warn }: { ok: boolean | null | undefined; label: string; icon?: ReactNode; warn?: boolean }) {
  const cls = warn ? "chip-warn" : ok === true ? "chip-ok" : ok === false ? "chip-bad" : "";
  return (
    <span className={`chip ${cls}`}>
      {icon ?? <span className={`dot ${warn ? "dot-warn" : ok === true ? "dot-ok" : ok === false ? "dot-bad" : "dot-unknown"}`} />}
      {label}
    </span>
  );
}

function TokenScreen({ onDone }: { onDone: () => void }) {
  const [value, setValue] = useState("");
  return (
    <>
      <Ambient />
      <div className="gate">
        <Orb state="idle" size={140} />
        <h1>CONNECT TO JARVIS</h1>
        <p className="muted">
          Open the dashboard from the JARVIS tray icon, or paste the access token from <code>%LOCALAPPDATA%\JARVIS\runtime.json</code>.
        </p>
        <form onSubmit={(e) => { e.preventDefault(); setToken(value); onDone(); }} className="gate-form">
          <input className="input" placeholder="Access token" value={value} onChange={(e) => setValue(e.target.value)} autoFocus />
          <button className="btn btn-primary" type="submit" disabled={!value.trim()}>Connect</button>
        </form>
      </div>
    </>
  );
}

function LockScreen({ onUnlocked }: { onUnlocked: () => void }) {
  const [pin, setPin] = useState("");
  const [error, setError] = useState("");
  const submit = async () => {
    try {
      await post("/auth/unlock", { pin });
      onUnlocked();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Wrong PIN");
      setPin("");
    }
  };
  return (
    <>
      <Ambient />
      <div className="gate">
        <Orb state="offline" size={140} />
        <h1>JARVIS IS LOCKED</h1>
        <p className="muted">Enter your PIN to continue.</p>
        <form onSubmit={(e) => { e.preventDefault(); void submit(); }} className="gate-form">
          <input className="input pin" type="password" inputMode="numeric" autoComplete="current-password" value={pin} onChange={(e) => setPin(e.target.value)} autoFocus aria-label="PIN" />
          <button className="btn btn-primary" type="submit" disabled={pin.length < 4}>Unlock</button>
        </form>
        {error && <div className="error-note">{error}</div>}
      </div>
    </>
  );
}
