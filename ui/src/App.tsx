import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import {
  Activity as ActivityIcon, Brain, CheckSquare, Cpu, LayoutDashboard, Lock, MessageSquare, Pause, Play, Settings as SettingsIcon, Wifi, WifiOff,
} from "lucide-react";
import { get, getToken, onAuthProblem, post, setToken, type Status } from "./api";
import { events, useEvents } from "./events";
import { Orb } from "./components/Orb";
import { Overview } from "./pages/Overview";
import { Assistant } from "./pages/Assistant";
import { Tasks } from "./pages/Tasks";
import { MemoryPage } from "./pages/Memory";
import { ActivityPage } from "./pages/Activity";
import { SystemPage } from "./pages/System";
import { SettingsPage } from "./pages/Settings";
import { useApprovals } from "./components/Approvals";

interface StatusCtx {
  status: Status | null;
  refresh: () => Promise<void>;
  connected: boolean;
}

const Ctx = createContext<StatusCtx>({ status: null, refresh: async () => {}, connected: false });
export const useStatus = () => useContext(Ctx);

const PAGES = [
  { id: "overview", label: "Overview", icon: LayoutDashboard },
  { id: "assistant", label: "Assistant", icon: MessageSquare },
  { id: "tasks", label: "Tasks", icon: CheckSquare },
  { id: "memory", label: "Memory", icon: Brain },
  { id: "activity", label: "Activity", icon: ActivityIcon },
  { id: "system", label: "System", icon: Cpu },
  { id: "settings", label: "Settings", icon: SettingsIcon },
] as const;

type PageId = (typeof PAGES)[number]["id"];

