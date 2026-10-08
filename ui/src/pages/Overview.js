import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from "react";
import { ArrowDown, ArrowUp, BatteryCharging, BatteryMedium, Bell, Code2, CornerDownLeft, MessageCircle, Users } from "lucide-react";
import { get, post } from "../api";
import { navigate, useClock, useStatus } from "../App";
import { useEvents } from "../events";
import { ApprovalCard, useApprovals } from "../components/Approvals";
import { Orb } from "../components/Orb";
import { StepList } from "../components/Steps";
import { Timeline } from "../components/Timeline";
import { Badge, Card, Empty, fmtBytes, formatTime, Gauge, Sparkline, timeAgo, useLoad } from "../components/ui";
import { useMetrics } from "../lib/metrics";
import { useOrbState } from "../lib/orbState";
import { useActiveTurn } from "../lib/turns";
import { tr } from "../lib/i18n";
/** The cinematic home: JARVIS at the centre, the machine on the left, your day on the right. */
export function Overview() {
    const { status, connected } = useStatus();
    const approvals = useApprovals();
    const orb = useOrbState(status, connected, approvals.length);
    const now = useClock();
    const tasks = useLoad(() => get("/tasks"));
    const reminders = useLoad(() => get("/reminders"));
    const activity = useLoad(() => get("/activity?limit=6&kind=request"));
    const notes = useLoad(() => get("/notifications?limit=5"));
    useEvents(["tasks", "reminders"], () => {
        void tasks.reload();
        void reminders.reload();
    });
    useEvents(["activity"], (e) => {
        const a = e.data;
        if (a.kind === "request")
            activity.setData((cur) => [a, ...(cur ?? [])].slice(0, 6));
    });
    useEvents(["notification"], () => void notes.reload());
    const hour = now.getHours();
    const greeting = hour < 5 ? "Working late" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
    const ar = document.documentElement.lang === "ar";
    const loc = ar ? "ar-EG" : [];
    const who = (ar ? status?.honorificAr : status?.honorific) || status?.userName || "";
    return (_jsxs("div", { className: "home", children: [_jsxs("div", { className: "home-side", children: [_jsx(MachineCard, {}), _jsx(NowCard, {})] }), _jsxs("div", { className: "home-center", children: [_jsxs("div", { className: "home-orb-wrap", children: [_jsx(Orb, { state: orb.state, size: "100%", label: orb.label, hollow: true }), _jsxs("div", { className: "home-time", dir: "ltr", children: [_jsx("div", { className: "d", children: now.toLocaleDateString(loc, { weekday: "long" }) }), _jsx("div", { className: "t", children: now.toLocaleTimeString(loc, { hour: "2-digit", minute: "2-digit" }) }), _jsx("div", { className: "d", children: now.toLocaleDateString(loc, { day: "numeric", month: "long", year: "numeric" }) })] })] }), _jsx("div", { className: "orb-label", "data-state": orb.state, children: orb.label }), _jsxs("div", { className: "greeting", children: [_jsxs("h1", { children: [tr(greeting), who ? `${document.documentElement.lang === "ar" ? " " : ", "}${who}` : "", document.documentElement.lang === "ar" ? "" : "."] }), _jsx("div", { className: "sub", children: summaryLine(tasks.data ?? [], reminders.data ?? [], approvals.length, status?.queuedActions ?? 0) })] }), _jsx(HomeCommand, {}), approvals.map((a) => _jsx("div", { className: "reply-card", children: _jsx(ApprovalCard, { approval: a }) }, a.id)), !status?.ai?.anyAvailable && status && (_jsxs(Card, { title: tr("Enable conversation (free, local)"), className: "reply-card", children: [_jsx("p", { className: "small", children: "Direct commands work now. For open conversation and multi-step planning, JARVIS needs a local model:" }), _jsxs("ol", { className: "steps small", children: [_jsxs("li", { children: ["Install ", _jsx("strong", { children: "Ollama" }), " from ", _jsx("code", { children: "ollama.com" }), "."] }), _jsxs("li", { children: ["Download a model in ", _jsx("a", { href: "#/settings/ai", children: "Settings \u2192 AI" }), " (qwen2.5:7b recommended, ~4.7 GB)."] })] })] }))] }), _jsxs("div", { className: "home-side right", children: [_jsx(Card, { title: tr("Priorities"), actions: _jsx("a", { href: "#/tasks", className: "link small", children: tr("All") }), children: tasks.data && tasks.data.length > 0 ? (_jsx("ul", { className: "list", children: tasks.data.slice().sort((a, b) => prio(b.priority) - prio(a.priority)).slice(0, 5).map((t) => (_jsxs("li", { children: [_jsx("span", { className: "truncate grow", dir: "auto", children: t.title }), t.dueAt && _jsx("span", { className: "meta nowrap", dir: "ltr", children: formatTime(t.dueAt) }), t.priority !== "normal" && _jsx(Badge, { tone: t.priority === "urgent" ? "bad" : t.priority === "high" ? "warn" : "neutral", children: t.priority })] }, t.id))) })) : _jsx(Empty, { children: tr("No open tasks.") }) }), _jsx(Card, { title: tr("Upcoming"), actions: _jsx("a", { href: "#/tasks", className: "link small", children: tr("All") }), children: reminders.data && reminders.data.length > 0 ? (_jsx("ul", { className: "list", children: reminders.data.slice(0, 5).map((r) => (_jsxs("li", { children: [_jsx(Bell, { size: 13, className: "dim" }), _jsx("span", { className: "truncate grow", dir: "auto", children: r.text }), _jsx("span", { className: "meta nowrap", dir: "ltr", children: formatTime(r.dueAt) })] }, r.id))) })) : _jsx(Empty, { children: tr("Nothing scheduled.") }) }), _jsx(Card, { title: tr("Recent"), actions: _jsx("a", { href: "#/activity", className: "link small", children: tr("Log") }), children: activity.data && activity.data.length > 0 ? (_jsx("ul", { className: "list", children: activity.data.map((a) => (_jsxs("li", { children: [_jsx("span", { className: `dot ${a.status === "ok" ? "dot-ok" : a.status === "failed" ? "dot-bad" : "dot-unknown"}` }), _jsx("span", { className: "truncate grow", dir: "auto", title: a.summary, children: a.summary }), _jsx("span", { className: "meta nowrap", dir: "ltr", children: timeAgo(a.timestamp) })] }, a.id))) })) : _jsx(Empty, { children: tr("Nothing yet.") }) }), notes.data && notes.data.length > 0 && (_jsx(Card, { title: tr("Notifications"), actions: _jsx("a", { href: "#/system", className: "link small", children: tr("All") }), children: _jsx("ul", { className: "list", children: notes.data.slice(0, 4).map((n) => (_jsxs("li", { children: [_jsx("span", { className: "truncate grow", dir: "auto", children: n.title }), _jsx(Badge, { tone: n.status === "held" ? "info" : "neutral", children: n.status })] }, n.id))) }) }))] })] }));
}
function prio(p) {
    return p === "urgent" ? 3 : p === "high" ? 2 : p === "normal" ? 1 : 0;
}
function summaryLine(tasks, reminders, approvals, queued) {
    const ar = document.documentElement.lang === "ar";
    const parts = [];
    const pl = (n, one, many) => `${n} ${n === 1 ? one : many}`;
    if (approvals)
        parts.push(ar ? `${approvals} حاجة مستنية موافقتك` : `${pl(approvals, "action", "actions")} waiting for your approval`);
    const soon = reminders.filter((r) => new Date(r.dueAt).getTime() - Date.now() < 3 * 3600_000).length;
    if (soon)
        parts.push(ar ? `${soon} تذكير في الكام ساعة الجايين` : `${pl(soon, "reminder", "reminders")} in the next few hours`);
    const urgent = tasks.filter((t) => t.priority === "urgent" || t.priority === "high").length;
    if (urgent)
        parts.push(ar ? `${urgent} مهمة مهمة` : `${pl(urgent, "high-priority task", "high-priority tasks")}`);
    else if (tasks.length)
        parts.push(ar ? `${tasks.length} مهمة مفتوحة` : `${pl(tasks.length, "open task", "open tasks")}`);
    if (queued)
        parts.push(ar ? `${queued} حاجة مستنية النت` : `${pl(queued, "action", "actions")} queued until we're back online`);
    return parts.length ? parts.join(" · ") + (ar ? "" : ".") : tr("All clear. Ask me anything, or say “Jarvis”.");
}
function MachineCard() {
    const { data } = useMetrics(3000);
    const c = data?.current;
    const hist = data?.history ?? [];
    return (_jsxs(Card, { title: tr("Systems"), actions: _jsx("a", { href: "#/system", className: "link small", children: tr("Details") }), children: [_jsxs("div", { className: "row", style: { justifyContent: "space-around", flexWrap: "wrap", gap: 6 }, children: [_jsx(Gauge, { value: c?.cpuPercent, label: "CPU", size: 96 }), _jsx(Gauge, { value: c?.memoryPercent, label: "Memory", size: 96 }), c?.gpuPercent != null ? _jsx(Gauge, { value: c.gpuPercent, label: "GPU", size: 96 }) : null] }), _jsx(Sparkline, { values: hist.map((h) => h.cpuPercent), max: 100, height: 44 }), _jsxs("div", { className: "row between small", children: [_jsxs("span", { className: "row", title: "Download", children: [_jsx(ArrowDown, { size: 13, className: "dim" }), " ", fmtBytes(c?.netDownBytesPerSec, true)] }), _jsxs("span", { className: "row", title: "Upload", children: [_jsx(ArrowUp, { size: 13, className: "dim" }), " ", fmtBytes(c?.netUpBytesPerSec, true)] }), c?.batteryPercent != null && (_jsxs("span", { className: "row", title: "Battery", children: [c.charging ? _jsx(BatteryCharging, { size: 14 }) : _jsx(BatteryMedium, { size: 14 }), " ", c.batteryPercent, "%"] }))] }), _jsxs("div", { className: "meta", children: ["JARVIS itself: ", c ? `${Math.round(c.runtimeMemoryMb)} MB · ${c.runtimeCpuPercent.toFixed(1)}% CPU` : "—"] })] }));
}
function NowCard() {
    const { status } = useStatus();
    const p = status?.presence?.snapshot;
    if (!status?.presence?.supported || !p)
        return null;
    const icon = p.activity === "coding" ? _jsx(Code2, { size: 16 }) : p.activity === "meeting" ? _jsx(Users, { size: 16 }) : _jsx(MessageCircle, { size: 16 });
    const hint = p.activity === "coding"
        ? "Try “build this project and tell me what failed” — I'll ask before running anything that changes files."
        : p.inMeeting
            ? "Holding non-urgent notifications until the meeting ends."
            : null;
    return (_jsxs(Card, { title: tr("Now"), children: [_jsxs("div", { className: "row", children: [icon, _jsx("strong", { className: "small", children: p.state === "Idle" ? "Away" : p.activity === "other" ? "Working" : p.activity }), p.inMeeting && _jsx(Badge, { tone: "warn", children: "meeting" }), p.isFullscreen && _jsx(Badge, { tone: "info", children: "fullscreen" })] }), p.activeProcess && (_jsxs("div", { className: "small truncate", title: p.activeWindowTitle ?? "", children: [_jsx("span", { className: "mono", children: p.activeProcess }), " ", _jsx("span", { className: "muted", dir: "auto", children: p.activeWindowTitle })] })), hint && _jsx("p", { className: "small muted", children: hint })] }));
}
function HomeCommand() {
    const [text, setText] = useState("");
    const [busy, setBusy] = useState(false);
    const [last, setLast] = useState(null);
    const active = useActiveTurn();
    const send = async () => {
        if (!text.trim() || busy)
            return;
        setBusy(true);
        setLast(null);
        try {
            setLast(await post("/chat", { text }));
            setText("");
        }
        catch (e) {
            setLast({ reply: e instanceof Error ? e.message : String(e), success: false });
        }
        finally {
            setBusy(false);
        }
    };
    return (_jsxs(_Fragment, { children: [_jsxs("form", { className: "cmd", onSubmit: (e) => { e.preventDefault(); void send(); }, children: [_jsx("span", { className: "prompt", "aria-hidden": true, children: "\u203A" }), _jsx("input", { dir: "auto", placeholder: tr("Ask JARVIS… “what's happening today?”, “افتح VS Code”"), value: text, onChange: (e) => setText(e.target.value), "aria-label": "Ask JARVIS" }), _jsx("span", { className: "kbd", title: "Command console", children: "Ctrl K" }), _jsx("button", { className: "btn btn-primary btn-icon", disabled: busy || !text.trim(), type: "submit", "aria-label": "Send", children: _jsx(CornerDownLeft, { size: 16 }) })] }), busy && active && (_jsxs("div", { className: "reply-card stack-sm", children: [_jsx(Timeline, { turn: active }), active.tools.length > 0 && _jsx(StepList, { steps: active.tools }), active.draft && _jsx("div", { className: "bubble", children: _jsx("div", { className: "bubble-text cursor", dir: "auto", children: active.draft }) })] })), last && !busy && (_jsxs("div", { className: `reply-card stack-sm ${last.success ? "" : "msg-error"}`, children: [last.steps?.length > 0 && _jsx(StepList, { steps: last.steps }), _jsx("div", { className: "bubble", children: _jsx("div", { className: "bubble-text", dir: "auto", children: last.reply }) }), _jsx("button", { className: "link small", style: { alignSelf: "flex-start" }, onClick: () => navigate("assistant"), children: tr("Continue in Assistant →") })] }))] }));
}
