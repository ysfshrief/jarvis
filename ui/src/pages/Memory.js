import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from "react";
import { Brain, Check, ChevronDown, ChevronRight, Link2, Pencil, Plus, RefreshCw, Save, Search, Sparkles, Trash2, Users, X } from "lucide-react";
import { del, get, post, put } from "../api";
import { useEvents } from "../events";
import { Badge, Card, ConfirmButton, Empty, ErrorNote, formatTime, PageHead, Segmented, timeAgo, useLoad } from "../components/ui";
import { tr } from "../lib/i18n";
const KINDS = ["fact", "preference", "person", "project", "context", "pattern", "assumption"];
const ENTITY_TYPES = ["person", "organization", "project", "place", "file", "event", "topic"];
const SOURCE_LABEL = {
    user: "You told me",
    confirmed: "You confirmed",
    learned: "Learned — unconfirmed",
    derived: "Inferred — unconfirmed",
};
/**
 * What JARVIS knows, where each piece came from, and what it only suspects. Inferred items never
 * become facts until you confirm them here.
 */
export function MemoryPage() {
    const [tab, setTab] = useState("memories");
    const [openEntity, setOpenEntity] = useState(null);
    const status = useLoad(() => get("/memory/status"));
    const [error, setError] = useState(null);
    const [learnMsg, setLearnMsg] = useState(null);
    useEvents(["memory"], () => void status.reload());
    const showEntity = (id) => { setOpenEntity(id); setTab("people"); };
    const s = status.data;
    const analyze = async () => {
        try {
            const r = await post("/memory/learn");
            setLearnMsg(r.proposed + r.updated === 0 ? "No new patterns in the last two weeks." : `${r.proposed} new and ${r.updated} updated pattern(s) to review.`);
            if (r.proposed)
                setTab("review");
        }
        catch (e) {
            setError(e);
        }
    };
    return (_jsxs("div", { className: "stack", children: [_jsx(PageHead, { title: tr("Memory"), sub: "Everything lives only on this computer. Each item shows where it came from; guesses stay separate from facts until you confirm them.", actions: _jsxs(_Fragment, { children: [_jsxs("button", { className: "btn btn-ghost btn-sm", onClick: analyze, title: "Look for patterns in the last two weeks of activity", children: [_jsx(Sparkles, { size: 14 }), " Analyze my activity"] }), _jsxs(ConfirmButton, { className: "btn btn-danger btn-sm", prompt: "Delete ALL memories? This cannot be undone.", onConfirm: () => del("/memory?confirm=true").catch(setError), children: [_jsx(Trash2, { size: 14 }), " Clear all"] })] }) }), _jsx(ErrorNote, { error: error }), learnMsg && _jsx("div", { className: "note", children: learnMsg }), _jsxs("div", { className: "grid-4", children: [_jsx(Stat, { label: "Confirmed facts", value: s?.confirmed }), _jsx(Stat, { label: "To review", value: s?.inferred, tone: s && s.inferred > 0 ? "warn" : undefined }), _jsx(Stat, { label: "People & things", value: s?.entities }), _jsxs(Card, { className: "metric-card", title: "Semantic search", children: [_jsxs("div", { className: "row", children: [_jsx("span", { className: `dot ${s?.semantic.available ? "dot-ok" : "dot-unknown"}` }), _jsx("strong", { className: "small", children: s?.semantic.available ? s.semantic.model : "Keyword only" }), s?.semantic.available && (_jsx("button", { className: "btn btn-ghost btn-icon", title: "Re-index now", "aria-label": "Re-index", onClick: () => post("/memory/reindex").then(() => status.reload()).catch(setError), children: _jsx(RefreshCw, { size: 13 }) }))] }), _jsx("div", { className: "meta", children: s?.semantic.message }), !s?.semantic.available && _jsx("a", { className: "link small", href: "#/settings/ai", children: "Get the free model \u2192" })] })] }), _jsx(Segmented, { label: "View", value: tab, onChange: setTab, options: [["memories", "Memories"], ["review", `To review${s?.inferred ? ` (${s.inferred})` : ""}`], ["people", "People & things"]] }), tab === "memories" && _jsx(Memories, { onEntity: showEntity, onError: setError }), tab === "review" && _jsx(Review, { learning: !!s?.learning, onEntity: showEntity, onError: setError }), tab === "people" && _jsx(People, { open: openEntity, setOpen: setOpenEntity, onError: setError })] }));
}
function Stat({ label, value, tone }) {
    return (_jsx(Card, { className: "metric-card", title: label, children: _jsx("div", { className: "metric-value", style: tone === "warn" ? { color: "var(--warn)" } : undefined, children: value ?? "—" }) }));
}
function Memories({ onEntity, onError }) {
    const [q, setQ] = useState("");
    const [kind, setKind] = useState("");
    const [query, setQuery] = useState("");
    useEffect(() => {
        const t = window.setTimeout(() => setQuery(q), 250);
        return () => window.clearTimeout(t);
    }, [q]);
    const path = `/memory?${new URLSearchParams({ ...(query ? { q: query } : {}), ...(kind ? { kind } : {}) })}`;
    const items = useLoad(() => get(path), [path]);
    useEvents(["memory"], () => void items.reload());
    return (_jsxs(_Fragment, { children: [_jsx(AddMemory, { onError: onError }), _jsxs(Card, { children: [_jsxs("div", { className: "row wrap", children: [_jsxs("div", { className: "input-icon grow", children: [_jsx(Search, { size: 16 }), _jsx("input", { className: "input", dir: "auto", placeholder: "Search by words or meaning\u2026", value: q, onChange: (e) => setQ(e.target.value) })] }), _jsxs("select", { className: "input", value: kind, onChange: (e) => setKind(e.target.value), "aria-label": "Kind", children: [_jsx("option", { value: "", children: "All kinds" }), KINDS.map((k) => _jsx("option", { children: k }, k))] })] }), _jsx(ErrorNote, { error: items.error }), items.data && items.data.length === 0 && _jsx(Empty, { children: q ? "Nothing matches." : "No memories yet. Say “remember that …” / “افتكر إن …”." }), _jsx("ul", { className: "list list-rows", children: items.data?.map((m) => _jsx(MemoryRow, { m: m, onEntity: onEntity, onError: onError }, m.id)) })] })] }));
}
function Review({ learning, onEntity, onError }) {
    const items = useLoad(() => get("/memory?confirmed=false&limit=200"));
    useEvents(["memory"], () => void items.reload());
    return (_jsxs(Card, { title: "Things JARVIS suspects but hasn't confirmed", children: [_jsx("p", { className: "small muted", children: "Confirm what's right (it becomes a fact JARVIS can rely on) and reject what's wrong (it's deleted and never suggested again). Until then these are only hints, labelled as such when the AI sees them." }), !learning && (_jsxs("div", { className: "note", children: ["Learning from your activity is off. Turn it on in ", _jsx("a", { href: "#/settings/memory", children: "Settings \u2192 Memory" }), " to let JARVIS notice routines (apps you open at certain times, your preferred language, how long you like answers). It only reads JARVIS's own activity log."] })), items.data?.length === 0 && _jsx(Empty, { children: "Nothing to review." }), _jsx("ul", { className: "list list-rows", children: items.data?.map((m) => _jsx(MemoryRow, { m: m, onEntity: onEntity, onError: onError, review: true }, m.id)) })] }));
}
function AddMemory({ onError }) {
    const [content, setContent] = useState("");
    const [kind, setKind] = useState("fact");
    const [subject, setSubject] = useState("");
    const add = async () => {
        try {
            await post("/memory", { content, kind, subject: subject || null });
            setContent("");
            setSubject("");
        }
        catch (e) {
            onError(e);
        }
    };
    return (_jsx(Card, { title: "Teach JARVIS something", children: _jsxs("form", { className: "row wrap", onSubmit: (e) => { e.preventDefault(); void add(); }, children: [_jsx("input", { className: "input grow", dir: "auto", placeholder: "e.g. Ahmed is the CityCrep account manager", value: content, onChange: (e) => setContent(e.target.value) }), _jsx("input", { className: "input", dir: "auto", placeholder: "About (optional)", value: subject, onChange: (e) => setSubject(e.target.value) }), _jsx("select", { className: "input", value: kind, onChange: (e) => setKind(e.target.value), "aria-label": "Kind", children: KINDS.filter((k) => k !== "assumption").map((k) => _jsx("option", { children: k }, k)) }), _jsxs("button", { className: "btn btn-primary", disabled: !content.trim(), type: "submit", children: [_jsx(Plus, { size: 15 }), " Remember"] })] }) }));
}
function MemoryRow({ m, onEntity, onError, review }) {
    const [editing, setEditing] = useState(false);
    const [why, setWhy] = useState(!!review);
    const [content, setContent] = useState(m.content);
    const [kind, setKind] = useState(m.kind);
    const save = async () => {
        try {
            await put(`/memory/${m.id}`, { content, kind });
            setEditing(false);
        }
        catch (e) {
            onError(e);
        }
    };
    const p = m.provenance;
    return (_jsxs("li", { style: { alignItems: "flex-start" }, children: [_jsxs("div", { className: "grow stack-sm", children: [editing ? (_jsxs("div", { className: "row wrap", children: [_jsx("input", { className: "input grow", dir: "auto", value: content, onChange: (e) => setContent(e.target.value) }), _jsx("select", { className: "input", value: kind, onChange: (e) => setKind(e.target.value), children: KINDS.map((k) => _jsx("option", { children: k }, k)) })] })) : (_jsxs("div", { dir: "auto", children: [m.subject && _jsxs("strong", { children: [m.subject, ": "] }), m.content] })), _jsxs("div", { className: "row small wrap", children: [_jsx(Badge, { tone: "accent", children: m.kind }), _jsxs(Badge, { tone: m.isConfirmed ? "good" : "warn", title: `Confidence ${Math.round(m.confidence * 100)}%`, children: [SOURCE_LABEL[m.source] ?? m.source, " \u00B7 ", Math.round(m.confidence * 100), "%"] }), m.semantic && _jsx(Badge, { tone: "info", title: "Found by meaning, not just matching words", children: "semantic" }), m.entities.map((e) => (_jsxs("button", { className: "link small", onClick: () => onEntity(e.id), title: e.type, children: [_jsx(Link2, { size: 11 }), " ", e.name] }, e.id))), _jsxs("span", { className: "meta", children: ["updated ", timeAgo(m.updatedAt), m.useCount > 0 ? ` · used ${m.useCount}×` : ""] }), _jsxs("button", { className: "link small", onClick: () => setWhy((w) => !w), children: [why ? _jsx(ChevronDown, { size: 12 }) : _jsx(ChevronRight, { size: 12 }), " Why is this here?"] })] }), why && (_jsxs("div", { className: "note small", children: [_jsxs("div", { children: [_jsx("strong", { children: p ? viaLabel(p.via) : "Stored before provenance was tracked." }), " · ", formatTime(m.createdAt), m.confirmedAt && _jsxs(_Fragment, { children: [" \u00B7 confirmed ", timeAgo(m.confirmedAt)] })] }), p?.quote && _jsxs("div", { dir: "auto", children: ["You said: \u201C", p.quote, "\u201D"] }), p?.reason && _jsxs("div", { dir: "auto", children: ["Evidence: ", p.reason] }), p?.tool && _jsxs("div", { className: "meta", children: ["via tool ", p.tool, p.conversationId ? ` · conversation ${p.conversationId}` : ""] })] }))] }), !m.isConfirmed && !editing && (_jsxs(_Fragment, { children: [_jsxs("button", { className: "btn btn-sm", onClick: () => post(`/memory/${m.id}/confirm`).catch(onError), title: "This is right \u2014 keep it as a fact", children: [_jsx(Check, { size: 14 }), " Confirm"] }), _jsxs("button", { className: "btn btn-ghost btn-sm", onClick: () => post(`/memory/${m.id}/reject`).catch(onError), title: "This is wrong \u2014 delete it and don't suggest it again", children: [_jsx(X, { size: 14 }), " Reject"] })] })), editing ? (_jsxs(_Fragment, { children: [_jsx("button", { className: "btn btn-primary btn-icon", onClick: save, "aria-label": "Save", children: _jsx(Save, { size: 15 }) }), _jsx("button", { className: "btn btn-ghost btn-icon", onClick: () => setEditing(false), "aria-label": "Cancel", children: _jsx(X, { size: 15 }) })] })) : (_jsxs(_Fragment, { children: [_jsx("button", { className: "btn btn-ghost btn-icon", onClick: () => setEditing(true), "aria-label": "Edit", children: _jsx(Pencil, { size: 15 }) }), _jsx("button", { className: "btn btn-ghost btn-icon", onClick: () => del(`/memory/${m.id}`).catch(onError), "aria-label": "Delete", children: _jsx(Trash2, { size: 15 }) })] }))] }));
}
function viaLabel(via) {
    switch (via) {
        case "text":
        case "chat": return "You told me in a conversation";
        case "voice": return "You told me by voice";
        case "dashboard": return "Added in the dashboard";
        case "learner": return "Noticed in your activity (pattern learning)";
        default: return `Added via ${via}`;
    }
}
function People({ open, setOpen, onError }) {
    const [type, setType] = useState("");
    const [q, setQ] = useState("");
    const list = useLoad(() => get(`/entities?${new URLSearchParams({ ...(type ? { type } : {}), ...(q ? { q } : {}) })}`), [type, q]);
    useEvents(["memory"], () => void list.reload());
    const [name, setName] = useState("");
    const [newType, setNewType] = useState("person");
    return (_jsxs("div", { className: "grid-2", style: { alignItems: "start" }, children: [_jsxs(Card, { title: "People & things", children: [_jsxs("div", { className: "row wrap", children: [_jsxs("div", { className: "input-icon grow", children: [_jsx(Search, { size: 15 }), _jsx("input", { className: "input", dir: "auto", placeholder: "Find\u2026", value: q, onChange: (e) => setQ(e.target.value) })] }), _jsxs("select", { className: "input", value: type, onChange: (e) => setType(e.target.value), "aria-label": "Type", children: [_jsx("option", { value: "", children: "All types" }), ENTITY_TYPES.map((t) => _jsx("option", { children: t }, t))] })] }), list.data?.length === 0 && _jsx(Empty, { children: "Nobody yet. Say \u201CAhmed works at CityCrep\u201D or add one below." }), _jsx("ul", { className: "list", children: list.data?.map((e) => (_jsxs("li", { children: [_jsxs("button", { className: "link grow", style: { textAlign: "start", color: open === e.id ? "var(--accent)" : "var(--text)" }, onClick: () => setOpen(e.id), dir: "auto", children: [e.type === "person" ? _jsx(Users, { size: 13 }) : _jsx(Brain, { size: 13 }), " ", e.name] }), _jsx(Badge, { children: e.type }), _jsx("span", { className: "meta", children: e.memories ?? 0 })] }, e.id))) }), _jsxs("form", { className: "row", onSubmit: async (ev) => { ev.preventDefault(); try {
                            const e = await post("/entities", { name, type: newType });
                            setName("");
                            setOpen(e.id);
                        }
                        catch (err) {
                            onError(err);
                        } }, children: [_jsx("input", { className: "input grow", dir: "auto", placeholder: "Add a person, company, project\u2026", value: name, onChange: (e) => setName(e.target.value) }), _jsx("select", { className: "input", value: newType, onChange: (e) => setNewType(e.target.value), "aria-label": "Type", children: ENTITY_TYPES.map((t) => _jsx("option", { children: t }, t)) }), _jsxs("button", { className: "btn btn-sm", disabled: !name.trim(), children: [_jsx(Plus, { size: 13 }), " Add"] })] })] }), open ? _jsx(Profile, { id: open, onClose: () => setOpen(null), onError: onError }) : _jsx(Card, { children: _jsx(Empty, { children: "Select someone or something to see everything connected to it." }) })] }));
}
function Profile({ id, onClose, onError }) {
    const p = useLoad(() => get(`/entities/${id}`), [id]);
    useEvents(["memory", "tasks"], () => void p.reload());
    const [rel, setRel] = useState({ relation: "works_at", to: "", toType: "organization" });
    const [aliases, setAliases] = useState(null);
    if (!p.data)
        return _jsx(Card, { children: _jsx(ErrorNote, { error: p.error }) });
    const { entity, memories, relations, tasks, reminders } = p.data;
    return (_jsxs(Card, { title: _jsx("span", { dir: "auto", children: entity.name }), actions: _jsxs(_Fragment, { children: [_jsx(Badge, { tone: "accent", children: entity.type }), _jsx(ConfirmButton, { className: "btn btn-ghost btn-icon", prompt: `Delete ${entity.name}? Its relationships are removed; memories stay.`, onConfirm: () => del(`/entities/${id}`).then(onClose).catch(onError), children: _jsx(Trash2, { size: 14 }) })] }), children: [_jsxs("div", { className: "row wrap small", children: [_jsx("span", { className: "muted", children: "Also known as:" }), aliases === null ? (_jsxs(_Fragment, { children: [_jsx("span", { dir: "auto", children: entity.aliases.join(", ") || "—" }), _jsx("button", { className: "link small", onClick: () => setAliases(entity.aliases.join(", ")), children: "edit" })] })) : (_jsxs("form", { className: "row grow", onSubmit: async (e) => { e.preventDefault(); await put(`/entities/${id}`, { aliases: aliases.split(",").map((a) => a.trim()).filter(Boolean) }).catch(onError); setAliases(null); void p.reload(); }, children: [_jsx("input", { className: "input grow", dir: "auto", value: aliases, onChange: (e) => setAliases(e.target.value), placeholder: "e.g. \u0623\u062D\u0645\u062F, Ahmed M." }), _jsx("button", { className: "btn btn-sm", children: "Save" })] }))] }), _jsx("div", { className: "hud-label", children: "Relationships" }), relations.length === 0 ? _jsx("div", { className: "small muted", children: "None recorded." }) : (_jsx("ul", { className: "list", children: relations.map((r) => (_jsxs("li", { children: [_jsxs("span", { className: "small", dir: "auto", children: [r.fromName, " ", _jsx("span", { className: "mono accent", children: r.type.replace(/_/g, " ") }), " ", r.toName] }), _jsx("button", { className: "btn btn-ghost btn-icon", "aria-label": "Remove relationship", onClick: () => del(`/relations/${r.id}`).then(() => p.reload()).catch(onError), children: _jsx(X, { size: 13 }) })] }, r.id))) })), _jsxs("form", { className: "row wrap", onSubmit: async (e) => { e.preventDefault(); try {
                    await post("/relations", { from: entity.name, fromType: entity.type, relation: rel.relation, to: rel.to, toType: rel.toType });
                    setRel({ ...rel, to: "" });
                }
                catch (err) {
                    onError(err);
                } }, children: [_jsx("span", { className: "small", dir: "auto", children: entity.name }), _jsx("select", { className: "input input-sm", value: rel.relation, onChange: (e) => setRel({ ...rel, relation: e.target.value }), "aria-label": "Relationship", children: ["works_at", "manages", "reports_to", "client_of", "member_of", "owns", "partner_of", "related_to"].map((x) => _jsx("option", { value: x, children: x.replace(/_/g, " ") }, x)) }), _jsx("input", { className: "input input-sm grow", style: { minWidth: 140 }, dir: "auto", placeholder: "who / what", value: rel.to, onChange: (e) => setRel({ ...rel, to: e.target.value }) }), _jsx("select", { className: "input input-sm", value: rel.toType, onChange: (e) => setRel({ ...rel, toType: e.target.value }), "aria-label": "Type", children: ENTITY_TYPES.map((t) => _jsx("option", { children: t }, t)) }), _jsxs("button", { className: "btn btn-sm", disabled: !rel.to.trim(), children: [_jsx(Plus, { size: 13 }), " Link"] })] }), _jsx("div", { className: "hud-label", children: "What JARVIS knows" }), memories.length === 0 ? _jsx("div", { className: "small muted", children: "No memories linked yet." }) : (_jsx("ul", { className: "list", children: memories.map((m) => (_jsxs("li", { children: [_jsx("span", { className: "small grow", dir: "auto", children: m.content }), _jsx(Badge, { tone: m.isConfirmed ? "good" : "warn", children: m.isConfirmed ? "fact" : "unconfirmed" })] }, m.id))) })), (tasks.length > 0 || reminders.length > 0) && (_jsxs(_Fragment, { children: [_jsx("div", { className: "hud-label", children: "Related work" }), _jsxs("ul", { className: "list", children: [tasks.map((t) => _jsxs("li", { children: [_jsx("span", { className: "small grow", dir: "auto", children: t.title }), _jsx(Badge, { children: t.state.replace("_", " ") })] }, t.id)), reminders.map((r) => _jsxs("li", { children: [_jsxs("span", { className: "small grow", dir: "auto", children: ["\u23F0 ", r.text] }), _jsx("span", { className: "meta", dir: "ltr", children: formatTime(r.dueAt) })] }, r.id))] })] }))] }));
}
