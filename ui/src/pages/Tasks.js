import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useState } from "react";
import { Bell, Plus, Trash2, X } from "lucide-react";
import { del, get, post, put } from "../api";
import { useEvents } from "../events";
import { Badge, Card, Empty, ErrorNote, formatTime, timeAgo, useLoad } from "../components/ui";
import { tr } from "../lib/i18n";
const STATES = ["pending", "in_progress", "waiting", "blocked", "completed", "cancelled"];
const PRIORITIES = ["low", "normal", "high", "urgent"];
export function Tasks() {
    const [showClosed, setShowClosed] = useState(false);
    const tasks = useLoad(() => get(`/tasks?all=${showClosed}`), [showClosed]);
    const reminders = useLoad(() => get("/reminders"));
    const [title, setTitle] = useState("");
    const [priority, setPriority] = useState("normal");
    const [due, setDue] = useState("");
    const [repeat, setRepeat] = useState("");
    const [error, setError] = useState(null);
    useEvents(["tasks"], () => void tasks.reload());
    useEvents(["reminders"], () => void reminders.reload());
    const add = async () => {
        if (!title.trim())
            return;
        try {
            await post("/tasks", { title, priority, dueAt: due ? new Date(due).toISOString() : null, recurrence: repeat || null });
            setTitle("");
            setDue("");
            setRepeat("");
            setError(null);
        }
        catch (e) {
            setError(e);
        }
    };
    const update = (t, patch) => put(`/tasks/${t.id}`, patch).catch(setError);
    const grouped = STATES.map((s) => ({ state: s, items: (tasks.data ?? []).filter((t) => t.state === s) })).filter((g) => g.items.length > 0);
    return (_jsxs("div", { className: "stack", children: [_jsx("h2", { children: tr("Tasks") }), _jsxs(Card, { children: [_jsxs("form", { className: "row wrap", onSubmit: (e) => { e.preventDefault(); void add(); }, children: [_jsx("input", { className: "input grow", dir: "auto", placeholder: "New task\u2026", value: title, onChange: (e) => setTitle(e.target.value) }), _jsx("select", { className: "input", value: priority, onChange: (e) => setPriority(e.target.value), "aria-label": "Priority", children: PRIORITIES.map((p) => _jsx("option", { children: p }, p)) }), _jsx("input", { className: "input", type: "datetime-local", value: due, onChange: (e) => setDue(e.target.value), "aria-label": "Due" }), _jsxs("select", { className: "input", value: repeat, onChange: (e) => setRepeat(e.target.value), "aria-label": "Repeat", children: [_jsx("option", { value: "", children: "Doesn't repeat" }), _jsx("option", { value: "daily", children: "Every day" }), _jsx("option", { value: "weekdays", children: "Every working day" }), _jsx("option", { value: "weekly", children: "Every week" }), _jsx("option", { value: "monthly", children: "Every month" })] }), _jsxs("button", { className: "btn btn-primary", type: "submit", disabled: !title.trim(), children: [_jsx(Plus, { size: 16 }), " Add"] })] }), _jsx(ErrorNote, { error: error ?? tasks.error })] }), _jsx("div", { className: "row", children: _jsxs("label", { className: "row small", children: [_jsx("input", { type: "checkbox", checked: showClosed, onChange: (e) => setShowClosed(e.target.checked) }), " Show completed and cancelled"] }) }), grouped.length === 0 && _jsx(Empty, { children: "No tasks. Add one above, or say \u201Cadd task \u2026\u201D / \u201C\u0636\u064A\u0641 \u0645\u0647\u0645\u0629 \u2026\u201D." }), grouped.map((g) => (_jsx(Card, { title: `${g.state.replace("_", " ")} (${g.items.length})`, children: _jsx("ul", { className: "list list-rows", children: g.items.map((t) => (_jsxs("li", { children: [_jsx("input", { type: "checkbox", "aria-label": "Done", checked: t.state === "completed", onChange: (e) => update(t, { state: e.target.checked ? "completed" : "pending" }) }), _jsxs("div", { className: "grow", children: [_jsx("div", { dir: "auto", className: t.state === "completed" ? "done" : "", children: t.title }), _jsxs("div", { className: "muted small", children: [t.dueAt ? `Due ${formatTime(t.dueAt)} · ` : "", "created ", timeAgo(t.createdAt), t.project ? ` · ${t.project}` : "", t.recurrence ? ` · repeats ${t.recurrence}` : ""] })] }), _jsx(Badge, { tone: t.priority === "urgent" ? "bad" : t.priority === "high" ? "warn" : "neutral", children: t.priority }), _jsx("select", { className: "input input-sm", value: t.state, onChange: (e) => update(t, { state: e.target.value }), "aria-label": "State", children: STATES.map((s) => _jsx("option", { value: s, children: s.replace("_", " ") }, s)) }), _jsx("button", { className: "btn btn-ghost", onClick: () => del(`/tasks/${t.id}`), "aria-label": "Delete task", children: _jsx(Trash2, { size: 16 }) })] }, t.id))) }) }, g.state))), _jsx(Reminders, { reminders: reminders.data ?? [] })] }));
}
function Reminders({ reminders }) {
    const [text, setText] = useState("");
    const [minutes, setMinutes] = useState(30);
    const add = async () => {
        if (!text.trim())
            return;
        await post("/reminders", { text, inMinutes: minutes });
        setText("");
    };
    return (_jsxs(Card, { title: _jsxs("span", { className: "row", children: [_jsx(Bell, { size: 16 }), " Reminders"] }), children: [_jsxs("form", { className: "row wrap", onSubmit: (e) => { e.preventDefault(); void add(); }, children: [_jsx("input", { className: "input grow", dir: "auto", placeholder: "Remind me to\u2026", value: text, onChange: (e) => setText(e.target.value) }), _jsx("span", { className: "muted", children: "in" }), _jsx("input", { className: "input input-sm", type: "number", min: 1, value: minutes, onChange: (e) => setMinutes(Number(e.target.value)), "aria-label": "Minutes" }), _jsx("span", { className: "muted", children: "min" }), _jsxs("button", { className: "btn", type: "submit", disabled: !text.trim(), children: [_jsx(Plus, { size: 16 }), " Remind me"] })] }), reminders.length === 0 ? (_jsx(Empty, { children: "No upcoming reminders." })) : (_jsx("ul", { className: "list list-rows", children: reminders.map((r) => (_jsxs("li", { children: [_jsx("div", { className: "grow", dir: "auto", children: r.text }), _jsxs("span", { className: "muted small nowrap", dir: "ltr", children: [formatTime(r.dueAt), " (", timeAgo(r.dueAt), ")"] }), _jsx("button", { className: "btn btn-ghost", onClick: () => del(`/reminders/${r.id}`), "aria-label": "Cancel reminder", children: _jsx(X, { size: 16 }) })] }, r.id))) }))] }));
}
