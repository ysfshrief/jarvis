import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { createContext, useCallback, useContext, useEffect, useState } from "react";
import { Activity as ActivityIcon, Brain, CheckSquare, Cpu, LayoutDashboard, Lock, MessageSquare, PanelRight, Pause, Play, Settings as SettingsIcon, Terminal, Wifi, WifiOff, Workflow, FolderSearch, } from "lucide-react";
import { get, getToken, onAuthProblem, post, setToken } from "./api";
import { events, useEvents } from "./events";
import { Orb } from "./components/Orb";
import { Overview } from "./pages/Overview";
import { Assistant } from "./pages/Assistant";
import { Tasks } from "./pages/Tasks";
import { WorkflowsPage } from "./pages/Workflows";
import { FilesPage } from "./pages/Files";
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
const Ctx = createContext({ status: null, refresh: async () => { }, connected: false, openConsole: () => { } });
export const useStatus = () => useContext(Ctx);
const PAGES = [
    { id: "overview", label: "Overview", icon: LayoutDashboard, section: "Command" },
    { id: "assistant", label: "Assistant", icon: MessageSquare, section: "Command" },
    { id: "tasks", label: "Tasks", icon: CheckSquare, section: "Work" },
    { id: "workflows", label: "Workflows", icon: Workflow, section: "Work" },
    { id: "memory", label: "Memory", icon: Brain, section: "Knowledge" },
    { id: "files", label: "Files", icon: FolderSearch, section: "Knowledge" },
    { id: "system", label: "System", icon: Cpu, section: "System" },
    { id: "activity", label: "Activity", icon: ActivityIcon, section: "System" },
    { id: "settings", label: "Settings", icon: SettingsIcon, section: "System" },
];
function currentPage() {
    const id = location.hash.replace(/^#\/?/, "").split(/[?&/]/)[0];
    if (id === "console")
        return "console";
    return (PAGES.find((p) => p.id === id)?.id ?? "overview");
}
export function navigate(page, sub) {
    location.hash = `#/${page}${sub ? `/${sub}` : ""}`;
}
/** The desktop shell hosts some pages in WebView2; this lets them talk back (e.g. "hide me"). */
export function hostMessage(message) {
    const w = window;
    w.chrome?.webview?.postMessage(message);
    return !!w.chrome?.webview;
}
export function App() {
    const [page, setPage] = useState(currentPage);
    const [status, setStatus] = useState(null);
    const [auth, setAuth] = useState(getToken() ? "ok" : "token");
    const [connected, setConnected] = useState(false);
    const [consoleOpen, setConsoleOpen] = useState(false);
    const [ctxOpen, setCtxOpen] = useState(false);
    const settings = useSettings();
    const refresh = useCallback(async () => {
        try {
            const s = await get("/status");
            setStatus(s);
            setAuth(s.locked ? "locked" : "ok");
        }
        catch {
            /* auth listener handles 401 */
        }
    }, []);
    useEffect(() => {
        const onHash = () => setPage(currentPage());
        window.addEventListener("hashchange", onHash);
        const off = onAuthProblem((code) => setAuth(code === 401 ? "token" : "locked"));
        const offConn = events.onConnection((c) => {
            setConnected(c);
            if (c)
                void refresh();
        });
        events.start();
        void refresh();
        void settingsStore.load();
        // A slow safety refresh; live changes arrive as events.
        const t = window.setInterval(() => { if (document.visibilityState === "visible")
            void refresh(); }, 30000);
        return () => {
            window.removeEventListener("hashchange", onHash);
            off();
            offConn();
            window.clearInterval(t);
        };
    }, [refresh]);
    // The command console: Ctrl+K anywhere, or "/" when not typing.
    useEffect(() => {
        const onKey = (e) => {
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
    if (auth === "token")
        return _jsx(TokenScreen, { onDone: () => { setAuth("ok"); events.restart(); void refresh(); void settingsStore.load(); } });
    if (auth === "locked")
        return _jsx(LockScreen, { onUnlocked: () => { setAuth("ok"); void refresh(); void settingsStore.load(); } });
    const value = { status, refresh, connected, openConsole: () => setConsoleOpen(true) };
    if (page === "console") {
        return (_jsxs(Ctx.Provider, { value: value, children: [_jsx(Ambient, {}), _jsx("div", { className: "console-standalone", children: _jsx(CommandConsole, { standalone: true, onClose: () => hostMessage("hide") || history.back() }) })] }));
    }
    const showCtx = settings?.appearance?.contextPanel ?? true;
    return (_jsxs(Ctx.Provider, { value: value, children: [_jsx(Ambient, {}), _jsxs("div", { className: "layout", children: [_jsx(Rail, { page: page }), _jsxs("main", { className: "main", children: [_jsx(TopBar, { onToggleCtx: showCtx ? () => setCtxOpen((o) => !o) : undefined }), _jsxs("div", { className: "page", children: [page === "overview" && _jsx(Overview, {}), page === "assistant" && _jsx(Assistant, {}), page === "tasks" && _jsx(Tasks, {}), page === "workflows" && _jsx(WorkflowsPage, {}), page === "memory" && _jsx(MemoryPage, {}), page === "files" && _jsx(FilesPage, {}), page === "activity" && _jsx(ActivityPage, {}), page === "system" && _jsx(SystemPage, {}), page === "settings" && _jsx(SettingsPage, {})] })] }), showCtx && _jsx(ContextPanel, { open: ctxOpen, onClose: () => setCtxOpen(false) })] }), consoleOpen && (_jsx("div", { className: "console-backdrop", onMouseDown: (e) => { if (e.target === e.currentTarget)
                    setConsoleOpen(false); }, children: _jsx(CommandConsole, { onClose: () => setConsoleOpen(false) }) }))] }));
}
function Ambient() {
    return (_jsxs(_Fragment, { children: [_jsx("div", { className: "hud-grid", "aria-hidden": true }), _jsx("div", { className: "hud-scan", "aria-hidden": true }), _jsx("div", { className: "hud-sweep", "aria-hidden": true })] }));
}
function Rail({ page }) {
    const approvals = useApprovals();
    const { status, connected, openConsole } = useStatus();
    const orb = useOrbState(status, connected, approvals.length);
    let section = "";
    return (_jsxs("nav", { className: "rail", "aria-label": "Main", children: [_jsxs("div", { className: "brand", children: [_jsx(Orb, { state: orb.state, size: 38, label: orb.label }), _jsxs("div", { children: [_jsx("div", { className: "brand-name", children: "JARVIS" }), _jsxs("div", { className: "brand-sub", children: ["v", status?.version ?? "…"] })] })] }), PAGES.map((p) => {
                const head = p.section !== section ? _jsx("div", { className: "rail-section", children: tr(p.section) }) : null;
                section = p.section;
                return (_jsxs("div", { style: { display: "contents" }, children: [head, _jsxs("a", { href: `#/${p.id}`, className: `nav-item ${page === p.id ? "active" : ""}`, "aria-current": page === p.id ? "page" : undefined, title: tr(p.label), children: [_jsx(p.icon, { size: 18 }), _jsx("span", { children: tr(p.label) }), p.id === "assistant" && approvals.length > 0 && _jsx("span", { className: "nav-count", children: approvals.length })] })] }, p.id));
            }), _jsxs("div", { className: "rail-foot", children: [_jsxs("button", { className: "btn btn-ghost", onClick: openConsole, title: "Command console (Ctrl+K)", children: [_jsx(Terminal, { size: 16 }), " ", _jsx("span", { className: "nav-label-console", children: tr("Console") }), " ", _jsx("span", { className: "kbd", children: "Ctrl K" })] }), _jsx("div", { className: "meta", children: status?.platformDescription })] })] }));
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
function TopBar({ onToggleCtx }) {
    const { status, connected, refresh } = useStatus();
    const now = useClock();
    const locale = document.documentElement.lang === "ar" ? "ar-EG" : undefined;
    const voice = status?.voice;
    const presence = status?.presence?.snapshot;
    const togglePause = async () => {
        await post(status?.paused ? "/runtime/resume" : "/runtime/pause");
        await refresh();
    };
    return (_jsxs("header", { className: "topbar", children: [_jsxs("div", { className: "clock", dir: "ltr", children: [now.toLocaleTimeString(locale ?? [], { hour: "2-digit", minute: "2-digit" }), _jsx("small", { children: now.toLocaleDateString(locale ?? [], { weekday: "short", day: "numeric", month: "short" }).toUpperCase() })] }), _jsxs("div", { className: "chips", children: [_jsx(Chip, { ok: connected, label: tr(connected ? "Core link" : "Reconnecting") }), _jsx(Chip, { ok: status?.online, label: tr(status?.online ? "Online" : "Offline"), icon: status?.online ? _jsx(Wifi, { size: 13 }) : _jsx(WifiOff, { size: 13 }) }), _jsx(Chip, { ok: status?.ai?.anyAvailable, label: tr(status?.ai?.anyAvailable ? "AI ready" : "No AI model") }), _jsx(Chip, { ok: voice?.sttReady && voice?.audioAvailable ? true : voice ? false : null, label: tr(voiceLabel(status)), warn: !!voice?.microphoneActive }), presence && presence.state !== "Unknown" && _jsx(Chip, { ok: null, label: tr(presenceLabel(presence.state, presence.activeProcess)) }), (status?.queuedActions ?? 0) > 0 && _jsx(Chip, { ok: false, label: `${status?.queuedActions} ${tr("queued")}` })] }), _jsxs("button", { className: "btn btn-ghost btn-sm", onClick: togglePause, title: status?.paused ? "Resume JARVIS" : "Pause listening and voice", children: [status?.paused ? _jsx(Play, { size: 14 }) : _jsx(Pause, { size: 14 }), " ", tr(status?.paused ? "Resume" : "Pause")] }), status?.pinSet && (_jsx("button", { className: "btn btn-ghost btn-icon", onClick: async () => { await post("/auth/lock"); location.reload(); }, title: "Lock the dashboard", "aria-label": "Lock", children: _jsx(Lock, { size: 15 }) })), onToggleCtx && (_jsx("button", { className: "btn btn-ghost btn-icon ctx-toggle", onClick: onToggleCtx, title: "Context panel", "aria-label": "Context panel", children: _jsx(PanelRight, { size: 16 }) }))] }));
}
function voiceLabel(s) {
    const v = s?.voice;
    if (!v)
        return "Voice";
    if (s?.paused)
        return "Voice paused";
    if (!v.audioAvailable)
        return "No microphone";
    if (!v.sttReady)
        return "Speech model needed";
    if (v.microphoneActive && v.state === "WakeListening")
        return "Mic on · wake word";
    if (v.state === "Listening")
        return "Mic on · listening";
    return "Voice ready";
}
function presenceLabel(state, app) {
    const s = state === "InMeeting" ? "In a meeting" : state === "Fullscreen" ? "Fullscreen" : state;
    return app && state === "Active" ? `${app}` : s;
}
function Chip({ ok, label, icon, warn }) {
    const cls = warn ? "chip-warn" : ok === true ? "chip-ok" : ok === false ? "chip-bad" : "";
    return (_jsxs("span", { className: `chip ${cls}`, children: [icon ?? _jsx("span", { className: `dot ${warn ? "dot-warn" : ok === true ? "dot-ok" : ok === false ? "dot-bad" : "dot-unknown"}` }), label] }));
}
function TokenScreen({ onDone }) {
    const [value, setValue] = useState("");
    return (_jsxs(_Fragment, { children: [_jsx(Ambient, {}), _jsxs("div", { className: "gate", children: [_jsx(Orb, { state: "idle", size: 140 }), _jsx("h1", { children: "CONNECT TO JARVIS" }), _jsxs("p", { className: "muted", children: ["Open the dashboard from the JARVIS tray icon, or paste the access token from ", _jsx("code", { children: "%LOCALAPPDATA%\\JARVIS\\runtime.json" }), "."] }), _jsxs("form", { onSubmit: (e) => { e.preventDefault(); setToken(value); onDone(); }, className: "gate-form", children: [_jsx("input", { className: "input", placeholder: "Access token", value: value, onChange: (e) => setValue(e.target.value), autoFocus: true }), _jsx("button", { className: "btn btn-primary", type: "submit", disabled: !value.trim(), children: "Connect" })] })] })] }));
}
function LockScreen({ onUnlocked }) {
    const [pin, setPin] = useState("");
    const [error, setError] = useState("");
    const submit = async () => {
        try {
            await post("/auth/unlock", { pin });
            onUnlocked();
        }
        catch (e) {
            setError(e instanceof Error ? e.message : "Wrong PIN");
            setPin("");
        }
    };
    return (_jsxs(_Fragment, { children: [_jsx(Ambient, {}), _jsxs("div", { className: "gate", children: [_jsx(Orb, { state: "offline", size: 140 }), _jsx("h1", { children: "JARVIS IS LOCKED" }), _jsx("p", { className: "muted", children: "Enter your PIN to continue." }), _jsxs("form", { onSubmit: (e) => { e.preventDefault(); void submit(); }, className: "gate-form", children: [_jsx("input", { className: "input pin", type: "password", inputMode: "numeric", autoComplete: "current-password", value: pin, onChange: (e) => setPin(e.target.value), autoFocus: true, "aria-label": "PIN" }), _jsx("button", { className: "btn btn-primary", type: "submit", disabled: pin.length < 4, children: "Unlock" })] }), error && _jsx("div", { className: "error-note", children: error })] })] }));
}
