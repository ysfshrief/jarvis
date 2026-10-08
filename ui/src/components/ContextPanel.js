import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Bell, Code2, MessageCircle, ShieldAlert, Users, X } from "lucide-react";
import { get } from "../api";
import { useStatus } from "../App";
import { useEvents } from "../events";
import { useOrbState } from "../lib/orbState";
import { useLatestTurn } from "../lib/turns";
import { useApprovals } from "./Approvals";
import { Orb } from "./Orb";
import { Timeline } from "./Timeline";
import { Badge, formatTime, timeAgo, useLoad } from "./ui";
import { tr } from "../lib/i18n";
/** The right-hand panel: what's happening now and what's next, always one glance away. */
export function ContextPanel({ open, onClose }) {
    const { status, connected } = useStatus();
    const approvals = useApprovals();
    const orb = useOrbState(status, connected, approvals.length);
    const turn = useLatestTurn(15000);
    const tasks = useLoad(() => get("/tasks"));
    const reminders = useLoad(() => get("/reminders"));
    useEvents(["tasks"], () => void tasks.reload());
    useEvents(["reminders"], () => void reminders.reload());
    const p = status?.presence?.snapshot;
    const nextReminder = reminders.data?.[0];
    const top = (tasks.data ?? []).slice().sort((a, b) => rank(b.priority) - rank(a.priority)).slice(0, 4);
    const providers = status?.ai?.providers?.filter((x) => x.available) ?? [];
    return (_jsxs("aside", { className: `ctx ${open ? "open" : ""}`, "aria-label": "Context", children: [_jsxs("div", { className: "row between", children: [_jsx("span", { className: "hud-label", children: tr("Context") }), _jsx("button", { className: "btn btn-ghost btn-icon ctx-toggle", onClick: onClose, "aria-label": "Close context panel", children: _jsx(X, { size: 16 }) })] }), _jsxs("div", { className: "card card-glow", children: [_jsxs("div", { className: "row", children: [_jsx(Orb, { state: orb.state, size: 54, label: orb.label }), _jsxs("div", { className: "grow", children: [_jsx("div", { className: "orb-label", style: { textAlign: "start" }, children: orb.state }), _jsx("div", { className: "small truncate", title: orb.label, children: orb.label })] })] }), turn && (_jsxs(_Fragment, { children: [_jsx("div", { className: "divider" }), _jsxs("div", { className: "meta truncate", dir: "auto", title: turn.text, children: ["\u203A ", turn.text] }), _jsx(Timeline, { turn: turn }), turn.tools.length > 0 && (_jsx("div", { className: "steps-list", children: turn.tools.slice(-3).map((t, i) => (_jsxs("div", { className: `step step-${String(t.status).toLowerCase()}`, children: [_jsx("span", { className: "step-tool", children: t.tool }), _jsx("span", { className: "truncate", dir: "auto", children: t.summary })] }, i))) }))] }))] }), approvals.length > 0 && (_jsxs("a", { className: "card card-warn", href: "#/assistant", style: { textDecoration: "none", color: "inherit" }, children: [_jsxs("div", { className: "row", children: [_jsx(ShieldAlert, { size: 16, color: "var(--warn)" }), " ", _jsxs("strong", { children: [approvals.length, " \u00B7 ", tr("Needs your approval")] })] }), _jsx("div", { className: "small muted truncate", dir: "auto", children: approvals[0].summary })] })), p && p.state !== "Unknown" && (_jsxs("div", { className: "card", children: [_jsxs("div", { className: "card-head", children: [_jsx("h3", { children: tr("Now") }), p.inMeeting && _jsxs(Badge, { tone: "warn", children: [_jsx(Bell, { size: 11 }), " held"] })] }), _jsxs("div", { className: "row", children: [p.activity === "coding" ? _jsx(Code2, { size: 16 }) : p.activity === "meeting" ? _jsx(Users, { size: 16 }) : _jsx(MessageCircle, { size: 16 }), _jsx("span", { className: "small", children: activityLabel(p.activity, p.state) })] }), p.activeProcess && _jsxs("div", { className: "small truncate", dir: "auto", title: p.activeWindowTitle ?? "", children: [_jsx("span", { className: "mono", children: p.activeProcess }), " \u00B7 ", _jsx("span", { className: "muted", children: p.activeWindowTitle })] }), p.inMeeting && _jsx("div", { className: "small muted", children: "Non-urgent notifications are held until the meeting ends." })] })), _jsxs("div", { className: "card", children: [_jsxs("div", { className: "card-head", children: [_jsx("h3", { children: tr("Next up") }), _jsx("a", { className: "link small", href: "#/tasks", children: tr("All") })] }), nextReminder ? (_jsxs("div", { className: "row small", children: [_jsx(Bell, { size: 14 }), _jsx("span", { className: "grow truncate", dir: "auto", children: nextReminder.text }), _jsx("span", { className: "meta nowrap", dir: "ltr", children: formatTime(nextReminder.dueAt) })] })) : _jsx("div", { className: "small muted", children: tr("No reminders scheduled.") }), top.length > 0 ? (_jsx("ul", { className: "list", children: top.map((t) => (_jsxs("li", { children: [_jsx("span", { className: "small truncate grow", dir: "auto", children: t.title }), t.priority !== "normal" && _jsx(Badge, { tone: t.priority === "urgent" ? "bad" : t.priority === "high" ? "warn" : "neutral", children: t.priority })] }, t.id))) })) : _jsx("div", { className: "small muted", children: tr("No open tasks.") })] }), _jsxs("div", { className: "card", children: [_jsx("div", { className: "card-head", children: _jsx("h3", { children: tr("Systems") }) }), _jsxs("dl", { className: "kv small", children: [_jsx("dt", { children: tr("Core") }), _jsx("dd", { children: connected ? "linked" : "reconnecting" }), _jsx("dt", { children: tr("Network") }), _jsxs("dd", { children: [status?.online ? "online" : "offline", (status?.queuedActions ?? 0) > 0 ? ` · ${status?.queuedActions} queued` : ""] }), _jsx("dt", { children: tr("AI") }), _jsx("dd", { children: providers.length ? providers.map((x) => x.name.replace(/\s*\(.*\)/, "")).join(", ") : "no model" }), _jsx("dt", { children: tr("Voice") }), _jsx("dd", { children: status?.voice?.sttReady ? (status.voice.microphoneActive ? "mic open" : "ready") : "not set up" }), _jsx("dt", { children: tr("Uptime") }), _jsx("dd", { children: status?.uptimeSeconds != null ? timeAgo(new Date(Date.now() - status.uptimeSeconds * 1000).toISOString()).replace(" ago", "") : "—" })] })] })] }));
}
function rank(p) {
    return p === "urgent" ? 3 : p === "high" ? 2 : p === "normal" ? 1 : 0;
}
function activityLabel(activity, state) {
    if (state === "Idle")
        return "Away from the keyboard";
    if (state === "Locked")
        return "Screen locked";
    return activity === "coding" ? "Coding" : activity === "meeting" ? "In a meeting" : activity === "communication" ? "Communicating" : activity === "browsing" ? "Browsing" : "Working";
}