function currentPage(): PageId {
  const id = location.hash.replace(/^#\/?/, "").split(/[?&]/)[0];
  return (PAGES.find((p) => p.id === id)?.id ?? "overview") as PageId;
}

export function navigate(page: PageId) {
  location.hash = `#/${page}`;
}

export function App() {
  const [page, setPage] = useState<PageId>(currentPage);
  const [status, setStatus] = useState<Status | null>(null);
  const [auth, setAuth] = useState<"ok" | "token" | "locked">(getToken() ? "ok" : "token");
  const [connected, setConnected] = useState(false);

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
    const t = window.setInterval(refresh, 20000);
    return () => {
      window.removeEventListener("hashchange", onHash);
      off();
      offConn();
      window.clearInterval(t);
    };
  }, [refresh]);

  // Keep the header live without polling.
  useEvents(["voice", "presence", "connectivity", "runtime", "approval", "tasks", "memory", "reminders", "queue"], (e) => {
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

  if (auth === "token") return <TokenScreen onDone={() => { setAuth("ok"); events.restart(); void refresh(); }} />;
  if (auth === "locked") return <LockScreen onUnlocked={() => { setAuth("ok"); void refresh(); }} />;

  return (
    <Ctx.Provider value={{ status, refresh, connected }}>
      <div className="layout">
        <Sidebar page={page} />
        <main className="main">
          <TopBar />
          <div className="page">
            {page === "overview" && <Overview />}
            {page === "assistant" && <Assistant />}
            {page === "tasks" && <Tasks />}
            {page === "memory" && <MemoryPage />}
            {page === "activity" && <ActivityPage />}
            {page === "system" && <SystemPage />}
            {page === "settings" && <SettingsPage />}
          </div>
        </main>
      </div>
    </Ctx.Provider>
  );
}

function Sidebar({ page }: { page: PageId }) {
  const approvals = useApprovals();
  const { status } = useStatus();
  return (
    <nav className="sidebar" aria-label="Main">
      <div className="brand">
        <Orb state={status?.paused ? "Paused" : status?.voice?.state ?? "Idle"} size={34} />
        <div>
          <div className="brand-name">JARVIS</div>
          <div className="brand-sub">v{status?.version ?? "…"}</div>
        </div>
      </div>
      {PAGES.map((p) => (
        <a key={p.id} href={`#/${p.id}`} className={`nav-item ${page === p.id ? "active" : ""}`} aria-current={page === p.id ? "page" : undefined}>
          <p.icon size={18} />
          <span>{p.label}</span>
          {p.id === "assistant" && approvals.length > 0 && <span className="nav-count">{approvals.length}</span>}
        </a>
      ))}
    </nav>
  );
}

function TopBar() {
  const { status, connected, refresh } = useStatus();
  const voice = status?.voice;
  const presence = status?.presence?.snapshot;
  const togglePause = async () => {
    await post(status?.paused ? "/runtime/resume" : "/runtime/pause");
    await refresh();
  };
  return (
    <header className="topbar">
      <div className="chips">
        <Chip ok={connected} label={connected ? "Runtime connected" : "Reconnecting…"} />
        <Chip ok={status?.online} label={status?.online ? "Online" : "Offline"} icon={status?.online ? <Wifi size={14} /> : <WifiOff size={14} />} />
        <Chip ok={status?.ai?.anyAvailable} label={status?.ai?.anyAvailable ? "AI ready" : "No AI model"} />
        <Chip ok={voice?.sttReady && voice?.audioAvailable ? true : voice ? false : null} label={voiceLabel(status)} />
        {presence && presence.state !== "Unknown" && <Chip ok={null} label={presenceLabel(presence.state, presence.activeProcess)} />}
        {(status?.queuedActions ?? 0) > 0 && <Chip ok={false} label={`${status?.queuedActions} queued`} />}
      </div>
      <div className="topbar-actions">
        <button className="btn btn-ghost" onClick={togglePause} title={status?.paused ? "Resume JARVIS" : "Pause listening and voice"}>
          {status?.paused ? <Play size={16} /> : <Pause size={16} />} {status?.paused ? "Resume" : "Pause"}
        </button>
        {status?.pinSet && (
          <button className="btn btn-ghost" onClick={async () => { await post("/auth/lock"); location.reload(); }} title="Lock the dashboard">
            <Lock size={16} />
          </button>
        )}
      </div>
    </header>
  );
}

function voiceLabel(s: Status | null) {
  const v = s?.voice;
  if (!v) return "Voice …";
  if (s?.paused) return "Voice paused";
  if (!v.audioAvailable) return "No microphone";
  if (!v.sttReady) return "Speech model needed";
  if (v.state === "WakeListening") return "Listening for “Jarvis”";
  if (v.state === "Listening") return "Listening…";
  return "Voice ready";
}

function presenceLabel(state: string, app?: string | null) {
  const s = state === "InMeeting" ? "In a meeting" : state === "Fullscreen" ? "Fullscreen" : state;
  return app && state === "Active" ? `Active · ${app}` : s;
}

function Chip({ ok, label, icon }: { ok: boolean | null | undefined; label: string; icon?: ReactNode }) {
  return (
    <span className={`chip ${ok === true ? "chip-ok" : ok === false ? "chip-bad" : ""}`}>
      {icon ?? <span className={`dot ${ok === true ? "dot-ok" : ok === false ? "dot-bad" : "dot-unknown"}`} />}
      {label}
    </span>
  );
}

function TokenScreen({ onDone }: { onDone: () => void }) {
  const [value, setValue] = useState("");
  return (
    <div className="gate">
      <Orb state="Idle" size={96} />
      <h1>Connect to JARVIS</h1>
      <p className="muted">
        Open the dashboard from the JARVIS tray icon, or paste the access token from <code>%LOCALAPPDATA%\JARVIS\runtime.json</code>.
      </p>
      <form onSubmit={(e) => { e.preventDefault(); setToken(value); onDone(); }} className="gate-form">
        <input className="input" placeholder="Access token" value={value} onChange={(e) => setValue(e.target.value)} autoFocus />
        <button className="btn btn-primary" type="submit" disabled={!value.trim()}>Connect</button>
      </form>
    </div>
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
    <div className="gate">
      <Orb state="Paused" size={96} />
      <h1>JARVIS is locked</h1>
      <p className="muted">Enter your PIN to continue.</p>
      <form onSubmit={(e) => { e.preventDefault(); void submit(); }} className="gate-form">
        <input className="input pin" type="password" inputMode="numeric" autoComplete="current-password" value={pin} onChange={(e) => setPin(e.target.value)} autoFocus aria-label="PIN" />
        <button className="btn btn-primary" type="submit" disabled={pin.length < 4}>Unlock</button>
      </form>
      {error && <div className="error-note">{error}</div>}
    </div>
  );
}
