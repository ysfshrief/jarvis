import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from "react";
import { ArrowLeft, Ban, Check, CheckCircle2, Circle, CircleDot, Clock, Hourglass, Link2, Play, Plus, Repeat, ShieldCheck, SkipForward, Trash2, Undo2, XCircle } from "lucide-react";
import { del, get, post, put } from "../api";
import { useEvents } from "../events";
import { Badge, Card, ConfirmButton, Empty, ErrorNote, Field, formatTime, Meter, PageHead, timeAgo, useLoad } from "../components/ui";
import { tr } from "../lib/i18n";
const REPEATS = [["", "Doesn't repeat"], ["daily", "Every day"], ["weekdays", "Every working day"], ["weekly", "Every week"], ["monthly", "Every month"]];
/** Long-running goals JARVIS tracks: steps, who you're waiting on, deadlines and history. */
export function WorkflowsPage() {
    const [openId, setOpenId] = useState(() => location.hash.split("/")[2] ?? null);
    const [showClosed, setShowClosed] = useState(false);
    const list = useLoad(() => get(`/workflows?all=${showClosed}`), [showClosed]);
    const [creating, setCreating] = useState(false);
    const [error, setError] = useState(null);
    useEvents(["workflows"], () => void list.reload());
    const open = (id) => { setOpenId(id); history.replaceState(null, "", id ? `#/workflows/${id}` : "#/workflows"); };
    if (openId)
        return _jsx(Detail, { id: openId, onBack: () => open(null) });
    return (_jsxs("div", { className: "stack", children: [_jsx(PageHead, { title: tr("Workflows"), sub: "Deals, projects and follow-ups JARVIS keeps track of \u2014 say \u201Ctrack the CityCrep deal\u201D or start one here.", actions: _jsxs("button", { className: "btn btn-primary btn-sm", onClick: () => setCreating((c) => !c), children: [_jsx(Plus, { size: 14 }), " New workflow"] }) }), _jsx(ErrorNote, { error: error ?? list.error }), creating && _jsx(Create, { onDone: (id) => { setCreating(false); open(id); }, onError: setError }), _jsxs("label", { className: "row small", children: [_jsx("input", { type: "checkbox", checked: showClosed, onChange: (e) => setShowClosed(e.target.checked) }), " Show completed and cancelled"] }), list.data?.length === 0 && !creating && _jsx(Empty, { children: "Nothing tracked yet. Try \u201Ctrack the CityCrep deal\u201D or \u201C\u062A\u0627\u0628\u0639 \u0635\u0641\u0642\u0629 \u0633\u064A\u062A\u064A \u0643\u0631\u064A\u0628\u201D." }), _jsx("div", { className: "grid-3", children: list.data?.map((w) => _jsx(WorkflowCard, { w: w, onOpen: () => open(w.id) }, w.id)) })] }));
}
function statusTone(s) {
    return s === "completed" ? "good" : s === "waiting" ? "info" : s === "blocked" ? "bad" : s === "cancelled" ? "neutral" : "warn";
}
function WorkflowCard({ w, onOpen }) {
    const waiting = w.steps.filter((s) => s.ready && s.status === "waiting");
    const overdue = w.steps.filter((s) => !["done", "skipped"].includes(s.status) && s.dueAt && new Date(s.dueAt) < new Date()).length;
    return (_jsx(Card, { title: _jsx("span", { dir: "auto", children: w.title }), actions: _jsx(Badge, { tone: statusTone(w.status), children: w.status }), className: "clickable-card", children: _jsxs("button", { className: "link", style: { all: "unset", cursor: "pointer", display: "flex", flexDirection: "column", gap: 8 }, onClick: onOpen, "aria-label": `Open ${w.title}`, children: [_jsxs("div", { className: "row between small", children: [_jsxs("span", { className: "muted", children: [w.doneCount, "/", w.steps.length, " steps"] }), w.entityName && _jsxs("span", { className: "row small", children: [_jsx(Link2, { size: 12 }), " ", w.entityName] })] }), _jsx(Meter, { value: w.progress * 100 }), waiting.length > 0 ? (_jsxs("div", { className: "small", children: [_jsx(Hourglass, { size: 12 }), " Waiting on ", waiting.map((s) => s.waitingFor ?? s.title).join(", ")] })) : w.next ? (_jsxs("div", { className: "small", children: [_jsx(CircleDot, { size: 12 }), " Next: ", _jsx("span", { dir: "auto", children: w.next.title })] })) : null, _jsxs("div", { className: "row small wrap", children: [w.dueAt && _jsxs("span", { className: "meta", dir: "ltr", children: ["due ", formatTime(w.dueAt)] }), overdue > 0 && _jsxs(Badge, { tone: "bad", children: [overdue, " overdue"] }), w.recurrence && _jsxs(Badge, { tone: "info", children: [_jsx(Repeat, { size: 10 }), " ", w.recurrence] }), _jsxs("span", { className: "meta", children: ["updated ", timeAgo(w.updatedAt)] })] })] }) }));
}
function Create({ onDone, onError }) {
    const templates = useLoad(() => get("/workflows/templates"));
    const [title, setTitle] = useState("");
    const [template, setTemplate] = useState("deal");
    const [about, setAbout] = useState("");
    const [due, setDue] = useState("");
    const [repeat, setRepeat] = useState("");
    const [steps, setSteps] = useState("");
    const tpl = templates.data?.find((t) => t.id === template);
    const submit = async () => {
        try {
            const custom = template === "custom" ? steps.split("\n").map((s) => s.trim()).filter(Boolean).map((t) => ({ title: t })) : undefined;
            const wf = await post("/workflows", { title, template, about: about || null, due: due ? new Date(due).toISOString() : null, repeat: repeat || null, steps: custom });
            onDone(wf.id);
        }
        catch (e) {
            onError(e);
        }
    };
    return (_jsxs(Card, { title: "Start tracking", children: [_jsxs("div", { className: "form-grid", children: [_jsx(Field, { label: "Name", children: _jsx("input", { className: "input", dir: "auto", placeholder: "e.g. CityCrep deal", value: title, onChange: (e) => setTitle(e.target.value) }) }), _jsx(Field, { label: "Template", hint: tpl?.description, children: _jsx("select", { className: "input", value: template, onChange: (e) => setTemplate(e.target.value), children: templates.data?.map((t) => _jsx("option", { value: t.id, children: t.name }, t.id)) }) }), _jsx(Field, { label: "About (client, project, person)", children: _jsx("input", { className: "input", dir: "auto", placeholder: "e.g. CityCrep", value: about, onChange: (e) => setAbout(e.target.value) }) }), _jsx(Field, { label: "Deadline", hint: "Step deadlines are spread up to it.", children: _jsx("input", { className: "input", type: "datetime-local", value: due, onChange: (e) => setDue(e.target.value) }) }), _jsx(Field, { label: "Repeat", children: _jsx("select", { className: "input", value: repeat, onChange: (e) => setRepeat(e.target.value), children: REPEATS.map(([v, l]) => _jsx("option", { value: v, children: l }, v)) }) })] }), template === "custom" ? (_jsx(Field, { label: "Steps (one per line, in order)", children: _jsx("textarea", { className: "input", dir: "auto", rows: 5, value: steps, onChange: (e) => setSteps(e.target.value) }) })) : tpl && (_jsx("ol", { className: "steps small muted", children: tpl.steps.map((s) => _jsx("li", { children: s }, s)) })), _jsx("div", { className: "row", children: _jsxs("button", { className: "btn btn-primary", disabled: !title.trim() || (template === "custom" && !steps.trim()), onClick: submit, children: [_jsx(Play, { size: 14 }), " Start"] }) })] }));
}
function Detail({ id, onBack }) {
    const data = useLoad(() => get(`/workflows/${id}`), [id]);
    const [error, setError] = useState(null);
    const [note, setNote] = useState("");
    const [newStep, setNewStep] = useState("");
    useEvents(["workflows"], () => void data.reload());
    if (!data.data)
        return _jsxs("div", { className: "stack", children: [_jsxs("button", { className: "btn btn-ghost btn-sm", onClick: onBack, children: [_jsx(ArrowLeft, { size: 14 }), " All workflows"] }), _jsx(ErrorNote, { error: data.error })] });
    const { workflow: w, history } = data.data;
    const act = (p) => p.catch(setError);
    return (_jsxs("div", { className: "stack", children: [_jsxs("button", { className: "btn btn-ghost btn-sm", style: { alignSelf: "flex-start" }, onClick: onBack, children: [_jsx(ArrowLeft, { size: 14 }), " All workflows"] }), _jsx(PageHead, { title: w.title, sub: _jsxs("span", { children: [w.goal, w.entityName && _jsxs(_Fragment, { children: [" \u00B7 ", _jsx(Link2, { size: 12 }), " ", w.entityName] }), w.dueAt && _jsxs(_Fragment, { children: [" \u00B7 due ", formatTime(w.dueAt)] })] }), actions: _jsxs(_Fragment, { children: [_jsx(Badge, { tone: statusTone(w.status), children: w.status }), w.isOpen && _jsxs(ConfirmButton, { className: "btn btn-ghost btn-sm", prompt: `Stop tracking ${w.title}?`, onConfirm: () => act(post(`/workflows/${id}/cancel`)), children: [_jsx(Ban, { size: 13 }), " Stop tracking"] }), _jsx(ConfirmButton, { className: "btn btn-danger btn-sm", prompt: `Delete ${w.title} and its history?`, onConfirm: () => del(`/workflows/${id}`).then(onBack).catch(setError), children: _jsx(Trash2, { size: 13 }) })] }) }), _jsx(ErrorNote, { error: error }), _jsxs("div", { className: "row", children: [_jsx("div", { className: "grow", children: _jsx(Meter, { value: w.progress * 100 }) }), _jsxs("span", { className: "meta nowrap", children: [w.doneCount, "/", w.steps.length] })] }), _jsxs("div", { className: "grid-2", style: { alignItems: "start" }, children: [_jsxs(Card, { title: "Steps", children: [_jsx("ol", { className: "wf-steps", children: w.steps.map((s, i) => _jsx(StepRow, { s: s, index: i, wf: w, onError: setError }, s.id)) }), w.isOpen && (_jsxs("form", { className: "row", onSubmit: (e) => { e.preventDefault(); act(post(`/workflows/${id}/steps`, { title: newStep }).then(() => setNewStep(""))); }, children: [_jsx("input", { className: "input grow", dir: "auto", placeholder: "Add a step\u2026", value: newStep, onChange: (e) => setNewStep(e.target.value) }), _jsxs("button", { className: "btn btn-sm", disabled: !newStep.trim(), children: [_jsx(Plus, { size: 13 }), " Add"] })] }))] }), _jsxs(Card, { title: "History", children: [_jsxs("form", { className: "row", onSubmit: (e) => { e.preventDefault(); act(post(`/workflows/${id}/notes`, { text: note }).then(() => setNote(""))); }, children: [_jsx("input", { className: "input grow", dir: "auto", placeholder: "Add a note (e.g. \u201CAhmed asked for a 10% discount\u201D)", value: note, onChange: (e) => setNote(e.target.value) }), _jsx("button", { className: "btn btn-sm", disabled: !note.trim(), children: "Note" })] }), _jsx("ul", { className: "list", children: history.map((h) => (_jsxs("li", { style: { alignItems: "flex-start" }, children: [_jsx("span", { className: "small grow", dir: "auto", children: h.text }), _jsx("span", { className: "meta nowrap", title: new Date(h.timestamp).toLocaleString(), children: timeAgo(h.timestamp) })] }, h.id))) })] })] })] }));
}
function StepRow({ s, index, wf, onError }) {
    const [waiting, setWaiting] = useState(null);
    const [msg, setMsg] = useState(null);
    const update = (body) => put(`/workflows/${wf.id}/steps/${s.id}`, body).catch(onError);
    const closed = s.status === "done" || s.status === "skipped";
    const blockedBy = s.dependsOn.map((d) => wf.steps.find((x) => x.id === d)).filter((x) => x && !["done", "skipped"].includes(x.status));
    return (_jsxs("li", { className: `wf-step wf-${s.status} ${s.ready ? "wf-ready" : ""}`, children: [_jsx(StepIcon, { s: s }), _jsxs("div", { className: "grow stack-sm", children: [_jsxs("div", { className: closed ? "done" : "", dir: "auto", children: [_jsxs("span", { className: "meta", children: [index + 1, "."] }), " ", s.title] }), _jsxs("div", { className: "row small wrap", children: [s.requiresApproval && !closed && _jsxs(Badge, { tone: "warn", children: [_jsx(ShieldCheck, { size: 10 }), " needs your OK"] }), s.status === "waiting" && _jsxs(Badge, { tone: "info", children: [_jsx(Hourglass, { size: 10 }), " waiting for ", s.waitingFor ?? "a reply"] }), s.followUpAt && !closed && _jsxs("span", { className: "meta", children: ["follow up ", formatTime(s.followUpAt)] }), s.dueAt && !closed && _jsxs("span", { className: `meta ${new Date(s.dueAt) < new Date() ? "accent" : ""}`, dir: "ltr", children: ["due ", formatTime(s.dueAt)] }), !s.ready && !closed && blockedBy.length > 0 && _jsxs("span", { className: "meta", children: ["after \u201C", blockedBy[0].title, "\u201D"] }), s.completedAt && _jsxs("span", { className: "meta", children: ["done ", timeAgo(s.completedAt)] }), s.action && _jsxs(Badge, { tone: "accent", children: ["runs ", s.action.tool] })] }), msg && _jsx("div", { className: "small muted", children: msg }), waiting && (_jsxs("form", { className: "row wrap", onSubmit: (e) => { e.preventDefault(); void update({ status: "waiting", waitingFor: waiting.who || null, followUpDays: waiting.days }); setWaiting(null); }, children: [_jsx("input", { className: "input input-sm grow", dir: "auto", placeholder: "Waiting for whom?", value: waiting.who, onChange: (e) => setWaiting({ ...waiting, who: e.target.value }) }), _jsx("span", { className: "small muted", children: "follow up in" }), _jsx("input", { className: "input input-sm", type: "number", min: 1, max: 60, style: { width: 64 }, value: waiting.days, onChange: (e) => setWaiting({ ...waiting, days: Number(e.target.value) }) }), _jsx("span", { className: "small muted", children: "days" }), _jsx("button", { className: "btn btn-sm", children: "Set" }), _jsx("button", { type: "button", className: "btn btn-ghost btn-sm", onClick: () => setWaiting(null), children: "Cancel" })] }))] }), wf.isOpen && (_jsxs("div", { className: "row", children: [!closed && s.action && s.ready && (_jsxs("button", { className: "btn btn-sm", title: "Run this step's action (asks first if it's a business action)", onClick: async () => { const r = await post(`/workflows/${wf.id}/steps/${s.id}/run`).catch(onError); if (r)
                            setMsg(r.message); }, children: [_jsx(Play, { size: 13 }), " Run"] })), !closed && _jsx("button", { className: "btn btn-sm", title: "Done", onClick: () => update({ status: "done" }), children: _jsx(Check, { size: 13 }) }), !closed && s.status !== "waiting" && _jsx("button", { className: "btn btn-ghost btn-icon", title: "Waiting on someone", onClick: () => setWaiting({ who: s.waitingFor ?? "", days: 3 }), children: _jsx(Hourglass, { size: 14 }) }), !closed && _jsx("button", { className: "btn btn-ghost btn-icon", title: "Skip", onClick: () => update({ status: "skipped" }), children: _jsx(SkipForward, { size: 14 }) }), closed && _jsx("button", { className: "btn btn-ghost btn-icon", title: "Reopen", onClick: () => update({ status: "pending", clearFollowUp: true }), children: _jsx(Undo2, { size: 14 }) })] }))] }));
}
function StepIcon({ s }) {
    const size = 16;
    switch (s.status) {
        case "done": return _jsx(CheckCircle2, { size: size, color: "var(--good)" });
        case "skipped": return _jsx(SkipForward, { size: size, className: "dim" });
        case "waiting": return _jsx(Clock, { size: size, color: "var(--info)" });
        case "blocked": return _jsx(XCircle, { size: size, color: "var(--bad)" });
        case "in_progress": return _jsx(CircleDot, { size: size, color: "var(--accent)" });
        default: return s.ready ? _jsx(CircleDot, { size: size, color: "var(--accent)" }) : _jsx(Circle, { size: size, className: "dim" });
    }
}
