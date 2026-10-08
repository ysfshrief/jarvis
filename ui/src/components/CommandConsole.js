import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useRef, useState } from "react";
import { CornerDownLeft, History, LayoutDashboard, Mic, Square, X } from "lucide-react";
import { get, post } from "../api";
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
export function CommandConsole({ onClose, standalone = false }) {
    const { status, connected } = useStatus();
    const approvals = useApprovals();
    const orb = useOrbState(status, connected, approvals.length);
    const active = useActiveTurn();
    const [text, setText] = useState("");
    const [busy, setBusy] = useState(false);
    const [result, setResult] = useState(null);
    const [recent, setRecent] = useState([]);
    const [listening, setListening] = useState(false);
    const input = useRef(null);
    const loadRecent = () => get("/activity?kind=request&limit=6").then(setRecent).catch(() => { });
    useEffect(() => {
        input.current?.focus();
        void loadRecent();
        // The desktop host shows/hides this page instead of reloading it; refocus when shown.
        const onFocus = () => { input.current?.focus(); void loadRecent(); };
        window.addEventListener("focus", onFocus);
        return () => window.removeEventListener("focus", onFocus);
    }, []);
    useEffect(() => {
        const onKey = (e) => { if (e.key === "Escape") {
            e.preventDefault();
            onClose();
        } };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, [onClose]);
    const send = async (t = text) => {
        const q = t.trim();
        if (!q || busy)
            return;
        setBusy(true);
        setResult(null);
        setText("");
        try {
            setResult(await post("/chat", { text: q, source: "text" }));
        }
        catch (e) {
            setResult({ reply: e instanceof Error ? e.message : String(e), success: false });
        }
        finally {
            setBusy(false);
            void loadRecent();
            input.current?.focus();
        }
    };
    const listen = async () => {
        setListening(true);
        try {
            await post("/voice/listen");
        }
        catch { /* the voice status explains */ }
        finally {
            setListening(false);
        }
    };
    const voice = status?.voice;
    const voiceUsable = !!voice?.audioAvailable && !!voice?.sttReady && !status?.paused;
    const live = busy || (active && !result) ? active : null;
    const openDashboard = () => {
        if (!hostMessage("dashboard"))
            location.hash = "#/assistant";
        else
            onClose();
    };
    return (_jsxs("div", { className: "console card card-glow", role: "dialog", "aria-label": "JARVIS command console", children: [_jsxs("div", { className: "console-head", children: [_jsx(Orb, { state: orb.state, size: standalone ? 44 : 40, label: orb.label }), _jsxs("div", { className: "grow", children: [_jsx("div", { className: "hud-label", children: tr("Command console") }), _jsx("div", { className: "small muted truncate", children: orb.label })] }), _jsxs("button", { className: "btn btn-ghost btn-sm", onClick: openDashboard, title: "Open the dashboard", children: [_jsx(LayoutDashboard, { size: 14 }), " ", tr("Dashboard")] }), _jsx("button", { className: "btn btn-ghost btn-icon", onClick: onClose, "aria-label": "Close (Esc)", title: "Close (Esc)", children: _jsx(X, { size: 16 }) })] }), _jsxs("form", { className: "cmd", onSubmit: (e) => { e.preventDefault(); void send(); }, children: [_jsx("span", { className: "prompt", "aria-hidden": true, children: "\u203A" }), _jsx("input", { ref: input, dir: "auto", placeholder: tr("Ask or command — English or عربي"), value: text, onChange: (e) => setText(e.target.value), "aria-label": "Command" }), _jsx("button", { type: "button", className: `btn btn-icon ${listening ? "btn-danger" : "btn-ghost"}`, onClick: listening ? () => post("/voice/stop") : listen, disabled: !voiceUsable && !listening, title: voiceUsable ? "Speak" : voice?.sttMessage ?? "Voice isn't set up", "aria-label": "Speak", children: listening ? _jsx(Square, { size: 16 }) : _jsx(Mic, { size: 16 }) }), _jsx("button", { type: "submit", className: "btn btn-primary btn-icon", disabled: busy || !text.trim(), "aria-label": "Send", children: _jsx(CornerDownLeft, { size: 16 }) })] }), _jsxs("div", { className: "console-body", children: [approvals.map((a) => _jsx(ApprovalCard, { approval: a }, a.id)), live && (_jsxs("div", { className: "stack-sm", children: [_jsx(Timeline, { turn: live }), live.tools.length > 0 && _jsx(StepList, { steps: live.tools }), live.draft && _jsx("div", { className: "bubble", children: _jsx("div", { className: "bubble-text cursor", dir: "auto", children: live.draft }) })] })), result && !busy && (_jsxs("div", { className: `stack-sm ${result.success ? "" : "msg-error"}`, children: [result.steps?.length > 0 && _jsx(StepList, { steps: result.steps }), _jsx("div", { className: "bubble", children: _jsx("div", { className: "bubble-text", dir: "auto", children: result.reply }) }), _jsxs("div", { className: "row meta", children: [result.route === "ai" ? `AI · ${result.model ?? ""}` : result.route === "deterministic" ? tr("Direct command") : "", result.durationMs ? ` · ${(result.durationMs / 1000).toFixed(1)} s` : "", _jsx("button", { className: "link small", onClick: openDashboard, children: tr("Continue in Assistant →") })] })] })), !live && !result && (_jsxs(_Fragment, { children: [_jsxs("div", { children: [_jsx("div", { className: "hud-label", style: { marginBottom: 6 }, children: tr("Suggestions") }), _jsx("div", { className: "row wrap", children: SUGGESTIONS.map((s) => (_jsx("button", { className: "chip-btn", dir: "auto", onClick: () => void send(s), children: s }, s))) })] }), recent.length > 0 && (_jsxs("div", { children: [_jsx("div", { className: "hud-label", style: { marginBottom: 4 }, children: tr("Recent") }), _jsx("div", { className: "recent", children: recent.map((r) => (_jsxs("button", { onClick: () => { setText(r.summary); input.current?.focus(); }, title: "Use again", children: [_jsx(History, { size: 14 }), _jsx("span", { className: "grow truncate", dir: "auto", children: r.summary }), _jsx("span", { className: "meta nowrap", children: timeAgo(r.timestamp) })] }, r.id))) })] }))] }))] }), _jsxs("div", { className: "row meta", style: { justifyContent: "space-between" }, children: [_jsx("span", { children: tr("Enter to run · Esc to close") }), _jsx("span", { children: tr(status?.ai?.anyAvailable ? "AI online" : "Direct commands only (no AI model)") })] })] }));
}
